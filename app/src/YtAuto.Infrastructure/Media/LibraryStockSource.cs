using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Umumiy rasm kutubxonasidan (ytauto.assets) sahna nomzodlari: har sahna turi uchun mos rasm turlari (hero - fotolar, collage -
/// shaffof cutout/renderlar...) va soni (elementlar × 2 + 1). Fayllar faqat ruxsat etilgan hostlardan (Wikimedia, NASA, Supabase
/// Storage) https orqali yuklanadi. Natija OpenFilm stok natijasi bilan bir xil shaklda: keyingi tekshiruv, kontakt varaq va
/// Claude tanlovi o'zgarmaydi.
/// </summary>
public sealed class LibraryStockSource : IStockMediaSource
{
    private const long MaxBytes = 30L * 1024 * 1024;
    private static readonly string[] AllowedHostSuffixes = { ".wikimedia.org", ".nasa.gov", ".supabase.co", ".polyhaven.org" };
    private readonly IAssetCatalog _catalog;
    private readonly HttpClient _http;

    public LibraryStockSource(IAssetCatalog catalog, HttpClient? http = null)
    {
        _catalog = catalog;
        _http = http ?? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("YtAuto/1.0 (educational video tool)");
    }

    public bool IsConfigured => true;

    /// <summary>
    /// Sinov uchun: YTAUTO_LIBRARY_TEST_ORIGIN (masalan http://127.0.0.1:8765) - faqat lokal manzil qo'shimcha ruxsat oladi.
    /// </summary>
    public static bool Allowed(Uri u)
    {
        var test = Environment.GetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN");
        if (!string.IsNullOrWhiteSpace(test) && Uri.TryCreate(test, UriKind.Absolute, out var t) && (t.IsLoopback)
            && string.Equals(u.GetLeftPart(UriPartial.Authority), t.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return true;
        return u.Scheme == Uri.UriSchemeHttps && AllowedHostSuffixes.Any(s => ("." + u.Host).EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<StockCandidates> FetchAsync(IReadOnlyList<StockRequest> scenes, string workDir, StockOptions options, IJobLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new StockCandidates();
        var used = new HashSet<long>();
        var channel = options.Channel ?? "";
        foreach (var req in scenes)
        {
            ct.ThrowIfCancellationRequested();
            var type = SceneTypes.Normalize(req.SceneType);
            var scene = new StockScene { Id = req.Id, Query = req.Query, Purpose = req.Purpose, At = req.At, SceneType = type, Elements = req.Elements };
            result.Scenes.Add(scene);
            var want = SceneTypes.CandidatesFor(type, req.Elements);
            if (type == SceneTypes.Typography) want = Math.Min(want, 2);
            IReadOnlyList<AssetHit> hits;
            try
            {
                hits = await _catalog.SearchAsync(new AssetQuery(req.SearchTerms, SceneTypes.AssetKindsFor(type), want + 4,
                    options.Orientation, 900, channel, 60, used), ct).ConfigureAwait(false);
            }
            catch (AssetLibraryUnavailableException) { throw; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                scene.Notes.Add("kutubxona qidiruvi xato: " + e.Message);
                continue;
            }
            var dir = Path.Combine(workDir, req.Id);
            Directory.CreateDirectory(dir);
            var n = 0;
            foreach (var h in hits)
            {
                if (scene.Candidates.Count >= want) break;
                if (!Uri.TryCreate(h.FileUrl, UriKind.Absolute, out var uri) || !Allowed(uri)) { scene.Notes.Add($"#{h.Id}: ruxsatsiz manzil"); continue; }
                var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) ext = h.HasAlpha ? ".png" : ".jpg";
                var path = Path.Combine(dir, $"lib{++n}{ext}");
                try
                {
                    await DownloadAsync(uri, path, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    try { File.Delete(path); } catch { }
                    scene.Notes.Add($"#{h.Id} yuklanmadi: {e.Message}");
                    continue;
                }
                used.Add(h.Id);
                scene.Candidates.Add(new StockCandidate
                {
                    Type = StockTypes.Image, File = path, Width = h.Width, Height = h.Height, Provider = "library:" + h.Source,
                    Credit = h.CreditLine(), Page = h.PageUrl, AssetId = h.Id, Kind = h.Kind, HasAlpha = h.HasAlpha, License = h.License,
                });
            }
            if (scene.Candidates.Count == 0 && hits.Count == 0) scene.Notes.Add("kutubxonada mos rasm topilmadi");
        }
        var total = result.Scenes.Sum(s => s.Candidates.Count);
        log.Info($"Kutubxona: {result.Scenes.Count(s => s.Candidates.Count > 0)}/{result.Scenes.Count} sahnaga {total} ta rasm " +
                 $"({result.Scenes.SelectMany(s => s.Candidates).Count(x => x.HasAlpha)} tasi shaffof cutout/render).", "Media");
        return result;
    }

    private async Task DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.RequestMessage?.RequestUri is { } final && !Allowed(final)) throw new InvalidOperationException("yo'naltirish ruxsatsiz manzilga");
        if (resp.Content.Headers.ContentLength is long len && len > MaxBytes) throw new InvalidOperationException($"juda katta ({len / 1048576} MB)");
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type != "application/octet-stream")
            throw new InvalidOperationException("rasm emas: " + type);
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBytes) throw new InvalidOperationException("juda katta");
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (total < 1024) throw new InvalidOperationException("fayl bo'sh");
    }
}
