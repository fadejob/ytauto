using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace YtAuto.Core.Qa;

public enum ClipKind { Page, Video, Image, Audio }

public sealed class TimelineClip
{
    public int Track { get; set; }
    public ClipKind Kind { get; set; }
    public string Src { get; set; } = "";
    public double At { get; set; }
    public double? In { get; set; }
    public double? Out { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Muted { get; set; }
    /// <summary>Hisoblangan davomiylik; null - aniqlab bo'lmadi (masalan, #t yo'q va media uzunligi noma'lum).</summary>
    public double? Length { get; set; }
    public double? End => Length is null ? null : At + Length;
    public bool IsVisual => Kind != ClipKind.Audio;
}

public sealed class FilmTimeline
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public List<TimelineClip> Clips { get; } = new();
    public HashSet<int> HiddenTracks { get; } = new();
    public HashSet<int> MutedTracks { get; } = new();
    public List<string> ParseWarnings { get; } = new();
}

/// <summary>
/// OpenFilm film.html ni (OpenFilm "edit" skill'idagi format bo'yicha) o'qiydi:
/// har &lt;section&gt; - trek; &lt;iframe|video|img|audio&gt; - klip, src#t=in,out, at, speed, volume.
/// </summary>
public static class FilmTimelineParser
{
    private static readonly Regex Viewport = new(@"<meta[^>]+name\s*=\s*[""']viewport[""'][^>]*content\s*=\s*[""'](?<c>[^""']*)[""']", RegexOptions.IgnoreCase);
    private static readonly Regex Section = new(@"<section(?<attrs>[^>]*)>(?<body>.*?)</section>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Element = new(@"<(?<tag>iframe|video|img|audio)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
    private static readonly Regex Attr = new(@"(?<name>[\w:-]+)(?:\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+)))?", RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline);

    /// <param name="mediaDuration">src (nisbiy yo'l) bo'yicha media uzunligi yoki sahifaning duration'i; noma'lum bo'lsa null.</param>
    public static FilmTimeline Parse(string html, Func<string, double?>? mediaDuration = null)
    {
        var tl = new FilmTimeline();
        html = Comment.Replace(html, "");
        var vp = Viewport.Match(html);
        if (vp.Success)
        {
            var w = Regex.Match(vp.Groups["c"].Value, @"width\s*=\s*(\d+)"); var h = Regex.Match(vp.Groups["c"].Value, @"height\s*=\s*(\d+)");
            if (w.Success) tl.Width = int.Parse(w.Groups[1].Value);
            if (h.Success) tl.Height = int.Parse(h.Groups[1].Value);
        }
        int track = 0;
        foreach (Match sec in Section.Matches(html))
        {
            var sattrs = ParseAttrs(sec.Groups["attrs"].Value);
            if (sattrs.ContainsKey("hidden")) tl.HiddenTracks.Add(track);
            if (sattrs.ContainsKey("muted")) tl.MutedTracks.Add(track);
            foreach (Match el in Element.Matches(sec.Groups["body"].Value))
            {
                var a = ParseAttrs(el.Groups["attrs"].Value);
                var tag = el.Groups["tag"].Value.ToLowerInvariant();
                var clip = new TimelineClip
                {
                    Track = track,
                    Kind = tag switch { "iframe" => ClipKind.Page, "video" => ClipKind.Video, "img" => ClipKind.Image, _ => ClipKind.Audio },
                };
                var src = WebUtility.HtmlDecode(a.GetValueOrDefault("src") ?? "");
                var hash = src.IndexOf("#t=", StringComparison.Ordinal);
                if (hash >= 0)
                {
                    var range = src[(hash + 3)..].Split(',');
                    clip.In = Num(range[0]);
                    if (range.Length > 1) clip.Out = Num(range[1]);
                    src = src[..hash];
                }
                else if (src.Contains('#')) src = src[..src.IndexOf('#')];
                clip.Src = src;
                clip.At = Num(a.GetValueOrDefault("at")) ?? 0;
                clip.Speed = Num(a.GetValueOrDefault("speed")) ?? 1;
                if (clip.Speed <= 0) clip.Speed = 1;
                clip.Volume = Num(a.GetValueOrDefault("volume")) ?? 1;
                clip.Muted = a.ContainsKey("muted");

                double? inP = clip.In ?? 0, outP = clip.Out;
                if (outP is null)
                {
                    var full = mediaDuration?.Invoke(src);
                    if (full is not null) outP = full;
                }
                if (outP is not null) clip.Length = Math.Max(0, (outP.Value - inP.Value) / clip.Speed);
                else tl.ParseWarnings.Add($"Klip uzunligi aniqlanmadi: {tag} {src} (#t yo'q)");
                tl.Clips.Add(clip);
            }
            track++;
        }
        return tl;
    }

    private static Dictionary<string, string?> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr.Matches(s))
        {
            var name = m.Groups["name"].Value;
            if (name.Length == 0 || name == "/") continue;
            d[name] = m.Groups["v"].Success ? m.Groups["v"].Value : null;
        }
        return d;
    }

    private static double? Num(string? s) =>
        double.TryParse(s?.Trim().TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
