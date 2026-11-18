using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Kalitsiz ochiq manbalardan rasm yuklaydi va faqat tijoratda ishlatish mumkin bo'lgan litsenziyalarni oladi:
/// <list type="bullet">
/// <item>Ikon va logolar: Iconify (api.iconify.design). Logolar - "logos" va "simple-icons" (CC0) to'plamlari;
/// ikonlar - MIT/Apache/ISC/CC0/BSD litsenziyali to'plamlar, butun video uchun bitta uslubdagi to'plam afzal.</item>
/// <item>Fotolar: Openverse (CC0, Public Domain Mark, CC BY), bo'lmasa Wikimedia Commons (CC0, Public domain, CC BY).
/// NC, ND va SA litsenziyalari olinmaydi. CC BY mualliflari manifestga yoziladi (YouTube tavsifiga qo'shiladi).</item>
/// </list>
/// Xavfsizlik: faqat https, hajm chegarasi, rasm turi fayl boshidan tekshiriladi, SVG ichidagi skript/hodisa atributlari olib tashlanadi.
/// Hech bir xato vazifani to'xtatmaydi: topilmagan so'rov Skipped ro'yxatiga tushadi.
/// </summary>
public sealed class OpenMediaLibrary : IMediaLibrary
{
    private const string UserAgent = "YtAuto/1.0 (educational video tool)";
    private const long MaxBytes = 15L * 1024 * 1024;
    private readonly HttpClient _http;

    /// <summary>Iconify to'plamlari: bitta videoda ikonlar bir xil uslubda bo'lishi uchun shu tartibda afzal ko'riladi.</summary>
    public static readonly string[] PreferredIconSets = { "ph", "tabler", "lucide", "mdi", "material-symbols", "carbon", "solar", "fluent", "iconoir", "mingcute", "ri", "bi" };
    public static readonly string[] LogoSets = { "logos", "simple-icons" };
    private static readonly string[] PermissiveSpdx = { "MIT", "Apache-2.0", "ISC", "CC0-1.0", "BSD-2-Clause", "BSD-3-Clause", "Unlicense" };

    public OpenMediaLibrary(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(40) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<MediaManifest> FetchAsync(MediaPlan plan, string mediaDir, IJobLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(mediaDir);
        var manifest = new MediaManifest();
        Dictionary<string, (string Spdx, string Title, string Url)>? sets = null;
        try { sets = await IconSetsAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { log.Warn("Iconify to'plamlari ro'yxati olinmadi: " + e.Message, "Media"); }
        string? iconSet = null; // birinchi topilgan ikon to'plami - keyingilari ham shundan
        var n = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var slug = Slug(item.Query);
            try
            {
                MediaAsset? a = item.Kind switch
                {
                    MediaKinds.Logo => await LogoAsync(item, sets, mediaDir, $"logo_{n:00}_{slug}", ct).ConfigureAwait(false),
                    MediaKinds.Icon => await IconAsync(item, sets, mediaDir, $"icon_{n:00}_{slug}", iconSet, ct).ConfigureAwait(false),
                    _ => await PhotoAsync(item, mediaDir, $"photo_{n:00}_{slug}", log, ct).ConfigureAwait(false),
                };
                if (a is null) { manifest.Skipped.Add($"{item.Kind}: {item.Query}"); continue; }
                if (a.Kind == MediaKinds.Icon && iconSet is null) iconSet = a.Provider.Split(':').Last();
                a.Purpose = item.Purpose; a.At = item.At; a.Query = item.Query;
                manifest.Assets.Add(a);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                manifest.Skipped.Add($"{item.Kind}: {item.Query} ({e.Message})");
            }
        }
        log.Info($"Real rasmlar: {manifest.Assets.Count} ta yuklandi ({string.Join(", ", manifest.Assets.GroupBy(a => a.Kind).Select(g => $"{g.Key} {g.Count()}"))})" +
                 (manifest.Skipped.Count > 0 ? $", {manifest.Skipped.Count} ta topilmadi." : "."), "Media");
        return manifest;
    }

    // ------------------------------------------------------------ Iconify

    private async Task<Dictionary<string, (string Spdx, string Title, string Url)>> IconSetsAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("https://api.iconify.design/collections", ct).ConfigureAwait(false);
        var d = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        if (json is JsonObject o)
            foreach (var (k, v) in o)
                d[k] = (v?["license"]?["spdx"]?.GetValue<string>() ?? "", v?["license"]?["title"]?.GetValue<string>() ?? "", v?["license"]?["url"]?.GetValue<string>() ?? "");
        return d;
    }

