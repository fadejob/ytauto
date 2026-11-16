using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Botdan yuklangan tayyor ovozni (Supabase Storage imzolangan havolasi) yuklab olib WAV'ga o'tkazadi.
/// Xavfsizlik: faqat https://*.supabase.co manzillari va hajm cheklovi; yuklangan fayl faqat ffmpeg orqali o'qiladi.
/// </summary>
public sealed class RemoteAudioImporter : IAudioImporter
{
    public const long MaxBytes = 200L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly FfmpegMediaProbe _media;

    public RemoteAudioImporter(HttpClient http, FfmpegMediaProbe media) { _http = http; _media = media; }

    public async Task ImportAsync(string url, string outWav, IJobLog log, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
            throw PipelineException.Config("manual_audio_url", "Ovoz havolasi ishonchsiz (faqat https://*.supabase.co).");

        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (ext.Length is < 2 or > 6) ext = ".bin";
        var download = outWav + ".download" + ext;
        try
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw PipelineException.Transient("manual_audio_network", "Ovozni yuklab olishda tarmoq xatosi: " + e.Message); }
            using (resp)
            {
                var code = (int)resp.StatusCode;
                if (code is 400 or 403 or 404)
                    throw PipelineException.Config("manual_audio_gone", $"Yuklangan ovoz Storage'da topilmadi yoki havola muddati o'tgan (HTTP {code}). Botda audioni qayta yuklang.");
                if (!resp.IsSuccessStatusCode) throw PipelineException.Transient("manual_audio_http", $"Ovozni yuklab olib bo'lmadi (HTTP {code}).");
                if (resp.Content.Headers.ContentLength is > MaxBytes) throw PipelineException.Config("manual_audio_big", "Ovoz fayli juda katta.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(download);
                var buf = new byte[256 * 1024]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    done += n;
                    if (done > MaxBytes) throw PipelineException.Config("manual_audio_big", "Ovoz fayli ruxsat etilgan hajmdan oshdi.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                log.Info($"Ovoz yuklab olindi: {done / 1048576.0:0.0} MB. WAV'ga o'tkazilmoqda.", "Voice");
            }
            await _media.ConvertToWavAsync(download, outWav, ct).ConfigureAwait(false);
        }
        finally { try { File.Delete(download); } catch { } }
    }
}
