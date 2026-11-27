using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;

namespace YtAuto.Infrastructure.Media;

/// <summary>Real vaqtda topilgan bitta nomzod (yuklab olishdan oldin).</summary>
public sealed record LiveHit(
    string Provider, string Kind, string Title, string Url, string Preview, int Width, int Height, bool HasAlpha,
    string License, string Author, string Page, bool NeedsCredit, string Credit = "",
    IReadOnlyList<(string Rel, string Url)>? Extra = null, IReadOnlyList<string>? Fallbacks = null);

/// <summary>Bitta jonli manba: sahna so'zlari bo'yicha nomzodlar (yuklamaydi).</summary>
public interface ILiveProvider
{
    string Name { get; }
    Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct);
}

/// <summary>
/// Video tayyorlanayotganda, oldindan to'ldirilgan bazasiz, sahna turiga qarab bir nechta manbadan nomzodlar:
/// <list type="bullet">
/// <item>fotolar/diagrammalar - Wikimedia Commons (avval Quality/Featured/Valued; CC0, PD, CC BY; odamlar va cheklangan fayllar olinmaydi), NASA;</item>
/// <item>3D ikonlar (shaffof) - 3dicons (CC0), Icons8 3D Fluency (bepul, havola bilan: tavsifga yoziladi);</item>
/// <item>haqiqiy 3D modellar (sahnada aylanadi, kit/model3d.js) - Poly Haven (CC0) va Objaverse (Sketchfab, CC BY / CC0).</item>
/// </list>
/// Fayllar faqat shu manbalarning hostlaridan https orqali yuklanadi. Natija OpenFilm stok natijasi shaklida.
/// </summary>
public sealed class LiveAssetSource : IStockMediaSource, IMaxModels
{
    internal const string UserAgent = "YtAuto/1.0 (educational video tool)";
    private const long MaxBytes = 30L * 1024 * 1024, MaxModelBytes = 16L * 1024 * 1024;
    private static readonly string[] AllowedHostSuffixes =
    {
        ".wikimedia.org", ".nasa.gov", ".supabase.co", ".icons8.com", ".sketchfab.com", ".huggingface.co", ".hf.co", ".polyhaven.org", ".polyhaven.com",
    };
    private readonly HttpClient _http;
    private readonly IReadOnlyList<ILiveProvider> _photos, _icons, _models;
    /// <summary>Bitta videoga ko'pi bilan nechta 3D model (har biri WebGL konteksti va bir necha MB).</summary>
    public int MaxModels { get; set; } = 4;

    public LiveAssetSource(string cacheDir, HttpClient? http = null)
    {
        _http = http ?? CreateHttp();
        var wiki = new WikimediaLive(_http);
        _photos = new ILiveProvider[] { wiki, new NasaLive(_http) };
        _icons = new ILiveProvider[] { new ThreeDIconsLive(_http), new Icons8ThreeDLive(_http) };
        _models = new ILiveProvider[] { new PolyHavenModels(_http), new ObjaverseModels(_http, cacheDir) };
    }

    /// <summary>Sinov uchun: manbalarni tashqaridan berish.</summary>
    internal LiveAssetSource(HttpClient http, IReadOnlyList<ILiveProvider> photos, IReadOnlyList<ILiveProvider> icons, IReadOnlyList<ILiveProvider> models)
    { _http = http; _photos = photos; _icons = icons; _models = models; }

