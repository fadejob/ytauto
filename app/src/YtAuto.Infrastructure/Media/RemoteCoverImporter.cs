using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Botda mavzuga yuklangan muqova rasmini (Supabase Storage imzolangan havolasi) yuklab oladi. Rasm keyin ffmpeg orqali
/// YouTube thumbnail o'lchamidagi JPEG'ga aylantiriladi (JobRunner). Xavfsizlik: faqat https://*.supabase.co va hajm cheklovi.
/// </summary>
public sealed class RemoteCoverImporter : ICoverImporter
{
    public const long MaxBytes = 20L * 1024 * 1024;
    private readonly HttpClient _http;

    public RemoteCoverImporter(HttpClient http) { _http = http; }

    public async Task DownloadAsync(string url, string outFile, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
            throw PipelineException.Config("manual_cover_url", "Muqova havolasi ishonchsiz (faqat https://*.supabase.co).");
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw PipelineException.Transient("manual_cover_network", "Muqovani yuklab olishda tarmoq xatosi: " + e.Message); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw PipelineException.Config("manual_cover_http", $"Muqova yuklab olinmadi (HTTP {(int)resp.StatusCode}) — havola eskirgan bo'lishi mumkin.");
            if (resp.Content.Headers.ContentLength is > MaxBytes) throw PipelineException.Config("manual_cover_size", "Muqova rasmi juda katta (20 MB gacha).");
            var tmp = outFile + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) throw PipelineException.Config("manual_cover_size", "Muqova rasmi juda katta (20 MB gacha).");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            File.Move(tmp, outFile, true);
        }
    }
}
