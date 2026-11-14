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

    private string Key() => _secrets.Get(SecretNames.GeminiApiKey) is { Length: > 0 } k ? k
        : throw PipelineException.Config("gemini_key_missing", "Gemini API kaliti kiritilmagan (Sozlamalar → Gemini).");

    public async Task<ScriptResult> GenerateAsync(ScriptRequest req, IJobLog log, CancellationToken ct)
    {
        var key = Key();
        var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model.Trim();
        string factSheet = ""; var grounded = new List<SourceRef>();
        if (req.UseSearchGrounding)
        {
            try
            {
                (factSheet, grounded) = await FactSheetAsync(key, model, req, ct).ConfigureAwait(false);
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
                ["responseSchema"] = ScriptSchema(),
            },
        };
        var (json, raw) = await CallAsync(key, model, body, TimeSpan.FromSeconds(240), ct).ConfigureAwait(false);
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

    private async Task<(string, List<SourceRef>)> FactSheetAsync(string key, string model, ScriptRequest req, CancellationToken ct)
    {
        var prompt = Fill(string.IsNullOrWhiteSpace(req.FactSheetPromptTemplate) ? DefaultPrompts.FactSheet : req.FactSheetPromptTemplate, req, "");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 4096 },
        };
        var (json, _) = await CallAsync(key, model, body, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
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
            .Replace("{format}", r.Format).Replace("{fact_sheet}", factSheet);
    }

    private async Task<(JsonNode Json, string Raw)> CallAsync(string key, string model, JsonObject body, TimeSpan timeout, CancellationToken ct)
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
            429 => PipelineException.Transient("gemini_rate_limit", $"Gemini limiti: {msg}"),
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

    internal static JsonObject ScriptSchema()
    {
        JsonObject Str() => new() { ["type"] = "STRING" };
        JsonObject Arr(JsonObject items) => new() { ["type"] = "ARRAY", ["items"] = items };
        var source = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject { ["title"] = Str(), ["url"] = Str() },
            ["required"] = new JsonArray("title", "url"),
        };
        return new JsonObject
        {
            ["type"] = "OBJECT",
            // Asosiy shartnoma: subtitle_text (ovoz + subtitr) va youtube_title. Qolganlari ixtiyoriy.
            ["properties"] = new JsonObject
            {
                ["subtitle_text"] = Str(), ["youtube_title"] = Str(), ["youtube_description"] = Str(), ["tags"] = Arr(Str()),
                ["sources"] = Arr(source),
            },
            ["required"] = new JsonArray("subtitle_text", "youtube_title"),
            ["propertyOrdering"] = new JsonArray("youtube_title", "subtitle_text", "youtube_description", "tags", "sources"),
        };
    }

    /// <summary>Integratsiya tekshiruvi: kalit va model mavjudligini tekshiradi (generatsiya qilmaydi).</summary>
    public async Task<(bool Ok, string Message, List<string> Models)> CheckAsync(string model, CancellationToken ct)
    {
        string key;
        try { key = Key(); } catch (PipelineException e) { return (false, e.Message, new()); }
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
            var has = models.Contains(model, StringComparer.OrdinalIgnoreCase);
            return (has, has ? $"Kalit ishlaydi, model mavjud: {model}" : $"Kalit ishlaydi, lekin '{model}' modeli ro'yxatda yo'q.", models);
        }
        catch (Exception e) { return (false, "Gemini'ga ulanib bo'lmadi: " + e.Message, new()); }
    }
}
