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

/// <summary>Harakat tahlili uchun shablon videosidan kesilgan klip: fayl va asl videodagi boshlanish vaqti (s).</summary>
public sealed record MotionClip(string Path, double StartSec, double DurationSec);

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
    /// <summary>
    /// Shablon videosining HARAKATI (MOTION_GUIDE.md matni): elementlar qanday paydo bo'ladi, davomiylik va easing, sahna o'tishlari,
    /// temp, kamera harakati, matn animatsiyasi. Model kadrlarni emas, videoning o'zini (kichik klip, <paramref name="fps"/> kadr/s) ko'radi.
    /// </summary>
    Task<string> TemplateMotionGuideAsync(IReadOnlyList<MotionClip> clips, double fps, string templateName, string notes, IJobLog log, CancellationToken ct);
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

// ------------------------------------------------------------------ stok nomzodlar (OpenFilm qidiradi, Claude tanlaydi)

/// <summary>Stok nomzod turi.</summary>
public static class StockTypes
{
    public const string Image = "image";
    public const string Video = "video";
}

/// <summary>Bitta sahna uchun stok qidiruv so'rovi (Gemini rasm rejasidagi "photo" bandidan).</summary>
public sealed record StockRequest(string Id, string Query, string Purpose, double At);

/// <summary>Har sahnaga nechta nomzod va qaysi yo'nalishda (landscape/portrait).</summary>
public sealed record StockOptions(int Images, int Clips, string Orientation);

/// <summary>Bitta nomzod: OpenFilm internetdan topib yuklagan rasm yoki qisqa video parcha.</summary>
public sealed class StockCandidate
{
    /// <summary>Sahna ichidagi belgi: I1..I5 (rasm), V1..V3 (video). Kontakt varaqda shu yozuv bilan ko'rinadi.</summary>
    public string Label { get; set; } = "";
    public string Type { get; set; } = StockTypes.Image;
    /// <summary>Manba chiqishida - mutlaq yo'l; film ichiga ko'chirilgach - film/ ga nisbiy yo'l (assets/stock/s01/img1.jpg).</summary>
    public string File { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Video davomiyligi (s); rasm uchun 0.</summary>
    public double Duration { get; set; }
    public long Bytes { get; set; }
    /// <summary>pexels | pixabay</summary>
    public string Provider { get; set; } = "";
    /// <summary>"Photo by X on Pexels" - OpenFilm bergan muallif qatori.</summary>
    public string Credit { get; set; } = "";
    /// <summary>Asl sahifa.</summary>
    public string Page { get; set; } = "";
}

/// <summary>Bitta sahna: so'rov va unga topilgan nomzodlar.</summary>
public sealed class StockScene
{
    /// <summary>s01, s02, ...</summary>
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    public string Purpose { get; set; } = "";
    public double At { get; set; }
    public List<StockCandidate> Candidates { get; set; } = new();
    /// <summary>Kontakt varaq (edit/ ga nisbiy yo'l), bo'lmasa "".</summary>
    public string Sheet { get; set; } = "";
    /// <summary>Qidiruvda nima bo'lmadi (topilmadi, manba rad etdi...).</summary>
    public List<string> Notes { get; set; } = new();
    public int Images => Candidates.Count(c => c.Type == StockTypes.Image);
    public int Clips => Candidates.Count(c => c.Type == StockTypes.Video);
}

/// <summary>Video uchun barcha stok nomzodlar (edit/stock_cache/stock.json va record/stock.json).</summary>
public sealed class StockCandidates
{
    public string PlanHash { get; set; } = "";
    public List<StockScene> Scenes { get; set; } = new();

    public int Total => Scenes.Sum(s => s.Candidates.Count);
    public string ToJson() => JsonSerializer.Serialize(this, MediaManifest.Json);
    public static StockCandidates FromJson(string json) => JsonSerializer.Deserialize<StockCandidates>(json, MediaManifest.Json) ?? new();
}

/// <summary>
/// Claude'ning tanlovi (film/media_selection.json): har sahna uchun qaysi nomzodlar olindi va qaysilari nima uchun rad etildi.
/// Format erkin o'qiladi: faqat "file" maydonlari muhim.
/// </summary>
public sealed class StockSelection
{
    public sealed class Pick
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
    public sealed class Scene
    {
        [JsonPropertyName("scene")] public string Id { get; set; } = "";
        [JsonPropertyName("chosen")] public List<Pick> Chosen { get; set; } = new();
        [JsonPropertyName("rejected")] public List<Pick> Rejected { get; set; } = new();
    }
    [JsonPropertyName("scenes")] public List<Scene> Scenes { get; set; } = new();

    public IEnumerable<string> ChosenFiles => Scenes.SelectMany(s => s.Chosen).Select(p => Norm(p.File)).Where(f => f.Length > 0);
    public IEnumerable<string> RejectedFiles => Scenes.SelectMany(s => s.Rejected).Select(p => Norm(p.File)).Where(f => f.Length > 0);

    /// <summary>"./assets/stock/s01/img1.jpg", "film/assets/..." va "\\" ko'rinishlari bir xil yo'lga keltiriladi.</summary>
    public static string Norm(string? f)
    {
        var s = (f ?? "").Trim().Replace('\\', '/');
        while (s.StartsWith("./")) s = s[2..];
        if (s.StartsWith("film/", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        return s;
    }

    /// <summary>Buzilgan yoki bo'sh JSON - null (xato emas: tanlov ixtiyoriy hujjat).</summary>
    public static StockSelection? TryParse(string json)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonSerializer.Deserialize<StockSelection>(json, opts);
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Internetdan stok rasm va qisqa video parchalarini qidirib yuklaydigan manba (OpenFilm `get image-search` / `get video-search`).
/// Har sahna uchun bir nechta nomzod olib keladi; qaysi biri yaroqli ekanini Claude hal qiladi.
/// </summary>
public interface IStockMediaSource
{
    /// <summary>Qidirish mumkinmi (OpenFilm o'rnatilgan va kamida bitta manba kaliti bor).</summary>
    bool IsConfigured { get; }
    /// <summary>
    /// Har so'rov uchun nomzodlarni <paramref name="workDir"/> ga yuklaydi (File - mutlaq yo'l). Bitta sahnadagi xato boshqalarini to'xtatmaydi:
    /// u sahna Notes'ga yoziladi. Hech narsa topilmasa ham xato emas (Candidates bo'sh).
    /// </summary>
    Task<StockCandidates> FetchAsync(IReadOnlyList<StockRequest> scenes, string workDir, StockOptions options, IJobLog log, CancellationToken ct);
}

/// <summary>Kontakt varaqdagi bitta katak: rasm fayli, ustidagi yozuv va o'rni (qator, ustun).</summary>
public sealed record SheetTile(string Path, string Label, int Row, int Col);
