using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtAuto.Core.Demo;

/// <summary>
/// Claude Code (MCP) yuborgan demo video so'rovi: loyiha konteksti, vizual materiallar va foydalanuvchi talablari.
/// Bot (MCP server) yozadi va <c>jobs.demo_spec</c> ga saqlaydi; worker o'qiydi. Ssenariy, ovoz va montaj - YtAuto ichida.
/// </summary>
public sealed class DemoSpec
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("project")] public DemoProject Project { get; set; } = new();
    [JsonPropertyName("media")] public List<DemoMedia> Media { get; set; } = new();
    [JsonPropertyName("requirements")] public DemoRequirements Requirements { get; set; } = new();
    [JsonPropertyName("publish")] public DemoPublish Publish { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false, PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static DemoSpec FromJson(string json) => JsonSerializer.Deserialize<DemoSpec>(json, Json) ?? throw new InvalidDataException("Bo'sh demo so'rovi.");
}

public sealed class DemoProject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Loyiha nima qiladi va kim uchun (Claude kod bazasini o'rganib yozgan).</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("repository")] public string? Repository { get; set; }
    [JsonPropertyName("tech_stack")] public string? TechStack { get; set; }
    [JsonPropertyName("features")] public List<DemoFeature> Features { get; set; } = new();
}

public sealed class DemoFeature
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

public static class DemoMediaKinds
{
    public const string Screenshot = "screenshot";
    public const string Video = "video";
}

/// <summary>Bitta vizual material: screenshot yoki qisqa action video va unda nima ko'rsatilayotgani.</summary>
public sealed class DemoMedia
{
    /// <summary>MCP media identifikatori (Storage'dagi hisob papkasiga nisbiy yo'l, masalan "a1b2.../03.png").</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = DemoMediaKinds.Screenshot;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>Kadrda nima ko'rinadi / videoda qanday amal bajariladi (kontekst).</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("feature")] public string? Feature { get; set; }
    /// <summary>Diqqat qaratiladigan joy (zoom/pan uchun), masalan "top-right Save button".</summary>
    [JsonPropertyName("highlight")] public string? Highlight { get; set; }
    /// <summary>Kompyuter yuklab oladigan imzolangan havola (bot qo'yadi).</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }

    public bool IsVideo => Kind == DemoMediaKinds.Video;
}

public sealed class DemoRequirements
{
    [JsonPropertyName("language")] public string Language { get; set; } = "en";
    /// <summary>0 - avtomatik (materiallar va funksiyalar soniga qarab).</summary>
    [JsonPropertyName("duration_sec")] public int DurationSec { get; set; }
    [JsonPropertyName("audience")] public string? Audience { get; set; }
    [JsonPropertyName("tone")] public string? Tone { get; set; }
    /// <summary>Foydalanuvchining qo'shimcha talablari (erkin matn).</summary>
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>Montaj effektlari (<see cref="DemoEffects"/> kalitlari). Bo'sh - hammasi ruxsat, Claude sahnaga mosini tanlaydi.</summary>
    [JsonPropertyName("effects")] public List<string>? Effects { get; set; }
}

/// <summary>Video yaratish bilan birga chiqarish so'ralganmi (faqat asosiy hisob, YouTube ulangan bo'lsa - bot tekshiradi).</summary>
public sealed class DemoPublish
{
    [JsonPropertyName("requested")] public bool Requested { get; set; }
    /// <summary>"now" yoki ISO vaqt (UTC).</summary>
    [JsonPropertyName("when")] public string? When { get; set; }

    public bool Now => Requested && (string.IsNullOrWhiteSpace(When) || When!.Trim().Equals("now", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Kompyuterga yuklab olingan va tekshirilgan material (ish papkasida).</summary>
public sealed class DemoLocalMedia
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>Ish papkasiga nisbiy yo'l (demo/media/...).</summary>
    public string File { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Video uchun davomiylik (s), rasm uchun 0.</summary>
    public double DurationSec { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Gemini ko'radigan kichik kadrlar (rasm uchun 1, video uchun 3), ish papkasiga nisbiy.</summary>
    public List<string> Previews { get; set; } = new();
}

public sealed class DemoMediaManifest
{
    public List<DemoLocalMedia> Items { get; set; } = new();
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static DemoMediaManifest FromJson(string json) => JsonSerializer.Deserialize<DemoMediaManifest>(json, Json) ?? new();
}

/// <summary>Ssenariy sahnasi: qaysi material ko'rsatiladi va shu paytda nima aytiladi.</summary>
public sealed class DemoScene
{
    [JsonPropertyName("media_id")] public string MediaId { get; set; } = "";
    [JsonPropertyName("narration")] public string Narration { get; set; } = "";
    /// <summary>Kadrda nimaga e'tibor qaratish (zoom/pan maqsadi) - ovozga kirmaydi, montaj metadata'si.</summary>
    [JsonPropertyName("focus")] public string Focus { get; set; } = "";
    /// <summary>Ekrandagi qisqa sarlavha (2-6 so'z) - ovozga kirmaydi.</summary>
    [JsonPropertyName("on_screen_text")] public string OnScreenText { get; set; } = "";
}

/// <summary>1-bosqich (Gemini): to'liq ssenariy qoralamasi - narration, sahnalar va YouTube metadata.</summary>
public sealed class DemoDraft
{
    [JsonPropertyName("youtube_title")] public string YouTubeTitle { get; set; } = "";
    [JsonPropertyName("youtube_description")] public string YouTubeDescription { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("scenes")] public List<DemoScene> Scenes { get; set; } = new();
}

/// <summary>Montaj uchun sahna rejasi: sahna vaqtlari ovozdagi haqiqiy so'z vaqtlaridan hisoblanadi.</summary>
public sealed class DemoPlanScene
{
    public int Index { get; set; }
    public string MediaId { get; set; } = "";
    /// <summary>film/ ga nisbiy yo'l (assets/demo/...).</summary>
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Narration { get; set; } = "";
    public string Focus { get; set; } = "";
    public string OnScreenText { get; set; } = "";
    public string MediaTitle { get; set; } = "";
    public string MediaDescription { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double MediaDurationSec { get; set; }
}

/// <summary>Tayyor demo videoning serverdagi (Storage) nusxasi: MCP get_video_result shu yo'llar uchun havola beradi.</summary>
public sealed class DemoResult
{
    [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
    [JsonPropertyName("cover_path")] public string? CoverPath { get; set; }
    [JsonPropertyName("subtitles_path")] public string? SubtitlesPath { get; set; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("duration_sec")] public double DurationSec { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("local_path")] public string? LocalPath { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("delivered_at_utc")] public DateTimeOffset? DeliveredAtUtc { get; set; }
    /// <summary>Serverga yuborib bo'lmagan bo'lsa - sabab (video kompyuterdagi arxivda baribir bor).</summary>
    [JsonPropertyName("error")] public string? Error { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, DemoSpec.Json);
}
