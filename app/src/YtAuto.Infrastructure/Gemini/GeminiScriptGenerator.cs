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

    /// <summary>Model router: zanjirdagi modellar, limit/mavjudlik holati va sarf (AppServices faylga yozadigan bilan almashtiradi).</summary>
    public GeminiModelRouter Router { get; set; } = new();

    /// <summary>So'rovda model zanjiri berilmasa: bitta model (eski sozlama) yoki standart zanjir.</summary>
    internal static IReadOnlyList<string> Chain(IReadOnlyList<string>? models, string? single) =>
        new GeminiSettings { Models = (models ?? Array.Empty<string>()).ToList(), Model = single ?? "" }.ModelChain(GeminiModelRouter.DefaultChain);

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
        var models = Chain(req.Models, req.Model);
        string factSheet = ""; var grounded = new List<SourceRef>();
        if (req.UseSearchGrounding)
        {
            try
            {
                (factSheet, grounded) = await FactSheetAsync(keys, models, req, log, ct).ConfigureAwait(false);
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
        var (json, raw, model) = await CallAsync(keys, models, GeminiTask.Script, body, TimeSpan.FromSeconds(240), log, ct).ConfigureAwait(false);
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
        var models = Chain(req.Models, req.Model);
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
        var (json, _, _) = await CallAsync(keys, models, GeminiTask.Light, body, TimeSpan.FromSeconds(120), log, ct).ConfigureAwait(false);
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

    private async Task<(string, List<SourceRef>)> FactSheetAsync(List<string> keys, IReadOnlyList<string> models, ScriptRequest req, IJobLog log, CancellationToken ct)
    {
        var prompt = Fill(string.IsNullOrWhiteSpace(req.FactSheetPromptTemplate) ? DefaultPrompts.FactSheet : req.FactSheetPromptTemplate, req, "");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 4096 },
        };
        var (json, _, _) = await CallAsync(keys, models, GeminiTask.Grounded, body, TimeSpan.FromSeconds(120), log, ct).ConfigureAwait(false);
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

    /// <summary>Bitta model bilan (eski chaqiruvlar va testlar uchun).</summary>
    internal async Task<(JsonNode Json, string Raw)> CallAsync(List<string> keys, string model, JsonObject body, TimeSpan timeout, IJobLog? log, CancellationToken ct)
    {
        var (json, raw, _) = await CallAsync(keys, new[] { model }, GeminiTask.Script, body, timeout, log, ct).ConfigureAwait(false);
        return (json, raw);
    }

    /// <summary>
    /// Model router: zanjirdagi modellarni (vazifa turiga qarab tartiblangan) navbat bilan, har birini barcha kalitlar bilan sinaydi.
    /// 429 - shu kalit+model juftligi sovutiladi (kunlik limit - Tinch okeani yarim tunigacha) va keyingi kalit/modelga o'tiladi;
    /// 404 - model bu kalitda yo'q, 400 - model bu vazifani qila olmaydi, 5xx/tarmoq - model hozir javob bermayapti: keyingi model.
    /// Hammasi limitda bo'lsa - Configuration xatosi (ish to'xtaydi, eng yaqin tiklanish vaqti aytiladi).
    /// </summary>
    internal async Task<(JsonNode Json, string Raw, string Model)> CallAsync(List<string> keys, IReadOnlyList<string> names, GeminiTask task, JsonObject body, TimeSpan timeout, IJobLog? log, CancellationToken ct)
    {
        // AI Studio nomlari ("Gemini 3.8 Flash") API id'ga Google'ning models ro'yxati orqali aylantiriladi.
        if (names.Any(n => n.Contains(' ')) && !Router.CatalogFresh) await RefreshCatalogAsync(keys, log, ct).ConfigureAwait(false);
        var keyIds = keys.Select(GeminiModelRouter.KeyId).ToList();
        var start = Math.Clamp(Volatile.Read(ref _active), 0, keys.Count - 1);
        PipelineException? lastAuth = null, lastOther = null; string? lastLimit = null; int limited = 0;
        var badKeys = new HashSet<int>(); var skipped = new List<string>(); var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogRetried = false;
        List<(string Id, string Display)> order;
    again:
        var (chain, missing) = Router.Resolve(names);
        Router.SetChain(chain, keys.Count);
        if (missing.Count > 0) log?.Debug($"Gemini: bu kalitlarda yo'q modellar o'tkazib yuborildi: {string.Join(", ", missing)}.", "Script");
        order = GeminiModelRouter.Order(chain, task);
        string? first = null;
        foreach (var (model, display) in order)
        {
            if (!tried.Add(model)) continue;
            if (!Router.ModelUsable(model, task, out var why)) { skipped.Add($"{display}: {why}"); continue; }
            first ??= display;
            var modelFailed = false;
            for (int i = 0; i < keys.Count && !modelFailed; i++)
            {
                var idx = (start + i) % keys.Count;
                if (badKeys.Contains(idx)) continue;
                if (!Router.PairReady(keyIds[idx], model, out _)) { limited++; continue; }
                try
                {
                    var r = await CallOnceAsync(keys[idx], model, body, timeout, ct).ConfigureAwait(false);
                    Volatile.Write(ref _active, idx);
                    Router.Success(keyIds[idx], model, display, r.Json["usageMetadata"]?["totalTokenCount"]?.GetValue<long>() ?? 0, keys.Count);
                    if (first != display) log?.Info($"Gemini: {first} ishlatib bo'lmadi, so'rov {display} bilan bajarildi.", "Script");
                    return (r.Json, r.Raw, model);
                }
                catch (GeminiRateLimitException e)
                {
                    limited++; lastLimit = e.Message;
                    Router.RateLimited(keyIds[idx], model, display, e, keyIds);
                    var why2 = e.PerDay ? "kunlik limiti tugadi (429)" : "limiti tugadi (429)";
                    if (keys.Count > 1)
                        log?.Warn(i + 1 < keys.Count ? $"Gemini {idx + 1}-kaliti {display} uchun {why2}, {(idx + 1) % keys.Count + 1}-kalit bilan davom etiladi."
                                                     : $"Gemini {idx + 1}-kaliti ham {display} uchun {why2}.", "Script");
                    else log?.Warn($"Gemini {display}: {why2}, keyingi model sinaladi.", "Script");
                }
                catch (PipelineException e) when (e.Code == "gemini_auth")
                {
                    lastAuth = e; badKeys.Add(idx);
                    if (i + 1 < keys.Count) log?.Warn($"Gemini {idx + 1}-kaliti yaroqsiz, {(idx + 1) % keys.Count + 1}-kalit bilan davom etiladi.", "Script");
                }
                catch (PipelineException e) when (e.Code == "gemini_model")
                {
                    lastOther = e; modelFailed = true;
                    if (!catalogRetried && !Router.CatalogFresh)
                    {
                        // Nom taxmin qilingan id bo'lishi mumkin (masalan -preview qo'shimchasisiz): ro'yxatni olib qayta aniqlaymiz.
                        catalogRetried = true;
                        await RefreshCatalogAsync(keys, log, ct).ConfigureAwait(false);
                        if (Router.CatalogFresh) { tried.Remove(model); goto again; }
                    }
                    Router.MarkDown(model, display, TimeSpan.FromHours(6), "bu kalitda mavjud emas", keys.Count);
                    log?.Warn($"Gemini {display} topilmadi, keyingi model sinaladi.", "Script");
                }
                catch (PipelineException e) when (e.Code == "gemini_request")
                {
                    lastOther = e; modelFailed = true;
                    Router.MarkUnsupported(model, task);
                    log?.Warn($"Gemini {display} bu so'rovni qabul qilmadi ({e.Message}); keyingi model sinaladi.", "Script");
                }
                catch (PipelineException e) when (e.Code is "gemini_server" or "gemini_timeout" or "gemini_network" or "gemini_empty")
                {
                    lastOther = e; modelFailed = true;
                    log?.Warn($"Gemini {display} hozir javob bermayapti ({e.Message}); keyingi model sinaladi.", "Script");
                }
            }
        }
        if (badKeys.Count == keys.Count && lastAuth is not null) throw lastAuth;
        if (limited > 0 && lastOther is not { Category: ErrorCategory.Transient })
        {
            var at = Router.EarliestReady(keyIds, order.Select(o => o.Id));
            var when = at is { } t ? $" Eng yaqin tiklanish: {t.ToLocalTime():HH:mm}." : "";
            throw PipelineException.Config("gemini_quota_exhausted",
                $"Barcha Gemini modellari ({order.Count} ta) va {(keys.Count == 1 ? "kalitning" : $"{keys.Count} ta kalitning")} limiti tugadi (429), ish to'xtatildi.{when} " +
                $"Botda yana kalit qo'shing yoki limit tiklanishini kuting. {lastLimit}");
        }
        if (lastOther is not null) throw lastOther;
        if (lastAuth is not null) throw lastAuth;
        throw PipelineException.Config("gemini_model", order.Count == 0
            ? $"Sozlangan Gemini modellaridan birortasi bu kalitda mavjud emas: {string.Join(", ", names)}."
            : "Ishlaydigan Gemini modeli qolmadi: " + string.Join("; ", skipped));
    }

    /// <summary>Google'ning models ro'yxati (generateContent qiladiganlari): nom -> API id aniqlash uchun. Xato bo'lsa - nomlardan id yasaladi.</summary>
    private async Task RefreshCatalogAsync(List<string> keys, IJobLog? log, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            try
            {
                var list = await ListModelsAsync(key, ct).ConfigureAwait(false);
                if (list.Count > 0) { Router.SetCatalog(list); return; }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            { log?.Debug("Gemini models ro'yxati olinmadi: " + e.Message, "Script"); }
        }
    }

    internal async Task<List<(string Id, string Display)>> ListModelsAsync(string key, CancellationToken ct)
    {
        var all = new List<(string, string)>(); string? page = null;
        for (int n = 0; n < 10; n++)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{Base}models?pageSize=1000" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
            msg.Headers.Add("x-goog-api-key", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw MapError(resp.StatusCode, raw, "models");
            var j = JsonNode.Parse(raw);
            foreach (var m in (j?["models"] as JsonArray) ?? new JsonArray())
            {
                var methods = (m?["supportedGenerationMethods"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "") ?? Array.Empty<string>();
                if (!methods.Contains("generateContent")) continue;
                var id = m?["name"]?.GetValue<string>()?.Replace("models/", "") ?? "";
                if (id.Length > 0) all.Add((id, m?["displayName"]?.GetValue<string>() ?? id));
            }
            page = j?["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(page)) break;
        }
        return all;
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
            429 => GeminiRateLimitException.Parse(raw, $"Gemini limiti ({model}): {msg}"),
            >= 500 => PipelineException.Transient("gemini_server", $"Gemini server xatosi {(int)code}: {msg}"),
            401 or 403 => PipelineException.Config("gemini_auth", $"Gemini API kaliti yaroqsiz yoki ruxsat yo'q: {msg}"),
            404 => PipelineException.Config("gemini_model", $"Gemini modeli topilmadi ({model}): {msg}"),
            400 when msg.Contains("API key", StringComparison.OrdinalIgnoreCase) => PipelineException.Config("gemini_auth", $"Gemini API kaliti yaroqsiz: {msg}"),
            _ => PipelineException.Config("gemini_request", $"Gemini so'rovi rad etildi ({(int)code}): {msg}"),
        };
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

    /// <summary>Integratsiya tekshiruvi: har kalit ishlaydimi va zanjirdagi qaysi modellar mavjud (generatsiya qilmaydi).</summary>
    public async Task<(bool Ok, string Message, List<string> Models)> CheckAsync(IReadOnlyList<string> chain, CancellationToken ct)
    {
        List<string> keys;
        try { keys = Keys(); } catch (PipelineException e) { return (false, e.Message, new()); }
        List<(string Id, string Display)>? catalog = null; var problems = new List<string>(); int working = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            try
            {
                var list = await ListModelsAsync(keys[i], ct).ConfigureAwait(false);
                working++; catalog ??= list;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            { problems.Add((keys.Count > 1 ? $"{i + 1}-kalit: " : "") + (e is PipelineException ? e.Message : "Gemini'ga ulanib bo'lmadi: " + e.Message)); }
        }
        if (working == 0 || catalog is null) return (false, string.Join(" | ", problems), new());
        Router.SetCatalog(catalog);
        var (found, missing) = Router.Resolve(chain);
        var head = keys.Count > 1 ? $"{working}/{keys.Count} kalit ishlaydi" : "Kalit ishlaydi";
        var tail = problems.Count > 0 ? " (" + string.Join(" | ", problems) + ")" : "";
        var msg = found.Count == 0
            ? $"{head}, lekin zanjirdagi modellardan birortasi ro'yxatda yo'q: {string.Join(", ", chain)}.{tail}"
            : $"{head}, zanjirda {found.Count}/{chain.Count} model mavjud: {string.Join(", ", found.Select(f => f.Display))}" +
              (missing.Count > 0 ? $"; yo'q: {string.Join(", ", missing)}" : "") + $".{tail}";
        return (found.Count > 0, msg, found.Select(f => f.Id).ToList());
    }
}