    internal static bool IconSetAllowed(string spdx) => PermissiveSpdx.Contains(spdx, StringComparer.OrdinalIgnoreCase);

    private async Task<MediaAsset?> LogoAsync(MediaPlanItem item, Dictionary<string, (string Spdx, string Title, string Url)>? sets, string dir, string name, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.iconify.design/search?query={Uri.EscapeDataString(item.Query)}&prefixes={string.Join(",", LogoSets)}&limit=24", ct).ConfigureAwait(false);
        var icons = (json?["icons"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Contains(':')).ToList() ?? new();
        var want = Slug(item.Query);
        // Aniq nom mosligi (masalan "visa") avval, "logos" (rangli) to'plami "simple-icons" (bir rangli) dan oldin; "-icon" variantlari keyin.
        var best = icons
            .Select(i => (Id: i, Prefix: i.Split(':')[0], Name: i.Split(':')[1]))
            .Where(i => sets is null || (sets.TryGetValue(i.Prefix, out var s) && IconSetAllowed(s.Spdx)))
            .OrderBy(i => i.Name == want || i.Name == want + "-icon" ? 0 : i.Name.StartsWith(want) ? 1 : 2)
            .ThenBy(i => Array.IndexOf(LogoSets, i.Prefix))
            .ThenBy(i => i.Name.Length)
            .FirstOrDefault();
        if (best.Id is null || (best.Name != want && !best.Name.StartsWith(want) && !want.StartsWith(best.Name))) return null; // boshqa brend logosini qo'ymaymiz
        return await SaveIconAsync(best.Prefix, best.Name, sets, dir, name, MediaKinds.Logo, ct).ConfigureAwait(false);
    }

    private async Task<MediaAsset?> IconAsync(MediaPlanItem item, Dictionary<string, (string Spdx, string Title, string Url)>? sets, string dir, string name, string? preferredSet, CancellationToken ct)
    {
        var order = preferredSet is null ? PreferredIconSets : new[] { preferredSet }.Concat(PreferredIconSets.Where(p => p != preferredSet)).ToArray();
        // Ko'p so'zli so'rov topilmasa ("warning duplicate") har so'z alohida sinaladi.
        var attempts = new[] { item.Query }.Concat(item.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var icons = new List<string>(); var want = "";
        foreach (var q in attempts)
        {
            var json = await GetJsonAsync($"https://api.iconify.design/search?query={Uri.EscapeDataString(q)}&prefixes={string.Join(",", order)}&limit=64", ct).ConfigureAwait(false);
            icons = (json?["icons"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Contains(':')).ToList() ?? new();
            want = Slug(q);
            if (icons.Count > 0) break;
        }
        var best = icons
            .Select(i => (Id: i, Prefix: i.Split(':')[0], Name: i.Split(':')[1]))
            .Where(i => sets is null || (sets.TryGetValue(i.Prefix, out var s) && IconSetAllowed(s.Spdx)))
            .OrderBy(i => Array.IndexOf(order, i.Prefix) is var k && k < 0 ? 99 : k)
            .ThenBy(i => i.Name == want ? 0 : i.Name.StartsWith(want) ? 1 : 2)
            .ThenBy(i => i.Name.Contains("fill") || i.Name.Contains("duotone") || i.Name.Contains("thin") || i.Name.Contains("light") ? 1 : 0)
            .ThenBy(i => i.Name.Length)
            .FirstOrDefault();
        if (best.Id is null) return null;
        return await SaveIconAsync(best.Prefix, best.Name, sets, dir, name, MediaKinds.Icon, ct).ConfigureAwait(false);
    }

    private async Task<MediaAsset> SaveIconAsync(string prefix, string iconName, Dictionary<string, (string Spdx, string Title, string Url)>? sets, string dir, string name, string kind, CancellationToken ct)
    {
        var svg = await GetStringLimitedAsync($"https://api.iconify.design/{prefix}/{iconName}.svg?height=auto", 512 * 1024, ct).ConfigureAwait(false);
        if (!svg.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SVG emas");
        File.WriteAllText(Path.Combine(dir, name + ".svg"), SanitizeSvg(svg), new UTF8Encoding(false));
        var lic = sets is not null && sets.TryGetValue(prefix, out var s) ? s : ("", "", "");
        return new MediaAsset
        {
            File = name + ".svg", Kind = kind, Title = $"{prefix}:{iconName}", Provider = $"iconify:{prefix}",
            License = string.IsNullOrEmpty(lic.Item1) ? lic.Item2 : lic.Item1, LicenseUrl = lic.Item3,
            SourceUrl = $"https://icon-sets.iconify.design/{prefix}/{iconName}/", NeedsCredit = false,
        };
    }

    /// <summary>Skript, hodisa atributlari, tashqi havolalar va foreignObject olib tashlanadi (SVG brauzer sahifasida ishlatiladi).</summary>
    internal static string SanitizeSvg(string svg)
    {
        svg = Regex.Replace(svg, @"<script[\s\S]*?</script\s*>", "", RegexOptions.IgnoreCase);
        svg = Regex.Replace(svg, @"<foreignObject[\s\S]*?</foreignObject\s*>", "", RegexOptions.IgnoreCase);
        svg = Regex.Replace(svg, @"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*')", "", RegexOptions.IgnoreCase);
        svg = Regex.Replace(svg, @"(href\s*=\s*[""'])\s*(https?:|javascript:|data:text)[^""']*", "$1#", RegexOptions.IgnoreCase);
        return svg;
    }

    // ------------------------------------------------------------ Fotolar

    private async Task<MediaAsset?> PhotoAsync(MediaPlanItem item, string dir, string name, IJobLog log, CancellationToken ct)
    {
        try
        {
            var a = await OpenverseAsync(item.Query, dir, name, ct).ConfigureAwait(false);
            if (a is not null) return a;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Debug($"Openverse ({item.Query}): {e.Message}", "Media");
        }
        return await WikimediaAsync(item.Query, dir, name, ct).ConfigureAwait(false);
    }

    private async Task<MediaAsset?> OpenverseAsync(string query, string dir, string name, CancellationToken ct)
    {
        var url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&license=cc0,pdm,by&mature=false&page_size=20";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var results = (json?["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(r => (r["mature"]?.GetValue<bool>() ?? false) == false)
            .Select(r => new
            {
                Url = Str(r["url"]), Title = Str(r["title"]), Author = Str(r["creator"]), Lic = Str(r["license"]).ToLowerInvariant(),
                Ver = Str(r["license_version"]), LicUrl = Str(r["license_url"]), Page = Str(r["foreign_landing_url"]),
                W = Int(r["width"]), H = Int(r["height"]), Type = Str(r["filetype"]).ToLowerInvariant(),
            })
            .Where(r => r.Url.StartsWith("https://") && r.Lic is "cc0" or "pdm" or "by" && r.Type is "" or "jpg" or "jpeg" or "png" or "webp")
            .Where(r => r.W == 0 || r.W >= 800)
            .OrderBy(r => r.W == 0 ? 1 : 0).ThenByDescending(r => r.W >= r.H ? 1 : 0).ThenByDescending(r => Math.Min(r.W, 3000))
            .Take(4).ToList();
        foreach (var r in results)
        {
            var file = await TryDownloadImageAsync(r.Url, dir, name, ct).ConfigureAwait(false);
            if (file is null) continue;
            var (lic, credit) = r.Lic switch
            {
                "cc0" => ("CC0", false),
                "pdm" => ("Public domain", false),
                _ => ($"CC BY {r.Ver}".Trim(), true),
            };
            return new MediaAsset
            {
                File = file.Value.File, Kind = MediaKinds.Photo, Title = r.Title, Author = r.Author, License = lic, LicenseUrl = r.LicUrl,
                SourceUrl = r.Page, Provider = "openverse", Width = file.Value.W > 0 ? file.Value.W : r.W, Height = file.Value.H > 0 ? file.Value.H : r.H, NeedsCredit = credit,
            };
        }
        return null;
    }

    /// <summary>Wikimedia Commons litsenziyasi: CC0, Public domain (PD-*), CC BY x.y (SA/NC/ND emas). Qaytaradi: (qisqa nom, muallif kerakmi) yoki null.</summary>
    internal static (string License, bool Credit)? WikimediaLicense(string shortName)
    {
        var s = (shortName ?? "").Trim();
        if (Regex.IsMatch(s, @"^(CC0|CC0 1\.0)$", RegexOptions.IgnoreCase)) return ("CC0", false);
        if (Regex.IsMatch(s, @"^(Public domain|PD\b|PD-)", RegexOptions.IgnoreCase)) return ("Public domain", false);
        var m = Regex.Match(s, @"^CC BY (\d(\.\d)?)$", RegexOptions.IgnoreCase);
        if (m.Success) return ($"CC BY {m.Groups[1].Value}", true);
        return null;
    }

    private async Task<MediaAsset?> WikimediaAsync(string query, string dir, string name, CancellationToken ct)
    {
        var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6&gsrlimit=15" +
                  $"&gsrsearch={Uri.EscapeDataString(query + " filetype:bitmap")}" +
                  "&prop=imageinfo&iiprop=url|size|mime|extmetadata&iiurlwidth=1600&iiextmetadatafilter=LicenseShortName|Artist|LicenseUrl|ObjectName";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var pages = (json?["query"]?["pages"] as JsonObject)?.Select(kv => kv.Value).OfType<JsonObject>()
            .OrderBy(p => Int(p["index"])).ToList() ?? new();
        foreach (var p in pages)
        {
            var ii = (p["imageinfo"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (ii is null) continue;
            var mime = Str(ii["mime"]);
            if (mime is not ("image/jpeg" or "image/png" or "image/webp")) continue;
            int w = Int(ii["width"]), h = Int(ii["height"]);
            if (w < 800) continue;
            var meta = ii["extmetadata"];
            var lic = WikimediaLicense(Str(meta?["LicenseShortName"]?["value"]));
            if (lic is null) continue;
            var src = Str(ii["thumburl"]); if (!src.StartsWith("https://")) src = Str(ii["url"]);
            var file = await TryDownloadImageAsync(src, dir, name, ct).ConfigureAwait(false);
            if (file is null) continue;
            var title = Str(meta?["ObjectName"]?["value"]);
            if (title.Length == 0) title = Str(p["title"]).Replace("File:", "");
            return new MediaAsset
            {
                File = file.Value.File, Kind = MediaKinds.Photo, Title = StripHtml(title), Author = StripHtml(Str(meta?["Artist"]?["value"])),
                License = lic.Value.License, LicenseUrl = Str(meta?["LicenseUrl"]?["value"]), SourceUrl = Str(ii["descriptionurl"]),
                Provider = "wikimedia", Width = file.Value.W > 0 ? file.Value.W : w, Height = file.Value.H > 0 ? file.Value.H : h, NeedsCredit = lic.Value.Credit,
            };
        }
        return null;
    }

    // ------------------------------------------------------------ HTTP yordamchilari

    private async Task<(string File, int W, int H)?> TryDownloadImageAsync(string url, string dir, string name, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
            using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is long len && len > MaxBytes) return null;
            var bytes = await ReadLimitedAsync(resp, MaxBytes, ct).ConfigureAwait(false);
            var ext = ImageExtension(bytes);
            if (ext is null) return null;
            var file = name + ext;
            await File.WriteAllBytesAsync(Path.Combine(dir, file), bytes, ct).ConfigureAwait(false);
            var (w, h) = ImageSize(bytes);
            return (file, w, h);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Fayl boshidagi belgilar bo'yicha rasm turi (Content-Type'ga ishonilmaydi).</summary>
    internal static string? ImageExtension(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 12 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return ".webp";
        return null;
    }

    /// <summary>PNG va JPEG o'lchami (boshqalar uchun 0,0).</summary>
    internal static (int W, int H) ImageSize(byte[] b)
    {
        try
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    var segLen = (b[i + 2] << 8) | b[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                        return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + segLen;
                }
            }
        }
        catch { }
        return (0, 0);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage resp, long max, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int r;
        while ((r = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, r);
            if (ms.Length > max) throw new InvalidDataException("fayl juda katta");
        }
        return ms.ToArray();
    }

    private async Task<string> GetStringLimitedAsync(string url, long max, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return Encoding.UTF8.GetString(await ReadLimitedAsync(resp, max, ct).ConfigureAwait(false));
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        return JsonNode.Parse(Encoding.UTF8.GetString(await ReadLimitedAsync(resp, 8 * 1024 * 1024, ct).ConfigureAwait(false)));
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static int Int(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : 0) : 0;
    internal static string StripHtml(string s) => System.Net.WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<[^>]+>", "")).Trim();

    internal static string Slug(string s)
    {
        var x = Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return x.Length > 30 ? x[..30].TrimEnd('-') : x.Length == 0 ? "item" : x;
    }
}
