using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YtAuto.Core.Script;

public sealed class ScriptRequest
{
    public string Topic { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Language { get; init; } = "en";
    public int TargetDurationSec { get; init; }
    public string Format { get; init; } = "";
    public string PromptTemplate { get; init; } = "";
    public string FactSheetPromptTemplate { get; init; } = "";
    public bool UseSearchGrounding { get; init; }
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.7;
    /// <summary>Oldingi urinishdagi validatsiya xatolari (qayta yozish uchun).</summary>
    public IReadOnlyList<string> PreviousProblems { get; init; } = Array.Empty<string>();
}

public sealed class SceneSpec
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("narration")] public string Narration { get; set; } = "";
    [JsonPropertyName("visual")] public string Visual { get; set; } = "";
    [JsonPropertyName("on_screen_text")] public string OnScreenText { get; set; } = "";
    [JsonPropertyName("approx_seconds")] public double ApproxSeconds { get; set; }
}

public sealed class SourceRef
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class VideoScript
{
    [JsonPropertyName("narration_text")] public string NarrationText { get; set; } = "";
    [JsonPropertyName("subtitle_text")] public string SubtitleText { get; set; } = "";
    [JsonPropertyName("scenes")] public List<SceneSpec> Scenes { get; set; } = new();
    [JsonPropertyName("on_screen_texts")] public List<string> OnScreenTexts { get; set; } = new();
    [JsonPropertyName("youtube_title")] public string YouTubeTitle { get; set; } = "";
    [JsonPropertyName("youtube_description")] public string YouTubeDescription { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("sources")] public List<SourceRef> Sources { get; set; } = new();
    [JsonPropertyName("unverified_claims")] public List<string> UnverifiedClaims { get; set; } = new();

    /// <summary>Grounding bosqichida haqiqatan topilgan manbalar (model emas, API metadata'si).</summary>
    [JsonPropertyName("grounded_sources")] public List<SourceRef> GroundedSources { get; set; } = new();
    [JsonPropertyName("fact_sheet")] public string FactSheet { get; set; } = "";
    [JsonPropertyName("sources_verified")] public bool SourcesVerified { get; set; }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static VideoScript FromJson(string json) => JsonSerializer.Deserialize<VideoScript>(json, Json)
        ?? throw new InvalidDataException("Bo'sh ssenariy JSON");
}

public sealed class ScriptResult
{
    public VideoScript Script { get; init; } = new();
    public string RawModelOutput { get; init; } = "";
    public string ModelUsed { get; init; } = "";
    public int PromptTokens { get; init; }
    public int OutputTokens { get; init; }
}

public static class LanguageNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["uz"] = "Uzbek (Latin script)", ["ru"] = "Russian", ["tr"] = "Turkish",
        ["kk"] = "Kazakh", ["de"] = "German", ["fr"] = "French", ["es"] = "Spanish", ["ar"] = "Arabic",
    };
    public static string Of(string code) => Names.TryGetValue(code ?? "", out var n) ? n : code ?? "";

    /// <summary>Taxminiy nutq tezligi (so'z/soniya) - davomiylikni baholash uchun.</summary>
    public static double WordsPerSecond(string code) => (code ?? "").ToLowerInvariant() switch
    {
        "en" => 2.5,
        "uz" or "tr" or "kk" => 1.9, // agglutinativ tillar: so'zlar uzunroq
        "ru" => 2.1,
        _ => 2.2,
    };
}

/// <summary>Ssenariyni schema va mazmun talablari bo'yicha tekshiradi.</summary>
public static class ScriptValidator
{
    private static readonly Regex StageDirection = new(
        @"\[[^\]]*\]|\((?:pause|music|sfx|sound|beat|laughs?|scene|cut|music\s+\w+)[^)]*\)|^\s*(scene|sahna|narrator|diktor|host|voice ?over|vo)\s*\d*\s*:|\*\*|^#{1,6}\s|https?://|www\.|<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Emoji = new(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]", RegexOptions.Compiled);

    public static int CountWords(string text) =>
        Regex.Matches(text ?? "", @"[\p{L}\p{N}][\p{L}\p{N}'’\-]*").Count;

    /// <summary>
    /// Oqim: subtitle_text - yagona matn: undan ovoz yaratiladi va u subtitr bo'ladi. NarrationText shu matnga tenglashtiriladi
    /// (eski formatdagi javobda faqat narration_text bo'lsa, u subtitr sifatida olinadi).
    /// </summary>
    public static void Canonicalize(VideoScript s)
    {
        s.SubtitleText = string.IsNullOrWhiteSpace(s.SubtitleText) ? (s.NarrationText ?? "").Trim() : s.SubtitleText.Trim();
        s.NarrationText = s.SubtitleText;
        s.YouTubeTitle = (s.YouTubeTitle ?? "").Trim();
        s.YouTubeDescription = (s.YouTubeDescription ?? "").Trim();
        s.Tags = CleanTags(s.Tags, Domain.DefaultPrompts.TagsPerVideo);
        s.Sources ??= new(); s.Scenes ??= new(); s.UnverifiedClaims ??= new(); s.OnScreenTexts ??= new();
    }

