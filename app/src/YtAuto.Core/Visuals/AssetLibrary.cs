using System.Globalization;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Visuals;

/// <summary>
/// Umumiy rasm kutubxonasi (ytauto.assets): litsenziyasi tekshirilgan tiniq rasmlar, fondan ajratilgan PNG'lar, 3D renderlar.
/// Kutubxonani `library/` (Python) zavodi to'ldiradi; worker video paytida faqat qidiradi va yuklab oladi.
/// </summary>
public static class AssetKinds
{
    public const string Photo = "photo";
    /// <summary>Fotodan fondan ajratilgan obyekt (shaffof PNG).</summary>
    public const string Cutout = "cutout";
    /// <summary>3D modelning shaffof fonli studiya renderi.</summary>
    public const string Render = "render";
    public const string Icon = "icon";
    public const string Diagram = "diagram";
    public const string Texture = "texture";
}

/// <summary>Sahna turi: nechta element kerakligi va qaysi turdagi rasmlar mosligi shundan.</summary>
public static class SceneTypes
{
    /// <summary>Bitta katta real obyekt, kamera unga yaqinlashadi.</summary>
    public const string Hero = "hero";
    /// <summary>3-8 ta fondan ajratilgan obyekt qatlam-qatlam (Pinterest kollaj).</summary>
    public const string Collage = "collage";
    /// <summary>2-4 narsa yonma-yon.</summary>
    public const string Comparison = "comparison";
    /// <summary>Real foto/diagramma + chizilgan belgilar, strelkalar.</summary>
    public const string Explainer = "explainer";
    /// <summary>Faqat kinetik matn (ehtimol bitta tekstura).</summary>
    public const string Typography = "typography";
    /// <summary>Rasm telefon/ekran/kartochka ichida.</summary>
    public const string Mockup = "mockup";

    public static readonly string[] All = { Hero, Collage, Comparison, Explainer, Typography, Mockup };

    public static string Normalize(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        return All.Contains(s) ? s : Hero;
    }

    /// <summary>Sahna turiga mos element soni chegarasi.</summary>
    public static (int Min, int Max) ElementRange(string type) => Normalize(type) switch
    {
        Collage => (3, 8),
        Comparison => (2, 4),
        Explainer => (1, 3),
        Typography => (0, 1),
        Mockup => (1, 2),
        _ => (1, 2),
    };

    /// <summary>Kutubxonadan qaysi turdagi rasmlar qidiriladi (tartib - afzallik).</summary>
    public static IReadOnlyList<string> AssetKindsFor(string type) => Normalize(type) switch
    {
        Collage => new[] { AssetKinds.Cutout, AssetKinds.Render, AssetKinds.Icon, AssetKinds.Photo },
        Comparison => new[] { AssetKinds.Cutout, AssetKinds.Render, AssetKinds.Photo },
        Explainer => new[] { AssetKinds.Photo, AssetKinds.Diagram, AssetKinds.Cutout },
        Typography => new[] { AssetKinds.Texture, AssetKinds.Photo },
        Mockup => new[] { AssetKinds.Photo, AssetKinds.Diagram },
        _ => new[] { AssetKinds.Photo, AssetKinds.Render, AssetKinds.Cutout },
    };

    /// <summary>Claude ko'rib tanlashi uchun nomzodlar soni: elementlardan ko'proq (rad etishga joy qolsin), 2..12.</summary>
    public static int CandidatesFor(string type, int elements)
    {
        var (min, max) = ElementRange(type);
        var e = Math.Clamp(elements, Math.Max(1, min), Math.Max(1, max));
        return Math.Clamp(e * 2 + 1, 2, 12);
    }
}

/// <summary>Kutubxonadan qidiruv: kalit so'zlar (inglizcha, aniqdan umumiyga), turlar, yo'nalish, minimal o'lcham.</summary>
public sealed record AssetQuery(
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Kinds,
    int Limit,
    string Orientation = "",
    int MinLongSide = 0,
    string ChannelId = "",
    int RecentDays = 60,
    IReadOnlyCollection<long>? ExcludeIds = null);

/// <summary>Topilgan rasm.</summary>
public sealed record AssetHit(
    long Id, string Source, string Kind, string Title, string FileUrl, string PreviewUrl,
    int Width, int Height, bool HasAlpha, string License, string LicenseUrl, string Author, string PageUrl, bool NeedsCredit, double Rank)
{
    /// <summary>Video tavsifi uchun muallif qatori (faqat CC BY - muallif ko'rsatilishi shart bo'lganlar), aks holda "".</summary>
    public string CreditLine()
    {
        if (!NeedsCredit) return "";
        var t = Title.Length > 60 ? Title[..60] + "…" : Title;
        var a = Author.Length > 40 ? Author[..40] + "…" : Author;
        return $"{t}{(a.Length > 0 ? " by " + a : "")}, {License}{(PageUrl.Length > 0 ? $" ({PageUrl})" : "")}";
    }
}

/// <summary>Kutubxona katalogi (online baza). Jadval yo'q (eski baza) bo'lsa <see cref="AssetLibraryUnavailableException"/>.</summary>
public interface IAssetCatalog
{
    Task<IReadOnlyList<AssetHit>> SearchAsync(AssetQuery query, CancellationToken ct);
    /// <summary>Videoda ishlatilgan rasmlar: shu kanalda yaqin kunlarda qayta chiqmasin.</summary>
    Task RecordUsageAsync(IReadOnlyCollection<long> assetIds, string channelId, long jobId, CancellationToken ct);
}

public sealed class AssetLibraryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Video tempi: o'rtacha kadr necha soniyada almashadi. Shablon videosi bo'lsa - undan o'lchanadi (kesimlar orasidagi o'rtacha vaqt),
/// bo'lmasa - formatga qarab Pinterest/Reels standarti: vertikal ~1.6 s, gorizontal ~2.4 s.
/// </summary>
public static class Pacing
{
    public const double VerticalDefault = 1.6, LandscapeDefault = 2.4, Min = 0.8, Max = 5.0;

    public static double SecondsPerShot(VideoFormat format, double? templateCutSec)
    {
        if (templateCutSec is double t && t > 0 && !double.IsNaN(t)) return Math.Round(Math.Clamp(t, Min, Max), 2);
        return format == VideoFormat.Landscape16x9 ? LandscapeDefault : VerticalDefault;
    }

    /// <summary>
    /// Kesimlar vaqtlaridan o'rtacha kadr uzunligi: juda yaqin (0.25 s dan) kesimlar bitta deb olinadi; kesim kam bo'lsa null.
    /// </summary>
    public static double? FromCuts(IReadOnlyList<double> cuts, double duration)
    {
        if (duration <= 2) return null;
        var pts = cuts.Where(t => t > 0.1 && t < duration - 0.1).OrderBy(t => t).ToList();
        var merged = new List<double>();
        foreach (var t in pts) if (merged.Count == 0 || t - merged[^1] >= 0.25) merged.Add(t);
        if (merged.Count < 2) return null;
        return Math.Round(duration / (merged.Count + 1), 2);
    }

    /// <summary>Video uchun kadrlar (rasm joylari) taxminiy soni: ovoz uzunligi / temp, 4..150.</summary>
    public static int ShotBudget(double audioSec, double secondsPerShot) =>
        Math.Clamp((int)Math.Round(audioSec / Math.Max(Min, secondsPerShot)), 4, 150);

    public static string Describe(double pace) => pace.ToString("0.0", CultureInfo.InvariantCulture);
}