    internal static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(90) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }

    public bool IsConfigured => true;

    public static bool Allowed(Uri u)
    {
        var test = Environment.GetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN");
        if (!string.IsNullOrWhiteSpace(test) && Uri.TryCreate(test, UriKind.Absolute, out var t) && t.IsLoopback
            && string.Equals(u.GetLeftPart(UriPartial.Authority), t.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return true;
        return u.Scheme == Uri.UriSchemeHttps && AllowedHostSuffixes.Any(s => ("." + u.Host).EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Sahna turiga qarab: nechta foto, 3D ikon va 3D model so'raladi.</summary>
    internal static (int Photos, int Icons, int Models) Mix(string sceneType, int elements)
    {
        var want = SceneTypes.CandidatesFor(sceneType, elements);
        return SceneTypes.Normalize(sceneType) switch
        {
            SceneTypes.Collage => (2, Math.Max(4, want), 1),
            SceneTypes.Comparison => (Math.Max(2, want / 2), Math.Max(2, want / 2), 1),
            SceneTypes.Explainer => (Math.Max(3, want), 1, 0),
            SceneTypes.Mockup => (Math.Max(3, want), 0, 0),
            SceneTypes.Typography => (1, 1, 0),
            _ => (Math.Max(3, want), 1, 1), // hero: katta real foto yoki aylanadigan 3D obyekt
        };
    }

    public async Task<StockCandidates> FetchAsync(IReadOnlyList<StockRequest> scenes, string workDir, StockOptions options, IJobLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new StockCandidates();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = 0;
        foreach (var req in scenes)
        {
            ct.ThrowIfCancellationRequested();
            var type = SceneTypes.Normalize(req.SceneType);
            var scene = new StockScene { Id = req.Id, Query = req.Query, Purpose = req.Purpose, At = req.At, SceneType = type, Elements = req.Elements };
            result.Scenes.Add(scene);
            var (np, ni, nm) = Mix(type, req.Elements);
            if (models >= MaxModels) nm = 0;
            var dir = Path.Combine(workDir, req.Id);
            Directory.CreateDirectory(dir);
            var n = 0;
            async Task Take(IReadOnlyList<ILiveProvider> providers, int want)
            {
                var got = 0;
                foreach (var p in providers)
                {
                    if (got >= want) break;
                    IReadOnlyList<LiveHit> hits;
                    try { hits = await p.SearchAsync(req, want - got + 2, options.Orientation, ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { scene.Notes.Add($"{p.Name}: {e.Message}"); continue; }
                    foreach (var h in hits)
                    {
                        if (got >= want) break;
                        if (!seen.Add(h.Url)) continue;
                        var cand = await DownloadAsync(h, dir, ++n, ct).ConfigureAwait(false);
                        if (cand is null) { scene.Notes.Add($"{p.Name}: {h.Title} yuklanmadi"); continue; }
                        scene.Candidates.Add(cand);
                        got++;
                        if (h.Kind == "model") models++;
                    }
                }
            }
            if (nm > 0) await Take(_models, nm).ConfigureAwait(false);
            if (ni > 0) await Take(_icons, ni).ConfigureAwait(false);
            if (np > 0) await Take(IsSpace(req) ? _photos.OrderBy(p => p.Name == "nasa" ? 0 : 1).ToList() : _photos, np).ConfigureAwait(false);
            if (scene.Candidates.Count == 0) scene.Notes.Add("jonli manbalarda mos rasm topilmadi");
        }
        var all = result.Scenes.SelectMany(s => s.Candidates).ToList();
        log.Info($"Jonli manbalar: {result.Scenes.Count(s => s.Candidates.Count > 0)}/{result.Scenes.Count} sahnaga {all.Count} ta nomzod " +
                 $"({all.Count(x => x.Kind == "photo")} foto, {all.Count(x => x.Kind == "icon3d")} 3D ikon, {all.Count(x => x.Type == StockTypes.Model)} 3D model; " +
                 $"{string.Join(", ", all.GroupBy(x => x.Provider).Select(g => $"{g.Key.Replace("live:", "")} {g.Count()}"))}).", "Media");
        return result;
    }

    private async Task<StockCandidate?> DownloadAsync(LiveHit h, string dir, int n, CancellationToken ct)
    {
        try
        {
            if (h.Kind == "model")
            {
                var mdir = Path.Combine(dir, $"model{n}");
                Directory.CreateDirectory(mdir);
                var main = Path.Combine(mdir, "model" + (h.Url.Contains(".gltf", StringComparison.OrdinalIgnoreCase) ? ".gltf" : ".glb"));
                long total = await GetAsync(h.Url, main, MaxModelBytes, ct).ConfigureAwait(false);
                foreach (var (rel, url) in h.Extra ?? Array.Empty<(string, string)>())
                {
                    var safe = rel.Replace('\\', '/');
                    if (safe.Contains("..") || Path.IsPathRooted(safe)) throw new InvalidOperationException("model fayl yo'li xavfli: " + rel);
                    var p = Path.Combine(mdir, safe.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    total += await GetAsync(url, p, MaxModelBytes - total, ct).ConfigureAwait(false);
                }
                string preview = "";
                if (h.Preview.Length > 0)
                    try { preview = Path.Combine(mdir, "preview.jpg"); await GetAsync(h.Preview, preview, 5L * 1024 * 1024, ct).ConfigureAwait(false); } catch { preview = ""; }
                return new StockCandidate
                {
                    Type = StockTypes.Model, File = main, Preview = preview, Provider = "live:" + h.Provider, Kind = "model", HasAlpha = true,
                    License = h.License, Credit = h.Credit.Length > 0 ? h.Credit : CreditOf(h), Page = h.Page, Bytes = total, Title = h.Title,
                };
            }
            var ext = Path.GetExtension(new Uri(h.Url).AbsolutePath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = h.HasAlpha ? ".png" : ".jpg";
            var path = Path.Combine(dir, $"live{n}{ext}");
            long bytes = 0;
            Exception? last = null;
            foreach (var u in new[] { h.Url }.Concat(h.Fallbacks ?? Array.Empty<string>()))
            {
                try { bytes = await GetAsync(u, path, MaxBytes, ct).ConfigureAwait(false); last = null; break; }
                catch (Exception e) when (e is not OperationCanceledException) { last = e; }
            }
            if (last is not null) throw last;
            return new StockCandidate
            {
                Type = StockTypes.Image, File = path, Width = h.Width, Height = h.Height, Provider = "live:" + h.Provider, Kind = h.Kind,
                HasAlpha = h.HasAlpha, License = h.License, Credit = h.Credit.Length > 0 ? h.Credit : CreditOf(h), Page = h.Page, Bytes = bytes, Title = h.Title,
            };
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static HashSet<string>? _spaceWords;

    /// <summary>Koinot mavzusi: fotolar avval NASA'dan.</summary>
    internal static bool IsSpace(StockRequest req) => Words(req.SearchTerms).Overlaps(_spaceWords ??= Words(new[]
        { "space orbit planet galaxy nebula moon mars jupiter saturn sun solar star stars astronaut rocket satellite spacecraft cosmos universe comet asteroid telescope iss" }));

    private static string CreditOf(LiveHit h)
    {
        if (!h.NeedsCredit) return "";
        var t = h.Title.Length > 60 ? h.Title[..60] + "…" : h.Title;
        var a = h.Author.Length > 40 ? h.Author[..40] + "…" : h.Author;
        return $"{t}{(a.Length > 0 ? " by " + a : "")}, {h.License}{(h.Page.Length > 0 ? $" ({h.Page})" : "")}";
    }

    private async Task<long> GetAsync(string url, string path, long max, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !Allowed(uri)) throw new InvalidOperationException("ruxsatsiz manzil");
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.RequestMessage?.RequestUri is { } final && !Allowed(final)) throw new InvalidOperationException("yo'naltirish ruxsatsiz manzilga");
        if (resp.Content.Headers.ContentLength is long len && len > max) throw new InvalidOperationException("juda katta");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long total = 0; int read;
            while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > max) throw new InvalidOperationException("juda katta");
                await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            if (total < 200) throw new InvalidOperationException("fayl bo'sh");
            return total;
        }
    }

    // ------------------------------------------------------------------ umumiy yordamchilar
    internal static async Task<JsonNode?> JsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)resp.StatusCode} {new Uri(url).Host}");
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    internal static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    /// <summary>So'zlar: kichik harf, faqat harf/raqam, 2+ belgi.</summary>
    internal static HashSet<string> Words(IEnumerable<string> texts) =>
        texts.SelectMany(t => Regex.Split((t ?? "").ToLowerInvariant(), "[^a-z0-9]+")).Where(w => w.Length >= 2 && !Stop.Contains(w))
            .Select(Stem).ToHashSet();

    /// <summary>
    /// Matn sahnaga mosmi: sahna so'rovining hamma so'zlari bor ("honey bee" - ham honey, ham bee), yoki ko'p so'zli kalit ibora /
    /// birinchi (eng aniq, masalan ilmiy nom) kalit so'z to'liq bor. Umumiy so'z ("bee") yolg'iz o'zi yetmaydi: "bee-eater" qushi o'tmaydi.
    /// </summary>
    internal static bool Relevant(string text, StockRequest req)
    {
        var have = Words(new[] { text });
        var q = Words(new[] { req.Query });
        if (q.Count > 0 && q.IsSubsetOf(have)) return true;
        var keys = req.Keywords ?? Array.Empty<string>();
        for (int i = 0; i < keys.Count; i++)
        {
            var k = Words(new[] { keys[i] });
            if (k.Count == 0 || !k.IsSubsetOf(have)) continue;
            if (k.Count >= 2 || i == 0) return true;
        }
        return false;
    }

    private static readonly HashSet<string> Stop = new()
    {
        "the", "and", "of", "in", "on", "with", "for", "a", "an", "to", "at", "by", "from", "into", "over", "under", "near", "about", "as", "is", "are",
        "its", "it", "this", "that", "how", "what", "why", "our", "your", "seen", "view", "3d", "model", "low", "poly", "lowpoly", "free",
    };
    internal static string Stem(string w) => w.Length > 4 && w.EndsWith("ies") ? w[..^3] + "y" : w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") ? w[..^1] : w;
}

// ====================================================================== fotolar
/// <summary>Wikimedia Commons: avval tanlangan (Quality/Featured/Valued), so'ng oddiy fayllar (eni 1600+). Litsenziya va odamlar filtri.</summary>
internal sealed class WikimediaLive(HttpClient http) : ILiveProvider
{
    public string Name => "wikimedia";
    private const string Curated = "Quality_images|Featured_pictures_on_Wikimedia_Commons|Valued_images_sorted_by_promotion_date";
    private const string FreeCats = "CC-BY-4.0|CC-BY-3.0|CC-BY-2.5|CC-BY-2.0|CC-Zero|PD-self|PD-user|PD-author|PD-old|PD-USGov|PD-USGov-NASA|PD-USGov-NIH|PD-USGov-NOAA|PD-USGov-USGS|PD-ineligible|Public_domain";
    internal static readonly Regex People = new(@"\b(people|persons?|wom[ae]n|m[ae]n|girls?|boys?|child(ren)?|kids?|bab(y|ies)|teen(ager)?s?|portraits?|selfies?|famil(y|ies)|weddings?|brides?|nudes?|crowds?|tourists?|actors?|actress(es)?|politicians?|celebrit(y|ies))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        var terms = req.SearchTerms.Take(3).Select(t => t.Contains(' ') ? $"\"{t}\"" : t).ToList();
        var q = string.Join(" OR ", terms);
        var list = new List<LiveHit>();
        foreach (var curated in new[] { true, false })
        {
            if (list.Count >= want) break;
            var search = $"{q} incategory:{FreeCats}" + (curated ? $" incategory:{Curated}" : " filew:>1599");
            var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2&generator=search&gsrnamespace=6&gsrlimit=30" +
                      "&prop=imageinfo&iiprop=url|size|mime|extmetadata&iiurlwidth=1920&iiextmetadatafilter=License|LicenseShortName|LicenseUrl|Artist|Restrictions|ImageDescription|Categories|ObjectName|NonFree" +
                      "&gsrsearch=" + Uri.EscapeDataString(search);
            var json = await LiveAssetSource.JsonAsync(http, url, ct).ConfigureAwait(false);
            var pages = (json?["query"]?["pages"] as JsonArray)?.OfType<JsonObject>().OrderBy(p => (int?)p["index"] ?? 0).ToList() ?? new();
            var hits = pages.Where(p => LiveAssetSource.Relevant(Blob(p), req)).Select(p => ToHit(p, curated)).OfType<LiveHit>().ToList();
            // Sahna yo'nalishiga mos (vertikal sahnaga vertikal) rasmlar oldinga - tartib barqaror.
            if (orientation == "portrait") hits = hits.OrderBy(h => h.Height > h.Width ? 0 : 1).ToList();
            else if (orientation == "landscape") hits = hits.OrderBy(h => h.Width >= h.Height ? 0 : 1).ToList();
            foreach (var h in hits) if (list.All(x => x.Url != h.Url)) list.Add(h);
        }
        return list;
    }

    /// <summary>Moslik tekshiruvi uchun: sarlavha, nom, kategoriyalar va tavsif.</summary>
    internal static string Blob(JsonObject p)
    {
        var m = ((p["imageinfo"] as JsonArray)?.FirstOrDefault() as JsonObject)?["extmetadata"];
        return string.Join(" ", LiveAssetSource.Str(p["title"]).Replace("File:", ""), LiveAssetSource.Str(m?["ObjectName"]?["value"]),
            LiveAssetSource.Str(m?["Categories"]?["value"]).Replace('|', ' '), Tag.Replace(LiveAssetSource.Str(m?["ImageDescription"]?["value"]), " "));
    }

    internal static LiveHit? ToHit(JsonObject p, bool curated)
    {
        var ii = (p["imageinfo"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (ii is null) return null;
        string M(string k) => LiveAssetSource.Str(ii["extmetadata"]?[k]?["value"]);
        string Text(string s, int max = 300) { var t = WebUtility.HtmlDecode(Tag.Replace(s, " ")); t = Regex.Replace(t, @"\s+", " ").Trim(); return t.Length > max ? t[..max] : t; }
        var mime = LiveAssetSource.Str(ii["mime"]);
        if (mime is not ("image/jpeg" or "image/png" or "image/svg+xml" or "image/webp" or "image/tiff")) return null;
        if (M("NonFree").Equals("true", StringComparison.OrdinalIgnoreCase) || Text(M("Restrictions")).Length > 0) return null;
        var title = Text(M("ObjectName"), 200);
        if (title.Length == 0) title = LiveAssetSource.Str(p["title"]).Replace("File:", "");
        if (People.IsMatch($"{LiveAssetSource.Str(p["title"])} {title} {M("Categories")} {Text(M("ImageDescription"))}")) return null;
        var lic = LicenseOf(M("License"), Text(M("LicenseShortName")), M("LicenseUrl"));
        if (lic is null) return null;
        var thumb = LiveAssetSource.Str(ii["thumburl"]);
        if (thumb.Length == 0) thumb = LiveAssetSource.Str(ii["url"]);
        var w = (int?)ii["thumbwidth"] ?? (int?)ii["width"] ?? 0;
        var h = (int?)ii["thumbheight"] ?? (int?)ii["height"] ?? 0;
        var kind = mime == "image/svg+xml" ? "diagram" : "photo";
        if (kind == "photo" && Math.Max(w, h) < 1200) return null;
        return new LiveHit("wikimedia", kind, title, thumb, "", w, h, kind == "diagram", lic.Value.Short, Text(M("Artist"), 120),
            LiveAssetSource.Str(ii["descriptionurl"]), lic.Value.Credit);
    }

    /// <summary>Faqat CC0, Public domain va CC BY (SA, NC, ND, GFDL - yo'q).</summary>
    internal static (string Short, bool Credit)? LicenseOf(string code, string shortName, string url)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        var s = (shortName ?? "").Trim().ToLowerInvariant();
        if (new[] { "-sa", " sa", "-nc", " nc", "-nd", " nd", "sharealike", "noncommercial", "noderiv" }.Any(x => c.Contains(x) || s.Contains(x))) return null;
        if (c is "cc0" or "cc-zero" || s is "cc0" or "cc0 1.0") return ("CC0", false);
        if (c.StartsWith("pd") || s.StartsWith("public domain")) return ("Public domain", false);
        var m = Regex.Match(c.Length > 0 ? c : s.Replace(' ', '-'), @"^cc-by-(\d(?:\.\d)?)$");
        if (m.Success) { var v = m.Groups[1].Value; if (!v.Contains('.')) v += ".0"; return ($"CC BY {v}", true); }
        return null;
    }
}

/// <summary>NASA Image Library: kalitsiz, public domain. Logotip/emblemalar va boshqa mualliflik belgisi borlari olinmaydi.</summary>
internal sealed class NasaLive(HttpClient http) : ILiveProvider
{
    public string Name => "nasa";
    private static readonly Regex Bad = new(@"©|\bcopyright\b|courtesy of|all rights reserved|getty|reuters|associated press|\bafp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Logo = new(@"\b(logo|insignia|emblem|patch|seal|meatball|worm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        var json = await LiveAssetSource.JsonAsync(http, "https://images-api.nasa.gov/search?media_type=image&page_size=40&q=" + Uri.EscapeDataString(req.Query), ct).ConfigureAwait(false);
        var list = new List<LiveHit>();
        foreach (var it in (json?["collection"]?["items"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var d = (it["data"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (d is null) continue;
            var id = LiveAssetSource.Str(d["nasa_id"]);
            var title = LiveAssetSource.Str(d["title"]);
            var kw = (d["keywords"] as JsonArray)?.Select(LiveAssetSource.Str).ToList() ?? new();
            var blob = $"{title} {LiveAssetSource.Str(d["description"])} {string.Join(' ', kw)} {LiveAssetSource.Str(d["photographer"])}";
            if (id.Length == 0 || Bad.IsMatch(blob) || Logo.IsMatch(title + " " + string.Join(' ', kw))) continue;
            // NASA qidiruvi keng: sarlavha yoki kalit so'zlarda sahna so'zlaridan biri bo'lishi shart.
            if (!LiveAssetSource.Relevant($"{title} {string.Join(' ', kw)}", req)) continue;
            var preview = (it["links"] as JsonArray)?.OfType<JsonObject>().Select(l => LiveAssetSource.Str(l["href"])).FirstOrDefault(h => h.Contains('~')) ?? "";
            if (preview.Length == 0) continue;
            var b = preview[..preview.LastIndexOf('~')];
            list.Add(new LiveHit("nasa", "photo", title, b + "~large.jpg", "", 0, 0, false, "Public domain", "NASA", $"https://images.nasa.gov/details/{id}", false,
                Fallbacks: new[] { b + "~orig.jpg", b + "~medium.jpg" }));
            if (list.Count >= want) break;
        }
        return list;
    }
}

// ====================================================================== 3D ikonlar
/// <summary>3dicons.co (CC0): 200+ ikon, 3 rakurs (dynamic, front, iso) va 4 rang; 400 px shaffof webp. Ro'yxat sahifadan bir marta o'qiladi.</summary>
internal sealed class ThreeDIconsLive(HttpClient http) : ILiveProvider
{
    public string Name => "3dicons";
    private static IReadOnlyList<(string Base, string Name, HashSet<string> Words)>? _index;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Regex Item = new(@"(https://[a-z0-9]+\.supabase\.co/storage/v1/object/public/sizes/)([0-9a-f]{6}-([a-z0-9-]+))/", RegexOptions.Compiled);
    private static readonly string[] Looks = { "dynamic/400/color", "iso/400/color", "front/400/premium", "dynamic/400/gradient" };

    internal static IReadOnlyList<(string Base, string Name, HashSet<string> Words)> ParseIndex(string html) =>
        Item.Matches(html).Select(m => (m.Groups[1].Value + m.Groups[2].Value, m.Groups[3].Value, LiveAssetSource.Words(new[] { m.Groups[3].Value })))
            .DistinctBy(x => x.Item1).ToList();

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        if (_index is null)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try { _index ??= ParseIndex(await http.GetStringAsync("https://3dicons.co/explore", ct).ConfigureAwait(false)); }
            finally { Gate.Release(); }
        }
        var words = LiveAssetSource.Words(req.SearchTerms);
        var list = new List<LiveHit>();
        var k = 0;
        foreach (var (b, name, w) in _index.Where(x => x.Words.Overlaps(words)).OrderByDescending(x => x.Words.Count(words.Contains)))
        {
            list.Add(new LiveHit("3dicons", "icon3d", name.Replace('-', ' '), $"{b}/{Looks[k++ % Looks.Length]}.webp", "", 400, 400, true, "CC0",
                "Vijay Verma (3dicons)", "https://3dicons.co", false));
            if (list.Count >= want) break;
        }
        return list;
    }
}

/// <summary>
/// Icons8 3D Fluency: bepul ishlatish - icons8.com'ga havola bilan (video tavsifiga yoziladi). Nom qidiruvdan (kalitsiz) va
/// so'zlardan olinadi, 500 px PNG mavjud bo'lsa olinadi (yo'q bo'lsa 404 - o'tkaziladi). Kesh qilinmaydi: har video o'zi so'raydi.
/// </summary>
internal sealed class Icons8ThreeDLive(HttpClient http) : ILiveProvider
{
    public string Name => "icons8";
    internal const string Credit = "3D icons by Icons8 (https://icons8.com)";

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var t in req.SearchTerms.Take(3)) names.Add(Slug(t));
        try
        {
            var json = await LiveAssetSource.JsonAsync(http, "https://search.icons8.com/api/iconsets/v5/search?amount=40&language=en&term=" + Uri.EscapeDataString(req.Query), ct).ConfigureAwait(false);
            foreach (var i in (json?["icons"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) names.Add(LiveAssetSource.Str(i["commonName"]));
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var words = LiveAssetSource.Words(req.SearchTerms);
        var list = new List<LiveHit>();
        var bases = new HashSet<string>();
        foreach (var n in names.Where(n => n.Length > 1).Distinct().Where(n => LiveAssetSource.Words(new[] { n }).Overlaps(words)).Take(10))
        {
            if (!bases.Add(Regex.Replace(n, "--v\\d+$", ""))) continue;
            if (list.Count >= want) break;
            var url = $"https://img.icons8.com/3d-fluency/500/{n}.png";
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(head, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) continue;
            list.Add(new LiveHit("icons8", "icon3d", n.Replace('-', ' '), url, "", 500, 500, true, "Icons8 free license (link)", "Icons8", "https://icons8.com", true, Credit));
        }
        return list;
    }

    internal static string Slug(string s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-"), "^-|-$", "");
}

// ====================================================================== 3D modellar
/// <summary>Poly Haven 3D modellari (CC0): glTF 1k (tekstura bilan, bir necha MB). Ro'yxat bir marta, moslik nom/teg/kategoriya bo'yicha.</summary>
internal sealed class PolyHavenModels(HttpClient http) : ILiveProvider
{
    public string Name => "polyhaven";
    private static List<(string Id, string Name, HashSet<string> Words, string Authors)>? _all;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        if (_all is null)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_all is null)
                {
                    var json = await LiveAssetSource.JsonAsync(http, "https://api.polyhaven.com/assets?t=models", ct).ConfigureAwait(false) as JsonObject;
                    _all = json?.Select(kv => (kv.Key, LiveAssetSource.Str(kv.Value?["name"]),
                        LiveAssetSource.Words(new[] { LiveAssetSource.Str(kv.Value?["name"]) }
                            .Concat((kv.Value?["tags"] as JsonArray)?.Select(LiveAssetSource.Str) ?? Enumerable.Empty<string>())
                            .Concat((kv.Value?["categories"] as JsonArray)?.Select(LiveAssetSource.Str) ?? Enumerable.Empty<string>())),
                        string.Join(", ", (kv.Value?["authors"] as JsonObject)?.Select(a => a.Key) ?? Enumerable.Empty<string>()))).ToList() ?? new();
                }
            }
            finally { Gate.Release(); }
        }
        // Moslik: avval sahnaning asosiy so'rovi (aniq obyekt), keyin kalit so'zlar.
        var main = LiveAssetSource.Words(new[] { req.Query });
        var words = LiveAssetSource.Words(req.SearchTerms);
        var picks = _all.Select(m => (m, Score: m.Words.Count(main.Contains) * 3 + m.Words.Count(words.Contains)))
            .Where(x => x.Score >= 3).OrderByDescending(x => x.Score).Take(want).ToList();
        var list = new List<LiveHit>();
        foreach (var (m, _) in picks)
        {
            var files = await LiveAssetSource.JsonAsync(http, $"https://api.polyhaven.com/files/{m.Id}", ct).ConfigureAwait(false);
            var g = files?["gltf"]?["1k"]?["gltf"];
            var url = LiveAssetSource.Str(g?["url"]);
            if (url.Length == 0) continue;
            var extra = (g?["include"] as JsonObject)?.Select(kv => (kv.Key, LiveAssetSource.Str(kv.Value?["url"]))).ToList() ?? new();
            list.Add(new LiveHit("polyhaven", "model", m.Name, url, $"https://cdn.polyhaven.com/asset_img/thumbs/{m.Id}.png?width=512&height=512", 0, 0, true,
                "CC0", m.Authors, $"https://polyhaven.com/a/{m.Id}", false, "", extra));
        }
        return list;
    }
}

/// <summary>
/// Objaverse (Sketchfab'dagi CC BY / CC0 modellar): qidiruv Sketchfab ochiq API'si orqali, GLB fayl Objaverse'ning Hugging Face
/// nusxasidan (uid -> yo'l indeksi kompyuterda bir marta keshlanadi, ~20 MB). NC/SA/ND litsenziyalar, katta (16 MB+) va og'ir
/// (300k+ uchburchak) modellar olinmaydi. CC BY - muallif video tavsifiga yoziladi.
/// </summary>
internal sealed class ObjaverseModels(HttpClient http, string cacheDir) : ILiveProvider
{
    public string Name => "objaverse";
    private readonly ObjaverseIndex _index = new(http, Path.Combine(cacheDir, "objaverse"));

    public async Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    {
        var json = await LiveAssetSource.JsonAsync(http, "https://api.sketchfab.com/v3/search?type=models&downloadable=true&count=24&q=" + Uri.EscapeDataString(req.Query), ct).ConfigureAwait(false);
        var list = new List<LiveHit>();
        foreach (var m in (json?["results"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (list.Count >= want) break;
            var label = LiveAssetSource.Str(m["license"]?["label"]);
            var lic = label switch { "CC Attribution" => "CC BY 4.0", "CC0 Public Domain" => "CC0", _ => "" };
            if (lic.Length == 0) continue;
            var size = (long?)m["archives"]?["glb"]?["size"] ?? long.MaxValue;
            var faces = (long?)m["faceCount"] ?? long.MaxValue;
            if (size > 16L * 1024 * 1024 || faces > 300_000 || (bool?)m["isAgeRestricted"] == true) continue;
            // Model nomi yoki teglari sahnaga mos bo'lishi shart ("honey bee" so'roviga "Bee-gypt" dioramasi kelmasin).
            var tags = string.Join(' ', (m["tags"] as JsonArray)?.OfType<JsonObject>().Select(x => LiveAssetSource.Str(x["name"])) ?? Enumerable.Empty<string>());
            if (!LiveAssetSource.Relevant($"{LiveAssetSource.Str(m["name"])} {tags}", req)) continue;
            var uid = LiveAssetSource.Str(m["uid"]);
            var path = await _index.PathOfAsync(uid, ct).ConfigureAwait(false);
            if (path is null) continue;
            var thumb = (m["thumbnails"]?["images"] as JsonArray)?.OfType<JsonObject>().OrderByDescending(i => (int?)i["width"] ?? 0)
                .Select(i => LiveAssetSource.Str(i["url"])).FirstOrDefault(u => u.Length > 0) ?? "";
            var user = LiveAssetSource.Str(m["user"]?["displayName"]);
            if (user.Length == 0) user = LiveAssetSource.Str(m["user"]?["username"]);
            var page = LiveAssetSource.Str(m["viewerUrl"]);
            list.Add(new LiveHit("objaverse", "model", LiveAssetSource.Str(m["name"]), $"https://huggingface.co/datasets/allenai/objaverse/resolve/main/{path}",
                thumb, 0, 0, true, lic, user, page, lic != "CC0"));
        }
        return list;
    }
}

/// <summary>
/// Objaverse uid -> glbs/NNN-NNN/uid.glb. object-paths.json.gz bir marta yuklanadi va qat'iy uzunlikdagi saralangan qatorlarga
/// ("uid NNN\n", 37 bayt) aylantiriladi: qidiruv diskda ikkilik qidiruv bilan (xotiraga yuklanmaydi). 60 kundan eski bo'lsa yangilanadi.
/// </summary>
internal sealed class ObjaverseIndex(HttpClient http, string dir)
{
    private const int Line = 37;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string File_ => Path.Combine(dir, "uids.idx");

    public async Task<string?> PathOfAsync(string uid, CancellationToken ct)
    {
        if (uid.Length != 32) return null;
        await EnsureAsync(ct).ConfigureAwait(false);
        using var f = new FileStream(File_, FileMode.Open, FileAccess.Read, FileShare.Read);
        long lo = 0, hi = f.Length / Line - 1;
        var buf = new byte[Line];
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            f.Seek(mid * Line, SeekOrigin.Begin);
            f.ReadExactly(buf);
            var key = Encoding.ASCII.GetString(buf, 0, 32);
            var c = string.CompareOrdinal(key, uid);
            if (c == 0) { var n = int.Parse(Encoding.ASCII.GetString(buf, 33, 3), CultureInfo.InvariantCulture); return $"glbs/000-{n:000}/{uid}.glb"; }
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (File.Exists(File_) && File.GetLastWriteTimeUtc(File_) > DateTime.UtcNow.AddDays(-60)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(File_) && File.GetLastWriteTimeUtc(File_) > DateTime.UtcNow.AddDays(-60)) return;
            Directory.CreateDirectory(dir);
            using var resp = await http.GetAsync("https://huggingface.co/datasets/allenai/objaverse/resolve/main/object-paths.json.gz", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gz, cancellationToken: ct).ConfigureAwait(false);
            var rows = new List<string>(820_000);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var m = Regex.Match(p.Value.GetString() ?? "", @"^glbs/000-(\d{3})/");
                if (p.Name.Length == 32 && m.Success) rows.Add($"{p.Name} {m.Groups[1].Value}\n");
            }
            rows.Sort(StringComparer.Ordinal);
            var tmp = File_ + ".tmp";
            await File.WriteAllTextAsync(tmp, string.Concat(rows), Encoding.ASCII, ct).ConfigureAwait(false);
            File.Move(tmp, File_, true);
        }
        finally { Gate.Release(); }
    }
}
