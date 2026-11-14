using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.YouTube;

/// <summary>
/// YouTube Data API v3: resumable upload. Session URI yuklash boshlanishidan OLDIN saqlanadi, video ID olingan zahoti saqlanadi.
/// Uzilishda mavjud sessiya so'raladi va davom ettiriladi; natija noaniq bo'lsa yangi upload ko'r-ko'rona boshlanmaydi.
/// </summary>
public sealed class YouTubeUploader : IVideoUploader
{
    private readonly YouTubeAuth _auth;
    private readonly HttpClient _http;
    private const int ChunkSize = 8 * 1024 * 1024; // 256 KB ga karrali

    public YouTubeUploader(YouTubeAuth auth, HttpClient http) { _auth = auth; _http = http; }

    public bool IsConnected => _auth.HasTokens;

    /// <summary>Oxirgi yuborilgan metadata (diagnostika uchun; sirlar yo'q).</summary>
    public string? LastInsertMetadata { get; private set; }

    /// <summary>YouTube kategoriyalari (faqat videoga biriktirish mumkin bo'lganlari), tanlangan mamlakat va til bo'yicha.</summary>
    public async Task<List<Core.Domain.CategoryItem>> GetCategoriesAsync(string regionCode, string hl, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/youtube/v3/videoCategories?part=snippet&regionCode={Uri.EscapeDataString(regionCode)}&hl={Uri.EscapeDataString(hl)}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ApiError(resp, "Kategoriyalar ro'yxati", ct).ConfigureAwait(false);
        var items = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["items"] as JsonArray ?? new JsonArray();
        return items.Where(i => i?["snippet"]?["assignable"]?.GetValue<bool>() == true)
            .Select(i => new Core.Domain.CategoryItem { Id = i!["id"]!.GetValue<string>(), Title = i["snippet"]!["title"]!.GetValue<string>() })
            .ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool auth = true)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = make();
            if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _auth.GetAccessTokenAsync(ct, forceRefresh: attempt > 0).ConfigureAwait(false));
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw PipelineException.Transient("youtube_network", $"YouTube bilan tarmoq xatosi: {e.Message}", e); }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw PipelineException.Transient("youtube_timeout", "YouTube javob bermadi (timeout).", e); }
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && auth) { resp.Dispose(); continue; }
            return resp;
        }
    }

    private static async Task<PipelineException> ApiError(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string reason = "", message = raw.Length > 300 ? raw[..300] : raw;
        try
        {
            var e = JsonNode.Parse(raw)?["error"];
            message = e?["message"]?.GetValue<string>() ?? message;
            reason = e?["errors"]?[0]?["reason"]?.GetValue<string>() ?? "";
        }
        catch { }
        var code = (int)resp.StatusCode;
        var text = $"{what}: HTTP {code} {reason} {message}".Trim();
        if (reason is "quotaExceeded" or "rateLimitExceeded" or "userRateLimitExceeded" || code == 429 || code >= 500)
            return PipelineException.Transient("youtube_quota_or_server", text);
        if (reason is "uploadLimitExceeded") return PipelineException.Transient("youtube_upload_limit", text);
        if (code is 401 or 403) return PipelineException.Config("youtube_forbidden", text);
        return PipelineException.Config("youtube_bad_request", text);
    }

    public async Task<string> UploadAsync(UploadRequest r, IUploadStateSink state, IJobLog log, CancellationToken ct)
    {
        var total = new FileInfo(r.VideoPath).Length;
        var session = state.LoadSessionUri();
        long offset = 0;
        if (session is not null)
        {
            log.Info("Saqlangan upload sessiyasi topildi - holati so'ralmoqda (qayta yuklash o'rniga davom ettiriladi).", "Upload");
            var q = await QueryAsync(session, total, ct).ConfigureAwait(false);
            if (q.VideoId is not null) { state.SaveVideoId(q.VideoId); log.Info("Oldingi upload aslida tugagan ekan; video ID tiklandi.", "Upload"); return q.VideoId; }
            if (q.Gone)
            {
                var since = (state.SessionStartedAtUtc() ?? DateTimeOffset.UtcNow.AddDays(-1)).AddMinutes(-10);
                var found = await FindRecentUploadAsync(r.Title, since, ct).ConfigureAwait(false);
                if (found is not null)
                {
                    state.SaveVideoId(found); state.SaveSessionUri(null, DateTimeOffset.UtcNow);
                    log.Warn($"Upload sessiyasi muddati tugagan, lekin kanalda shu nomli video topildi ({found}) - u qabul qilindi.", "Upload");
                    return found;
                }
                throw PipelineException.Ambiguous("upload_session_lost",
                    "Oldingi upload sessiyasi yaroqsiz va kanalda mos video topilmadi. Natija noaniq bo'lgani uchun yangi upload avtomatik boshlanmadi. " +
                    "YouTube Studio'ni tekshiring; video yo'q bo'lsa, tafsilotlar oynasida 'Yangi upload' ni bosing.");
            }
            offset = q.Offset;
            log.Info($"Upload {offset / 1048576} MB dan davom ettiriladi.", "Upload");
        }
        else
        {
            session = await InitiateAsync(r, total, ct).ConfigureAwait(false);
            state.SaveSessionUri(session, DateTimeOffset.UtcNow);
            log.Info($"Resumable upload sessiyasi ochildi ({total / 1048576.0:0.0} MB).", "Upload");
        }

        int failures = 0;
        await using var fs = new FileStream(r.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[ChunkSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var len = (int)Math.Min(ChunkSize, total - offset);
            fs.Position = offset;
            int read = 0; while (read < len) { var n = await fs.ReadAsync(buffer.AsMemory(read, len - read), ct).ConfigureAwait(false); if (n == 0) break; read += n; }
            try
            {
                using var resp = await SendAsync(() =>
                {
                    var m = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buffer, 0, read) };
                    m.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + read - 1, total);
                    m.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                    return m;
                }, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code is 200 or 201)
                {
                    var id = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["id"]?.GetValue<string>()
                             ?? throw PipelineException.Ambiguous("upload_no_id", "Upload tugadi, lekin javobda video ID yo'q.");
                    state.SaveVideoId(id);
                    state.Progress(total, total);
                    return id;
                }
                if (code == 308)
                {
                    offset = ParseRange(resp);
                    state.Progress(offset, total);
                    failures = 0;
                    continue;
                }
                if (code is 404 or 410)
                    throw PipelineException.Ambiguous("upload_session_expired", "Upload sessiyasi upload paytida yaroqsiz bo'ldi; natija noaniq - qayta yuklanmadi.");
                if (code >= 500 || code == 429) throw PipelineException.Transient("youtube_chunk", $"Upload bo'lagi xatosi HTTP {code}");
                throw await ApiError(resp, "Upload bo'lagi rad etildi", ct).ConfigureAwait(false);
            }
            catch (PipelineException e) when (e.Category == Core.Domain.ErrorCategory.Transient && failures < 8)
            {
                failures++;
                var wait = TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, failures - 1)));
                log.Warn($"Upload uzildi ({e.Message}); {wait.TotalSeconds:0}s dan keyin sessiya holati so'raladi ({failures}/8).", "Upload");
                await Task.Delay(wait, ct).ConfigureAwait(false);
                var q = await QueryAsync(session, total, ct).ConfigureAwait(false);
                if (q.VideoId is not null) { state.SaveVideoId(q.VideoId); return q.VideoId; }
                if (q.Gone) throw PipelineException.Ambiguous("upload_session_expired", "Upload sessiyasi yo'qoldi; natija noaniq - qayta yuklanmadi.");
                offset = q.Offset;
            }
        }
    }

    private async Task<string> InitiateAsync(UploadRequest r, long total, CancellationToken ct)
    {
        var lang = r.Language.Split('-')[0];
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["title"] = r.Title, ["description"] = r.Description, ["categoryId"] = r.CategoryId,
                ["tags"] = new JsonArray(r.Tags.Select(t => (JsonNode)t).ToArray()),
                ["defaultLanguage"] = lang, ["defaultAudioLanguage"] = lang,
            },
            ["status"] = new JsonObject
            {
                ["privacyStatus"] = "private",
                ["publishAt"] = r.PublishAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["selfDeclaredMadeForKids"] = r.MadeForKids,
                ["containsSyntheticMedia"] = r.ContainsSyntheticMedia,
                ["embeddable"] = r.Embeddable, ["license"] = r.License, ["publicStatsViewable"] = r.PublicStatsViewable,
            },
        };
        var parts = "snippet,status";
        if (r.RecordingDateUtc is not null || !string.IsNullOrWhiteSpace(r.LocationDescription))
        {
            var rec = new JsonObject();
            if (r.RecordingDateUtc is { } rd) rec["recordingDate"] = rd.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(r.LocationDescription)) rec["locationDescription"] = r.LocationDescription;
            body["recordingDetails"] = rec;
            parts += ",recordingDetails";
        }
        var url = $"https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part={parts}&notifySubscribers={(r.NotifySubscribers ? "true" : "false")}";
        LastInsertMetadata = body.ToJsonString();
        using var resp = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            m.Headers.Add("X-Upload-Content-Type", "video/mp4");
            m.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
            return m;
        }, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ApiError(resp, "Upload sessiyasini ochib bo'lmadi", ct).ConfigureAwait(false);
        var loc = resp.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc)) throw PipelineException.Transient("youtube_no_location", "YouTube upload sessiya manzilini qaytarmadi.");
        Redactor.Register(loc);
        return loc;
    }

    private sealed record SessionState(string? VideoId, long Offset, bool Gone);

    private async Task<SessionState> QueryAsync(string session, long total, CancellationToken ct)
    {
        using var resp = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            m.Content.Headers.ContentRange = new ContentRangeHeaderValue(total); // "bytes */total"
            return m;
        }, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        if (code is 200 or 201)
            return new SessionState(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["id"]?.GetValue<string>(), total, false);
        if (code == 308) return new SessionState(null, ParseRange(resp), false);
        if (code is 404 or 410) return new SessionState(null, 0, true);
        if (code >= 500 || code == 429) throw PipelineException.Transient("youtube_query", $"Upload holati so'rovida HTTP {code}");
        throw await ApiError(resp, "Upload holati so'rovi", ct).ConfigureAwait(false);
    }

    private static long ParseRange(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Range", out var vals))
        {
            var v = vals.First(); // "bytes=0-12345"
            var dash = v.LastIndexOf('-');
            if (dash > 0 && long.TryParse(v[(dash + 1)..], out var last)) return last + 1;
        }
        return 0;
    }

    public async Task<YouTubeVideoStatus> GetStatusAsync(string videoId, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/videos?part=status,processingDetails,snippet&id={Uri.EscapeDataString(videoId)}"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw await ApiError(resp, "Video holatini olish", ct).ConfigureAwait(false);
        var item = (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["items"] as JsonArray)?.FirstOrDefault();
        if (item is null) return new YouTubeVideoStatus { VideoId = videoId, Exists = false };
        DateTimeOffset? Dt(JsonNode? n) => n is null ? null : DateTimeOffset.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture);
        return new YouTubeVideoStatus
        {
            VideoId = videoId, Exists = true,
            UploadStatus = item["status"]?["uploadStatus"]?.GetValue<string>() ?? "",
            PrivacyStatus = item["status"]?["privacyStatus"]?.GetValue<string>() ?? "",
            PublishAtUtc = Dt(item["status"]?["publishAt"]),
            FailureReason = item["status"]?["failureReason"]?.GetValue<string>(),
            RejectionReason = item["status"]?["rejectionReason"]?.GetValue<string>(),
            ProcessingStatus = item["processingDetails"]?["processingStatus"]?.GetValue<string>() ?? "",
            PublishedAtUtc = Dt(item["snippet"]?["publishedAt"]),
            Title = item["snippet"]?["title"]?.GetValue<string>() ?? "",
        };
    }

    public async Task<string?> FindRecentUploadAsync(string title, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        using var ch = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=contentDetails&mine=true"), ct).ConfigureAwait(false);
        if (!ch.IsSuccessStatusCode) throw await ApiError(ch, "Kanal yuklamalari ro'yxati", ct).ConfigureAwait(false);
        var uploads = JsonNode.Parse(await ch.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["items"]?[0]?["contentDetails"]?["relatedPlaylists"]?["uploads"]?.GetValue<string>();
        if (uploads is null) return null;
        using var pl = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"https://www.googleapis.com/youtube/v3/playlistItems?part=snippet,contentDetails&maxResults=25&playlistId={Uri.EscapeDataString(uploads)}"), ct).ConfigureAwait(false);
        if (!pl.IsSuccessStatusCode) throw await ApiError(pl, "Yuklamalar playlisti", ct).ConfigureAwait(false);
        var items = JsonNode.Parse(await pl.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["items"] as JsonArray ?? new JsonArray();
        foreach (var it in items)
        {
            var t = it?["snippet"]?["title"]?.GetValue<string>();
            var at = it?["snippet"]?["publishedAt"]?.GetValue<string>();
            if (t == title && at is not null && DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) >= sinceUtc)
                return it?["contentDetails"]?["videoId"]?.GetValue<string>();
        }
        return null;
    }

    public async Task<bool> SetThumbnailAsync(string videoId, string imagePath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        if (bytes.Length > 2 * 1024 * 1024) return false;
        using var resp = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(videoId)}&uploadType=media")
            { Content = new ByteArrayContent(bytes) };
            m.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return m;
        }, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }
}
