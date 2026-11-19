using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;

namespace YtAuto.Infrastructure.Gemini;

/// <summary>
/// Gemini REST API (generateContent). Ssenariy - responseSchema bilan strukturali JSON.
/// Hozirgi faktlar uchun (sozlamada yoqilgan bo'lsa) avval Google Search grounding bilan alohida fakt varag'i olinadi;
/// manbalar faqat API'ning groundingMetadata'sidan olinadi - model "o'ylab topgan" havolalar tasdiqlangan deb ko'rsatilmaydi.
/// Gemini oldingi suhbatni eslab qolmaydi: har chaqiruv to'liq prompt bilan.
/// </summary>
public sealed class GeminiScriptGenerator : IScriptGenerator
{
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private const string Base = "https://generativelanguage.googleapis.com/v1beta/";

    public GeminiScriptGenerator(ISecretStore secrets, HttpClient? http = null)
    {
        _secrets = secrets;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Online bazadan (bot orqali kiritilgan) kalitlar, tartib bilan. Bo'lsa lokal DPAPI kalitidan ustun turadi.</summary>
    public Func<IReadOnlyList<string>?>? RemoteKeys { get; set; }

    /// <summary>false bo'lsa (mehmon hisobi vazifasi) kompyuterning lokal DPAPI kaliti ishlatilmaydi.</summary>
    public Func<bool>? LocalKeyAllowed { get; set; }

    /// <summary>Oxirgi muvaffaqiyatli kalit: keyingi chaqiruvlar limiti tugagan kalitni qayta-qayta urmasdan shundan boshlaydi.</summary>
    private int _active;

    /// <summary>
    /// Model router zaxirasi (asosiy model limitga tushsa, band yoki topilmasa shu tartibda sinaladi). null - zaxirasiz.
    /// Bepul limitlar har model uchun alohida: o'sha kalit boshqa modelda odatda ishlaydi va ish to'xtamaydi.
    /// </summary>
    public Func<IReadOnlyList<string>?>? FallbackModels { get; set; }

    /// <summary>Limitga tushgan model qachongacha chetlab o'tiladi (Gemini retryDelay yoki 60 s; kunlik limitda 1 soat).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bu kalitlarda mavjud bo'lmagan model (404) - 6 soat so'ralmaydi.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Router tartibi: asosiy model, so'ng zaxiralar; limitdagilar oxiriga, mavjud emaslar tashlanadi.</summary>
    internal List<string> RouteModels(string primary, DateTimeOffset now)
    {
        IReadOnlyList<string>? fb = null;
        try { fb = FallbackModels?.Invoke(); } catch { }
        var all = new[] { primary }.Concat(fb ?? Array.Empty<string>()).Select(m => m?.Trim() ?? "").Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var avail = all.Where(m => !(_missing.TryGetValue(m, out var until) && until > now)).ToList();
        if (avail.Count == 0) avail = all;
        DateTimeOffset Cool(string m) => _cooldown.TryGetValue(m, out var u) ? u : DateTimeOffset.MinValue;
        return avail.Where(m => Cool(m) <= now).Concat(avail.Where(m => Cool(m) > now).OrderBy(Cool)).ToList();
    }

    /// <summary>Kamida bitta Gemini kaliti bormi (tarmoqqa chiqmaydi; kalit ishlashini <see cref="CheckAsync"/> tekshiradi).</summary>
    public int KeyCount { get { try { return Keys().Count; } catch (PipelineException) { return 0; } } }

    private List<string> Keys()
    {
        IReadOnlyList<string>? remote = null;
        try { remote = RemoteKeys?.Invoke(); } catch { }
        var keys = (remote ?? Array.Empty<string>()).Select(k => k?.Trim() ?? "").Where(k => k.Length > 0).Distinct().ToList();
        if (keys.Count > 0) return keys;
        var local = true;
        try { local = LocalKeyAllowed?.Invoke() ?? true; } catch { }
        return local && _secrets.Get(SecretNames.GeminiApiKey) is { Length: > 0 } k ? new List<string> { k }
            : throw PipelineException.Config("gemini_key_missing", "Gemini API kaliti kiritilmagan (Telegram bot → Sozlamalar → Gemini).");
    }

    /// <summary>Boshqa Gemini chaqiruvlari (vision) uchun: shu kalitlar navbati va limit/429 almashinuvi qayta ishlatiladi.</summary>
    internal List<string> KeysForCall() => Keys();
    internal static string TextOf(JsonNode json) => ExtractText(json);

    public async Task<ScriptResult> GenerateAsync(ScriptRequest req, IJobLog log, CancellationToken ct)
    {
        var keys = Keys();
        var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model.Trim();
        string factSheet = ""; var grounded = new List<SourceRef>();
        if (req.UseSearchGrounding)
        {
            try
            {
                (factSheet, grounded) = await FactSheetAsync(keys, model, req, log, ct).ConfigureAwait(false);
                log.Info($"Fakt varag'i Google Search grounding bilan olindi: {grounded.Count} manba.", "Script");
            }
            catch (PipelineException e) when (e.Category == ErrorCategory.Transient) { throw; }
            catch (Exception e)
            {
                log.Warn($"Grounding (Google Search) ishlamadi, fakt varag'isiz davom etiladi; manbalar tasdiqlanmagan hisoblanadi: {e.Message}", "Script");
            }
        }
        else if (req.Notes.Contains("[FACT SHEET FROM PREVIOUS STEP]")) factSheet = "(see notes)";

        var prompt = Fill(req.PromptTemplate, req, factSheet.Length == 0 ? "(empty - no search-grounded facts available; avoid recent numbers and specific statistics)" : factSheet);
        prompt += MetadataInstruction(req.WriteDescription, req.WriteTags);
        if (req.PreviousProblems.Count > 0)
            prompt += "\n\nYour previous answer was rejected by an automatic check. Fix ALL of these problems:\n- " + string.Join("\n- ", req.PreviousProblems);

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = req.Temperature,
                ["maxOutputTokens"] = 16384,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = ScriptSchema(req.WriteDescription, req.WriteTags),
            },
        };
        var (json, raw) = await CallAsync(keys, model, body, TimeSpan.FromSeconds(240), log, ct).ConfigureAwait(false);
        var text = ExtractText(json);
        VideoScript script;
        try { script = VideoScript.FromJson(text); }
        catch (Exception e) { throw PipelineException.Transient("gemini_bad_json", $"Gemini javobi JSON schema'ga mos emas: {e.Message}"); }
        script.GroundedSources = grounded;
        script.FactSheet = factSheet;
        var usage = json["usageMetadata"];
        return new ScriptResult
        {
            Script = script, RawModelOutput = raw, ModelUsed = model,
            PromptTokens = usage?["promptTokenCount"]?.GetValue<int>() ?? 0,
            OutputTokens = usage?["candidatesTokenCount"]?.GetValue<int>() ?? 0,
        };
    }

    /// <summary>
    /// Promptga (foydalanuvchi o'zgartirgan bo'lsa ham) qo'shiladigan aniq ko'rsatma: kanalda standart tavsif/taglar bormi.
    /// Bor bo'lsa - yozmaydi (standart ishlatiladi); yo'q bo'lsa - tavsif va aynan 5 ta tag yozadi.
    /// </summary>
    internal static string MetadataInstruction(bool writeDescription, bool writeTags)
    {
        var sb = new StringBuilder("\n\nYouTube description and tags for this channel:\n");
        sb.Append(writeDescription
            ? "- The channel has NO default description: write youtube_description (2 short paragraphs summarising this video, no < or > characters).\n"
            : "- The channel has its own default description: do NOT write youtube_description.\n");
        sb.Append(writeTags
            ? $"- The channel has NO default tags: write exactly {DefaultPrompts.TagsPerVideo} tags for this video (1-3 words each, no # sign, most specific first).\n"
            : "- The channel has its own default tags: do NOT write tags.\n");
        return sb.ToString();
    }

    public const string MetadataPrompt = """
You write YouTube metadata for a finished video. The text below is exactly what is said in the video; do not change it.
Video title: {title}
Language: {language}

{instructions}
SPOKEN TEXT:
{text}
""";

    /// <summary>Foydalanuvchining tayyor matni uchun faqat tavsif va/yoki 5 ta tag (matn va title o'zgartirilmaydi).</summary>
    public async Task<MetadataResult> DescribeAsync(MetadataRequest req, IJobLog log, CancellationToken ct)
    {
        if (!req.WriteDescription && !req.WriteTags) return new MetadataResult();
        var keys = Keys();
        var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model.Trim();
        var prompt = MetadataPrompt.Replace("{title}", req.Title.Trim()).Replace("{language}", req.Language)
            .Replace("{instructions}", MetadataInstruction(req.WriteDescription, req.WriteTags).TrimStart()).Replace("{text}", req.Text.Trim());
        var schema = ScriptSchema(req.WriteDescription, req.WriteTags);
        var props = (JsonObject)schema["properties"]!;
        props.Remove("subtitle_text"); props.Remove("youtube_title"); props.Remove("sources");
        schema["required"] = new JsonArray(props.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        schema["propertyOrdering"] = new JsonArray(props.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.4, ["maxOutputTokens"] = 4096, ["responseMimeType"] = "application/json", ["responseSchema"] = schema,
            },
        };
        var (json, _) = await CallAsync(keys, model, body, TimeSpan.FromSeconds(120), log, ct).ConfigureAwait(false);
        try
        {
            var n = JsonNode.Parse(ExtractText(json));
            return new MetadataResult
            {
                Description = req.WriteDescription ? n?["youtube_description"]?.GetValue<string>()?.Trim() ?? "" : "",
                Tags = req.WriteTags ? ScriptValidator.CleanTags((n?["tags"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? ""), DefaultPrompts.TagsPerVideo) : new(),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw PipelineException.Transient("gemini_bad_json", "Tavsif/tag javobi JSON emas: " + e.Message); }
    }

    private async Task<(string, List<SourceRef>)> FactSheetAsync(List<string> keys, string model, ScriptRequest req, IJobLog log, CancellationToken ct)
    {
        var prompt = Fill(string.IsNullOrWhiteSpace(req.FactSheetPromptTemplate) ? DefaultPrompts.FactSheet : req.FactSheetPromptTemplate, req, "");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 4096 },
        };
        var (json, _) = await CallAsync(keys, model, body, TimeSpan.FromSeconds(120), log, ct).ConfigureAwait(false);
        var text = ExtractText(json);
        var sources = new List<SourceRef>();
        var chunks = json["candidates"]?[0]?["groundingMetadata"]?["groundingChunks"] as JsonArray;
        if (chunks is null || chunks.Count == 0) throw new InvalidOperationException("javobda groundingMetadata yo'q (model qidiruvdan foydalanmadi)");
        foreach (var ch in chunks)
        {
            var uri = ch?["web"]?["uri"]?.GetValue<string>(); var title = ch?["web"]?["title"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(uri)) continue;
            var final = await ResolveRedirectAsync(uri, ct).ConfigureAwait(false);
            if (sources.All(s => s.Url != final)) sources.Add(new SourceRef { Title = title, Url = final });
            if (sources.Count >= 10) break;
        }
        var sb = new StringBuilder(text.Trim());
        sb.Append("\n\nSources found by search (only these may be cited):\n");
        foreach (var s in sources) sb.Append("- ").Append(s.Title).Append(": ").Append(s.Url).Append('\n');
        return (sb.ToString(), sources);
    }

    /// <summary>Grounding havolalari vaqtinchalik redirect: haqiqiy sahifa manzilini olamiz (sahifa tarkibi yuklanmaydi).</summary>
    private async Task<string> ResolveRedirectAsync(string uri, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                return loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(uri), loc).ToString();
        }
        catch { }
        return uri;
    }

    internal static string Fill(string template, ScriptRequest r, string factSheet)
    {
        string lengthRule;
        if (r.TargetDurationSec > 0)
        {
            var words = (int)Math.Round(r.TargetDurationSec * LanguageNames.WordsPerSecond(r.Language));
            lengthRule = $"Target length: about {r.TargetDurationSec} seconds of narration, which is about {words} spoken words.";
        }
        else
        {
            // Davomiylik dinamik: eski promptlardagi qat'iy uzunlik qatori ham dinamik qoidaga almashtiriladi.
            lengthRule = DefaultPrompts.DynamicLengthRule;
            template = template.Replace(DefaultPrompts.LegacyLengthLine, "{length_rule}");
        }
        return template.Replace("{topic}", r.Topic).Replace("{notes}", string.IsNullOrWhiteSpace(r.Notes) ? "(none)" : r.Notes)
            .Replace("{language}", r.Language).Replace("{language_name}", LanguageNames.Of(r.Language))
            .Replace("{length_rule}", lengthRule)
            .Replace("{duration_sec}", r.TargetDurationSec > 0 ? r.TargetDurationSec.ToString() : "as many as the topic needs")
            .Replace("{target_words}", "as many as needed")
            .Replace("{format}", r.Format).Replace("{fact_sheet}", factSheet)
            .Replace(Core.Pipeline.ChannelPrompt.Placeholder, ""); // kontekst JobRunner'da qo'yiladi; qolib ketgan placeholder modelga bormaydi
    }

    /// <summary>
    /// Model router: avval asosiy model barcha kalitlar bilan; hammasi limitda (429), model band (5xx / vaqt tugashi), topilmasa (404)
    /// yoki zaxira model so'rovni qabul qilmasa - keyingi model. Barcha Gemini chaqiruvlari (ssenariy, fakt varag'i, tavsif,
    /// shablon/rasm tahlili, moslik tekshiruvi) shu yerdan o'tadi. Hammasi limitda bo'lsagina ish to'xtaydi (NeedsAttention).
    /// </summary>
    internal async Task<(JsonNode Json, string Raw)> CallAsync(List<string> keys, string model, JsonObject body, TimeSpan timeout, IJobLog? log, CancellationToken ct)
    {
        var chain = RouteModels(model, DateTimeOffset.UtcNow);
        PipelineException? limitErr = null, lastErr = null;
        var problems = new List<string>();
        for (int i = 0; i < chain.Count; i++)
        {
            var m = chain[i];
            string why;
            try
            {
                var r = await CallWithKeysAsync(keys, m, body, timeout, log, ct).ConfigureAwait(false);
                _cooldown.TryRemove(m, out _);
                if (!string.Equals(m, model, StringComparison.OrdinalIgnoreCase))
                    log?.Info($"Gemini javobi zaxira modeldan: «{m}» ({string.Join("; ", problems)}).", "Gemini");
                return r;
            }
            catch (PipelineException e) when (e.Code == "gemini_quota_exhausted")
            {
                limitErr ??= e;
                var daily = e.Data["daily"] is true;
                var wait = e.Data["retryMs"] is int ms ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 5_000, 600_000)) : TimeSpan.FromSeconds(60);
                _cooldown[m] = DateTimeOffset.UtcNow + (daily ? TimeSpan.FromHours(1) : wait);
                why = daily ? "kunlik limiti tugadi (429)" : "limiti tugadi (429)";
            }
            catch (PipelineException e) when (e.Code == "gemini_model")
            {
                _missing[m] = DateTimeOffset.UtcNow.AddHours(6); lastErr = e; why = "mavjud emas (404)";
            }
            catch (PipelineException e) when (e.Code is "gemini_server" or "gemini_timeout")
            {
                lastErr = e; why = e.Code == "gemini_timeout" ? "vaqtida javob bermadi" : "band (server xatosi)";
            }
            catch (PipelineException e) when (e.Code == "gemini_request" && !string.Equals(m, model, StringComparison.OrdinalIgnoreCase))
            {
                lastErr ??= e; why = "so'rovni qabul qilmadi";
            }
            problems.Add($"{m}: {why}");
            if (i + 1 < chain.Count) log?.Warn($"Gemini «{m}» modeli {why} — «{chain[i + 1]}» modeliga o'tildi.", "Gemini");
        }
        if (limitErr is not null)
            throw PipelineException.Config("gemini_quota_exhausted", chain.Count == 1 ? limitErr.Message
                : $"Barcha kalitlar {chain.Count} ta modelda ham limitga tushdi (429), ish to'xtatildi: {string.Join("; ", problems)}. Botda (Sozlamalar → Gemini) yangi kalit yoki zaxira model qo'shing, yoki limit tiklanishini kuting.");
        throw lastErr ?? PipelineException.Transient("gemini_unavailable", $"Gemini javob bermadi: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// Bitta model: kalitlarni navbat bilan sinaydi - 429 (limit tugadi) yoki kalit yaroqsiz bo'lsa keyingisiga o'tadi.
    /// Hammasi limitda bo'lsa - gemini_quota_exhausted (Data: retryMs - eng qisqa kutish, daily - kunlik limit).
    /// </summary>
    private async Task<(JsonNode Json, string Raw)> CallWithKeysAsync(List<string> keys, string model, JsonObject body, TimeSpan timeout, IJobLog? log, CancellationToken ct)
    {
        var start = Math.Clamp(Volatile.Read(ref _active), 0, keys.Count - 1);
        PipelineException? lastAuth = null; string? lastLimit = null; int limited = 0;
        int? retryMs = null; var daily = false;
        for (int i = 0; i < keys.Count; i++)
        {
            var idx = (start + i) % keys.Count;
            try
            {
                var r = await CallOnceAsync(keys[idx], model, body, timeout, ct).ConfigureAwait(false);
                Volatile.Write(ref _active, idx);
                return r;
            }
            catch (PipelineException e) when (e.Code is "gemini_rate_limit" or "gemini_auth")
            {
                if (e.Code == "gemini_rate_limit")
                {
                    limited++; lastLimit = e.Message;
                    if (e.Data["retryMs"] is int r && (retryMs is null || r < retryMs)) retryMs = r;
                    daily |= e.Data["daily"] is true;
                }
                else lastAuth = e;
                var why = e.Code == "gemini_rate_limit" ? "limiti tugadi (429)" : "yaroqsiz";
                if (i + 1 < keys.Count)
                    log?.Warn($"Gemini {idx + 1}-kaliti {why}, {(idx + 1) % keys.Count + 1}-kalit bilan davom etiladi.", "Script");
                else if (keys.Count > 1)
                    log?.Warn($"Gemini {idx + 1}-kaliti ham {why}: boshqa kalit qolmadi.", "Script");
            }
        }
        if (limited == 0 && lastAuth is not null) throw lastAuth;
        var quota = PipelineException.Config("gemini_quota_exhausted", keys.Count == 1
            ? $"Gemini kalitining limiti tugadi (429), ish to'xtatildi. Botda (Sozlamalar → Gemini) yana kalit qo'shing yoki limit tiklanishini kuting. {lastLimit}"
            : $"Barcha {keys.Count} ta Gemini kalitining limiti tugadi (429), ish to'xtatildi. Botda yangi kalit qo'shing yoki limit tiklanishini kuting. {lastLimit}");
        if (retryMs is int rm) quota.Data["retryMs"] = rm;
        quota.Data["daily"] = daily;
        throw quota;
    }

    /// <summary>429 javobidan: qancha kutish (RetryInfo.retryDelay) va kunlik limitmi (QuotaFailure quotaId ...PerDay...).</summary>
    internal static (int? RetryMs, bool Daily) RateInfo(string raw)
    {
        try
        {
            if (JsonNode.Parse(raw)?["error"]?["details"] is not JsonArray details) return (null, false);
            int? ms = null; var daily = false;
            foreach (var d in details)
            {
                if (d?["retryDelay"]?.GetValue<string>() is { } delay && delay.EndsWith('s')
                    && double.TryParse(delay[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    ms = (int)Math.Ceiling(sec * 1000);
                if (d?["violations"] is JsonArray v && v.Any(x => (x?["quotaId"]?.GetValue<string>() ?? "").Contains("PerDay", StringComparison.OrdinalIgnoreCase)))
                    daily = true;
            }
            return (ms, daily);
        }
        catch { return (null, false); }
    }

    private async Task<(JsonNode Json, string Raw)> CallOnceAsync(string key, string model, JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{Base}models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-goog-api-key", key);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PipelineException.Transient("gemini_timeout", $"Gemini {timeout.TotalSeconds:0}s ichida javob bermadi."); }
        catch (HttpRequestException e) { throw PipelineException.Transient("gemini_network", $"Gemini bilan tarmoq xatosi: {e.Message}", e); }
        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw MapError(resp.StatusCode, raw, model);
            var json = JsonNode.Parse(raw) ?? throw PipelineException.Transient("gemini_empty", "Gemini bo'sh javob qaytardi.");
            var block = json["promptFeedback"]?["blockReason"]?.GetValue<string>();
            if (block is not null) throw PipelineException.Quality("gemini_blocked", $"Gemini so'rovni blokladi: {block}. Mavzu yoki promptni o'zgartiring.");
            return (json, raw);
        }
    }

    private static PipelineException MapError(HttpStatusCode code, string raw, string model)
    {
        var msg = raw.Length > 400 ? raw[..400] : raw;
        try { msg = JsonNode.Parse(raw)?["error"]?["message"]?.GetValue<string>() ?? msg; } catch { }
        return (int)code switch
        {
            429 => RateLimit(raw, msg),
            >= 500 => PipelineException.Transient("gemini_server", $"Gemini server xatosi {(int)code}: {msg}"),
            401 or 403 => PipelineException.Config("gemini_auth", $"Gemini API kaliti yaroqsiz yoki ruxsat yo'q: {msg}"),
            404 => PipelineException.Config("gemini_model", $"Gemini modeli topilmadi ({model}): {msg}"),
            400 when msg.Contains("API key", StringComparison.OrdinalIgnoreCase) => PipelineException.Config("gemini_auth", $"Gemini API kaliti yaroqsiz: {msg}"),
            _ => PipelineException.Config("gemini_request", $"Gemini so'rovi rad etildi ({(int)code}): {msg}"),
        };
    }

    private static PipelineException RateLimit(string raw, string msg)
    {
        var e = PipelineException.Transient("gemini_rate_limit", $"Gemini limiti: {msg}");
        var (ms, daily) = RateInfo(raw);
        if (ms is int v) e.Data["retryMs"] = v;
        e.Data["daily"] = daily;
        return e;
    }

    private static string ExtractText(JsonNode json)
    {
        var cand = json["candidates"]?[0];
        var finish = cand?["finishReason"]?.GetValue<string>();
        var parts = cand?["content"]?["parts"] as JsonArray;
        var sb = new StringBuilder();
        if (parts is not null) foreach (var p in parts) if (p?["text"] is JsonNode t && p["thought"] is null) sb.Append(t.GetValue<string>());
        if (sb.Length == 0)
            throw PipelineException.Transient("gemini_no_text", $"Gemini matn qaytarmadi (finishReason={finish}).");
        if (finish is "MAX_TOKENS") throw PipelineException.Transient("gemini_truncated", "Gemini javobi token limitida kesildi.");
        if (finish is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT") throw PipelineException.Quality("gemini_safety", $"Gemini javobi to'xtatildi: {finish}.");
        return sb.ToString();
    }

    internal static JsonObject ScriptSchema(bool writeDescription = true, bool writeTags = true)
    {
        JsonObject Str() => new() { ["type"] = "STRING" };
        JsonObject Arr(JsonObject items) => new() { ["type"] = "ARRAY", ["items"] = items };
        var source = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject { ["title"] = Str(), ["url"] = Str() },
            ["required"] = new JsonArray("title", "url"),
        };
        var props = new JsonObject { ["subtitle_text"] = Str(), ["youtube_title"] = Str() };
        var required = new List<string> { "subtitle_text", "youtube_title" };
        var order = new List<string> { "youtube_title", "subtitle_text" };
        if (writeDescription) { props["youtube_description"] = Str(); required.Add("youtube_description"); order.Add("youtube_description"); }
        if (writeTags)
        {
            props["tags"] = new JsonObject { ["type"] = "ARRAY", ["items"] = Str(), ["minItems"] = DefaultPrompts.TagsPerVideo, ["maxItems"] = DefaultPrompts.TagsPerVideo };
            required.Add("tags"); order.Add("tags");
        }
        props["sources"] = Arr(source); order.Add("sources");
        return new JsonObject
        {
            ["type"] = "OBJECT",
            // Asosiy shartnoma: subtitle_text (ovoz + subtitr) va youtube_title. Tavsif va aynan 5 ta tag - faqat kanalda
            // standarti bo'lmasa (bo'lsa sxemada umuman yo'q: Gemini ularni yozmaydi, standart ishlatiladi).
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["propertyOrdering"] = new JsonArray(order.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        };
    }

    /// <summary>Integratsiya tekshiruvi: har kalit va model mavjudligini tekshiradi (generatsiya qilmaydi).</summary>
    public async Task<(bool Ok, string Message, List<string> Models)> CheckAsync(string model, CancellationToken ct)
    {
        List<string> keys;
        try { keys = Keys(); } catch (PipelineException e) { return (false, e.Message, new()); }
        var models = new List<string>(); var problems = new List<string>(); int working = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            var (ok, msg, list) = await CheckKeyAsync(keys[i], model, ct).ConfigureAwait(false);
            if (ok) { working++; if (models.Count == 0) models = list; }
            else problems.Add(keys.Count > 1 ? $"{i + 1}-kalit: {msg}" : msg);
        }
        if (working == 0) return (false, string.Join(" | ", problems), models);
        var has = models.Contains(model, StringComparer.OrdinalIgnoreCase);
        var head = keys.Count > 1 ? $"{working}/{keys.Count} kalit ishlaydi" : "Kalit ishlaydi";
        var tail = problems.Count > 0 ? " (" + string.Join(" | ", problems) + ")" : "";
        return (has, has ? $"{head}, model mavjud: {model}{tail}" : $"{head}, lekin '{model}' modeli ro'yxatda yo'q.{tail}", models);
    }

    private async Task<(bool Ok, string Message, List<string> Models)> CheckKeyAsync(string key, string model, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{Base}models?pageSize=200");
        msg.Headers.Add("x-goog-api-key", key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, MapError(resp.StatusCode, raw, model).Message, new());
            var models = (JsonNode.Parse(raw)?["models"] as JsonArray ?? new JsonArray())
                .Select(m => m?["name"]?.GetValue<string>()?.Replace("models/", "") ?? "")
                .Where(n => n.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)).ToList();
            return (true, "ok", models);
        }
        catch (Exception e) { return (false, "Gemini'ga ulanib bo'lmadi: " + e.Message, new()); }
    }
}