    /// <summary>
    /// YouTube taglari: # va burchak qavslar olib tashlanadi, bo'shliqlar yig'iladi, bo'sh va 100 belgidan uzunlari tashlanadi,
    /// takrorlar (katta-kichik harfsiz) olib tashlanadi, ko'pi bilan <paramref name="max"/> ta.
    /// </summary>
    public static List<string> CleanTags(IEnumerable<string>? tags, int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in tags ?? Array.Empty<string>())
        {
            var t = Regex.Replace((raw ?? "").Replace("#", " ").Replace("<", " ").Replace(">", " ").Replace(",", " "), @"\s+", " ").Trim();
            if (t.Length is 0 or > 100 || !seen.Add(t)) continue;
            list.Add(t);
            if (list.Count >= max) break;
        }
        return list;
    }

    public static List<string> Validate(VideoScript s, string language, int targetDurationSec)
    {
        Canonicalize(s);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(s.SubtitleText)) problems.Add("subtitle_text bo'sh.");
        if (string.IsNullOrWhiteSpace(s.YouTubeTitle)) problems.Add("youtube_title bo'sh.");
        else if (s.YouTubeTitle.Length > 100) problems.Add($"youtube_title juda uzun ({s.YouTubeTitle.Length} > 100 belgi).");
        if (s.YouTubeDescription.Length > 4900) problems.Add("youtube_description 4900 belgidan uzun.");
        if (s.YouTubeTitle.Contains('<') || s.YouTubeTitle.Contains('>')) problems.Add("youtube_title ichida < yoki > belgisi bo'lmasligi kerak (YouTube rad etadi).");
        if (s.YouTubeDescription.Contains('<') || s.YouTubeDescription.Contains('>')) problems.Add("youtube_description ichida < yoki > bo'lmasligi kerak.");

        foreach (Match m in StageDirection.Matches(s.SubtitleText))
            problems.Add($"Ovozga o'qilmasligi kerak bo'lgan texnik ko'rsatma/belgi bor: \"{Trim(m.Value)}\"");
        if (Emoji.IsMatch(s.SubtitleText)) problems.Add("subtitle_text ichida emoji bor.");

        var words = CountWords(s.NarrationText ?? "");
        var wps = LanguageNames.WordsPerSecond(language);
        var expected = targetDurationSec * wps;
        if (targetDurationSec > 0 && words > 0)
        {
            var ratio = words / expected;
            if (ratio < 0.65 || ratio > 1.45)
                problems.Add($"Matn uzunligi davomiylikka mos emas: {words} so'z, ~{targetDurationSec}s uchun taxminan {expected:0} so'z kerak.");
        }
        else if (words > 0)
        {
            // Dinamik uzunlik: faqat aqlga sig'adigan chegaralar (juda qisqa yoki resurslarni bitiradigan darajada uzun emas).
            if (words < 40) problems.Add($"Ssenariy juda qisqa ({words} so'z): mavzuni tushuntirish uchun yetarli emas.");
            if (words > 2500) problems.Add($"Ssenariy haddan tashqari uzun ({words} so'z, ~{words / wps / 60:0} daqiqa): mavzuni ixchamroq yozing.");
        }

        // Sahnalarni Gemini emas, Claude subtitr ma'nosiga qarab o'zi belgilaydi.
        return problems;
    }

    /// <summary>Model o'zi yozgan, lekin grounding'da topilmagan manbalarni olib tashlaydi.</summary>
    public static List<string> SanitizeSources(VideoScript s)
    {
        var notes = new List<string>();
        var grounded = new HashSet<string>(s.GroundedSources.Select(g => NormalizeUrl(g.Url)), StringComparer.OrdinalIgnoreCase);
        var keep = new List<SourceRef>();
        foreach (var src in s.Sources)
        {
            if (grounded.Contains(NormalizeUrl(src.Url))) keep.Add(src);
            else notes.Add($"Tasdiqlanmagan manba olib tashlandi: {src.Url}");
        }
        s.Sources = keep;
        s.SourcesVerified = keep.Count > 0;
        return notes;
    }

    private static string NormalizeUrl(string u) => (u ?? "").Trim().TrimEnd('/');
    private static string Trim(string v) => v.Length > 40 ? v[..40] + "…" : v;
}
