using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YtAuto.Core.Demo;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Tayyor demo videoni serverga (Supabase Storage) yuboradi: bot shu kompyuter tokeni bilan (Authorization: Bearer) har fayl uchun
/// bir martalik yuklash havolasini beradi, fayl to'g'ridan-to'g'ri Storage'ga PUT qilinadi. Mehmon vazifasi bo'lsa bot faylni mehmon
/// hisobi papkasiga yozadi (faqat shu kompyuter hisobi mezbon bo'lgan mehmon).
/// </summary>
public sealed class BotDemoResultSink : IDemoResultSink
{
    private readonly HttpClient _http;
    private readonly Func<(string BotUrl, string Token)?> _account;
    private readonly Func<Guid?> _guest;

    public BotDemoResultSink(HttpClient http, Func<(string BotUrl, string Token)?> account, Func<Guid?> guest)
    {
        _http = http; _account = account; _guest = guest;
    }

    private sealed record FileReq([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("size")] long Size);
    private sealed record Upload(
        [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("uploadUrl")] string UploadUrl);
    private sealed record Prepared([property: JsonPropertyName("uploads")] List<Upload>? Uploads, [property: JsonPropertyName("error")] string? Error);

    public async Task<DemoResult> DeliverAsync(VideoJobRef job, string videoPath, string? coverPath, string? subtitlesPath, IJobLog log, CancellationToken ct)
    {
        var acc = _account() ?? throw PipelineException.Config("demo_no_account", "Kompyuter hisobi (bot manzili/token) sozlanmagan - natija serverga yuborilmadi.");
        var files = new List<(string Name, string Path, string Type)> { ("video.mp4", videoPath, "video/mp4") };
        if (coverPath is not null && File.Exists(coverPath)) files.Add(("cover.jpg", coverPath, "image/jpeg"));
        if (subtitlesPath is not null && File.Exists(subtitlesPath)) files.Add(("subtitles.srt", subtitlesPath, "application/x-subrip"));

        using var req = new HttpRequestMessage(HttpMethod.Post, acc.BotUrl.TrimEnd('/') + "/api/device/demo-result")
        {
            Content = JsonContent.Create(new { jobId = job.Id, guest = _guest()?.ToString(), files = files.Select(f => new FileReq(f.Name, new FileInfo(f.Path).Length)) }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.Token);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw PipelineException.Transient("demo_result_network", "Bot bilan tarmoq xatosi: " + e.Message); }
        Prepared? prep;
        using (resp)
        {
            try { prep = await resp.Content.ReadFromJsonAsync<Prepared>(ct).ConfigureAwait(false); } catch { prep = null; }
            if ((int)resp.StatusCode >= 500) throw PipelineException.Transient("demo_result_bot", $"Bot xatosi (HTTP {(int)resp.StatusCode}): {prep?.Error}");
            if (!resp.IsSuccessStatusCode || prep?.Uploads is null)
                throw PipelineException.Config("demo_result_rejected", $"Bot natija yuklashni rad etdi (HTTP {(int)resp.StatusCode}): {prep?.Error}");
        }

        var result = new DemoResult { DeliveredAtUtc = DateTimeOffset.UtcNow, DurationSec = job.DurationSec };
        foreach (var f in files)
        {
            var up = prep.Uploads.FirstOrDefault(u => u.Name == f.Name) ?? throw PipelineException.Config("demo_result_rejected", $"Bot {f.Name} uchun havola bermadi.");
            await PutAsync(up.UploadUrl, f.Path, f.Type, ct).ConfigureAwait(false);
            switch (f.Name)
            {
                case "video.mp4": result.VideoPath = up.Path; result.SizeBytes = new FileInfo(f.Path).Length; result.Sha256 = Hashing.Sha256File(f.Path); break;
                case "cover.jpg": result.CoverPath = up.Path; break;
                default: result.SubtitlesPath = up.Path; break;
            }
            log.Debug($"Demo natijasi: {f.Name} serverga yuklandi ({new FileInfo(f.Path).Length / 1048576.0:0.0} MB).", "Archive");
        }
        return result;
    }

    private async Task PutAsync(string url, string path, string contentType, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var content = new StreamContent(fs, 256 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        req.Headers.Add("x-upsert", "true");
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw PipelineException.Transient("demo_result_network", "Storage'ga yuklashda tarmoq xatosi: " + e.Message); }
        using (resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var msg = $"Storage yuklashni rad etdi (HTTP {(int)resp.StatusCode}): {(body.Length > 200 ? body[..200] : body)}";
            throw (int)resp.StatusCode >= 500 ? PipelineException.Transient("demo_result_storage", msg) : PipelineException.Config("demo_result_storage", msg);
        }
    }
}
