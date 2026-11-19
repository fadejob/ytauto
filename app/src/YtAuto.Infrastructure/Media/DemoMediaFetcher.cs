using YtAuto.Core.Demo;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// MCP orqali yuborilgan materialni (Supabase Storage imzolangan havolasi) yuklab oladi.
/// Xavfsizlik: faqat https va ruxsat etilgan host (Storage), hajm chegarasi, rasm formati fayl boshidan tekshiriladi;
/// video faqat ffmpeg orqali o'qiladi va ovozsiz H.264 ga o'tkaziladi. Fayl nomi worker tomonidan beriladi (yo'l buzib bo'lmaydi).
/// </summary>
public sealed class DemoMediaFetcher : IDemoMediaFetcher
{
    public const long MaxImageBytes = 25L * 1024 * 1024;
    public const long MaxVideoBytes = 500L * 1024 * 1024;
    public const double MaxVideoSec = 180;
    private readonly HttpClient _http;
    private readonly FfmpegMediaProbe _media;
    private readonly Func<Uri, bool> _allowed;

    /// <param name="allowed">Ruxsat etilgan manzil (standart: https://*.supabase.co).</param>
    public DemoMediaFetcher(HttpClient http, FfmpegMediaProbe media, Func<Uri, bool>? allowed = null)
    {
        _http = http; _media = media;
        _allowed = allowed ?? DefaultAllowed;
    }

    public static bool DefaultAllowed(Uri u) => u.Scheme == Uri.UriSchemeHttps && u.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase);

    public async Task<string> FetchAsync(DemoMedia m, string outStem, IJobLog log, CancellationToken ct)
    {
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri) || !_allowed(uri))
            throw PipelineException.Config("demo_media_url", $"Material havolasi ishonchsiz: {m.Id}.");
        var max = m.IsVideo ? MaxVideoBytes : MaxImageBytes;
        var part = outStem + ".download";
        try
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw PipelineException.Transient("demo_media_network", "Materialni yuklab olishda tarmoq xatosi: " + e.Message); }
            using (resp)
            {
                var code = (int)resp.StatusCode;
                if (code is 400 or 403 or 404)
                    throw PipelineException.Config("demo_media_gone", $"Material Storage'da topilmadi yoki havola muddati o'tgan (HTTP {code}): {m.Id}. Claude Code'dan materiallarni qayta yuboring.");
                if (!resp.IsSuccessStatusCode) throw PipelineException.Transient("demo_media_http", $"Materialni yuklab olib bo'lmadi (HTTP {code}).");
                if (resp.Content.Headers.ContentLength is long len && len > max) throw PipelineException.Config("demo_media_big", $"Material juda katta: {m.Id}.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(part);
                var buf = new byte[256 * 1024]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    done += n;
                    if (done > max) throw PipelineException.Config("demo_media_big", $"Material ruxsat etilgan hajmdan oshdi: {m.Id}.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            if (m.IsVideo)
            {
                var outMp4 = outStem + ".mp4";
                await _media.NormalizeScreenRecordingAsync(part, outMp4, MaxVideoSec, ct).ConfigureAwait(false);
                return outMp4;
            }
            var ext = SniffImage(part) ?? throw PipelineException.Config("demo_media_type", $"Screenshot PNG, JPEG, WebP yoki GIF bo'lishi kerak: {m.Id}.");
            var final = outStem + ext;
            File.Move(part, final, true);
            return final;
        }
        finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
    }

    /// <summary>Fayl boshidan rasm formati (kengaytma bilan) yoki null.</summary>
    internal static string? SniffImage(string path)
    {
        var h = new byte[12];
        using (var f = File.OpenRead(path)) { if (f.Read(h, 0, h.Length) < 12) return null; }
        if (h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return ".png";
        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return ".gif";
        return null;
    }
}
