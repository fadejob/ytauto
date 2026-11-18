using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YtAuto.Core.Pipeline;
using YtAuto.Core;

namespace YtAuto.Core.Visuals;

/// <summary>Videoda ishlatiladigan real rasm turi.</summary>
public static class MediaKinds
{
    /// <summary>Tushuncha ikoni (SVG, rangi CSS bilan o'zgartiriladi).</summary>
    public const string Icon = "icon";
    /// <summary>Haqiqiy brend/mahsulot/tashkilot logosi (SVG).</summary>
    public const string Logo = "logo";
    /// <summary>Real sahna/obyekt fotosi yoki rasmi.</summary>
    public const string Photo = "photo";
    public static readonly string[] All = { Icon, Logo, Photo };
}

/// <summary>Gemini ssenariy ma'nosiga qarab so'ragan bitta rasm: nima, qachon, nima uchun.</summary>
public sealed class MediaPlanItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = MediaKinds.Icon;
    /// <summary>Qidiruv so'zi (inglizcha, 1-4 so'z). Logo uchun - brend nomi.</summary>
    [JsonPropertyName("query")] public string Query { get; set; } = "";
    /// <summary>Sahnada nimani tushuntiradi (Claude'ga ko'rsatiladi).</summary>
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    /// <summary>Taxminiy vaqt (s): shu rasm kerak bo'lgan sahna boshlanishi.</summary>
    [JsonPropertyName("at")] public double At { get; set; }
}

public sealed class MediaPlan
{
    [JsonPropertyName("items")] public List<MediaPlanItem> Items { get; set; } = new();

    /// <summary>Takrorlar, bo'sh/noto'g'ri yozuvlar olib tashlanadi, soni cheklanadi.</summary>
    public MediaPlan Clean(int max)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<MediaPlanItem>();
        foreach (var i in Items)
        {
            var kind = (i.Kind ?? "").Trim().ToLowerInvariant();
            var q = System.Text.RegularExpressions.Regex.Replace(i.Query ?? "", @"\s+", " ").Trim();
            if (!MediaKinds.All.Contains(kind) || q.Length is < 2 or > 60) continue;
            if (!seen.Add(kind + ":" + q)) continue;
            list.Add(new MediaPlanItem { Kind = kind, Query = q, Purpose = (i.Purpose ?? "").Trim(), At = Math.Max(0, i.At) });
            if (list.Count >= max) break;
        }
        return new MediaPlan { Items = list };
    }
}

/// <summary>Yuklab olingan rasm va uning litsenziyasi (manba, muallif). Fayl - film/ ichidagi nisbiy yo'l.</summary>
public sealed class MediaAsset
{
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Query { get; set; } = "";
    public string Purpose { get; set; } = "";
    public double At { get; set; }
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>Qisqa litsenziya nomi: "CC0", "Public domain", "CC BY 4.0", "MIT"...</summary>
    public string License { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    /// <summary>Asl sahifa (muallif ko'rsatish uchun).</summary>
    public string SourceUrl { get; set; } = "";
    public string Provider { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Litsenziya muallifni ko'rsatishni talab qiladi (CC BY): tavsifga yoziladi.</summary>
    public bool NeedsCredit { get; set; }
}

public sealed class MediaManifest
{
    public string PlanHash { get; set; } = "";
    public List<MediaAsset> Assets { get; set; } = new();
    /// <summary>Topilmagan yoki litsenziyasi mos kelmagani uchun tashlab ketilgan so'rovlar.</summary>
    public List<string> Skipped { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static MediaManifest FromJson(string json) => JsonSerializer.Deserialize<MediaManifest>(json, Json) ?? new();

    /// <summary>YouTube tavsifi uchun mualliflar bloki (faqat muallif ko'rsatilishi shart bo'lgan rasmlar). Bo'sh bo'lsa "".</summary>
    public string CreditsBlock()
    {
        var need = Assets.Where(a => a.NeedsCredit).GroupBy(a => a.SourceUrl).Select(g => g.First()).ToList();
        if (need.Count == 0) return "";
        var sb = new StringBuilder("Image credits:");
        foreach (var a in need)
        {
            sb.Append("\n- ");
            sb.Append(string.IsNullOrWhiteSpace(a.Title) ? a.Query : Trim(a.Title, 60));
            if (!string.IsNullOrWhiteSpace(a.Author)) sb.Append(" by ").Append(Trim(a.Author, 40));
            sb.Append(", ").Append(a.License);
            if (!string.IsNullOrWhiteSpace(a.SourceUrl)) sb.Append(" (").Append(a.SourceUrl).Append(')');
        }
        return sb.ToString();
    }

    private static string Trim(string s, int n) { s = s.Replace("\n", " ").Trim(); return s.Length > n ? s[..n] + "…" : s; }
}

/// <summary>Tayyor video kadrlarining shablon uslubiga mosligi (Gemini vision): 0-10 ball va tuzatish kerak bo'lgan joylar.</summary>
public sealed class StyleReview
{
    [JsonPropertyName("score")] public double Score { get; set; }
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("problems")] public List<string> Problems { get; set; } = new();
}

/// <summary>Kadrlarni ko'radigan model (Gemini vision): shablon uslubi, rasm rejasi va natijani shablon bilan solishtirish.</summary>
public interface IVisualDirector
{
    /// <summary>Shablon kadrlaridan dizayn tizimi (STYLE_GUIDE.md matni): ranglar (hex), shrift, kompozitsiya, fon, ikon/rasm uslubi, harakat.</summary>
    Task<string> TemplateStyleGuideAsync(IReadOnlyList<string> framePaths, string templateName, string notes, IJobLog log, CancellationToken ct);
    /// <summary>Subtitr ma'nosiga qarab qaysi sahnaga qanday real ikon/logo/foto kerakligi.</summary>
    Task<MediaPlan> PlanMediaAsync(string subtitleText, IReadOnlyList<SubtitleCue> cues, string channelContext, int maxItems, IJobLog log, CancellationToken ct);
    /// <summary>Tayyor video kadrlarini shablon kadrlari va uslub qo'llanmasi bilan solishtiradi.</summary>
    Task<StyleReview> ReviewAgainstTemplateAsync(IReadOnlyList<string> templateFrames, IReadOnlyList<string> videoFrames, string styleGuide, IJobLog log, CancellationToken ct);
}

/// <summary>Ochiq manbalardan (litsenziyasi tijoratga ruxsat beradigan) rasm/ikonlarni yuklab oladi.</summary>
public interface IMediaLibrary
{
    /// <summary>Rejadagi har so'rov uchun mos rasmni <paramref name="mediaDir"/> ga yuklaydi. Topilmasa - Skipped. Xato ishni to'xtatmaydi.</summary>
    Task<MediaManifest> FetchAsync(MediaPlan plan, string mediaDir, IJobLog log, CancellationToken ct);
}
