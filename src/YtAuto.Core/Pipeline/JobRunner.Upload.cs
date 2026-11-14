using System.Text.Json;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

public sealed partial class JobRunner
{
    /// <summary>Muddat o'tib ketgan video shuncha daqiqadan keyin chiqariladi.</summary>
    public const int LatePublishDelayMinutes = 35;

    // ---------------------------------------------------------------- YouTube upload
    private async Task UploadStageAsync(JobContext c, VideoScript script)
    {
        var rec = _x.Store.GetStage(c.Job.Id, StageKind.Upload);
        if (!string.IsNullOrEmpty(c.Job.YouTubeVideoId))
        {
            c.Log.Info($"Video allaqachon yuklangan ({c.Job.YouTubeVideoId}); qayta yuklanmaydi.", "Upload");
            if (rec.State != StageState.Done) { rec.State = StageState.Done; rec.FinishedAtUtc ??= _x.Clock.UtcNow; _x.Store.SaveStage(rec); }
            if (c.Job.Status is JobStatus.Uploading or JobStatus.Archived) { c.Job.Status = JobStatus.Processing; _x.Store.Update(c.Job); }
            await PollAfterUploadAsync(c).ConfigureAwait(false);
            return;
        }
        if (!_x.Uploader.IsConnected)
            throw new AttentionRequiredException("youtube_not_connected", "YouTube kanali ulanmagan. Video arxivda saqlandi; Sozlamalarda kanalni ulang va vazifani qayta navbatga qo'ying.");

        var lead = TimeSpan.FromMinutes(Math.Max(15, c.S.YouTube.MinLeadMinutes));
        if (c.Job.PublishAtUtc <= _x.Clock.UtcNow + lead)
        {
            // Video tayyor bo'lguncha chiqish vaqti o'tib ketgan: hozirgi vaqtdan 35 daqiqa keyinga suriladi.
            var old = c.Job.PublishAtUtc;
            c.Job.PublishAtUtc = _x.Clock.UtcNow.AddMinutes(LatePublishDelayMinutes);
            _x.Store.Update(c.Job);
            c.Log.Warn($"Chiqish vaqti ({old.ToLocalTime():yyyy-MM-dd HH:mm}) o'tib ketgan edi — yangi vaqt: {c.Job.PublishAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} (hozir + {LatePublishDelayMinutes} daqiqa).", "Upload");
        }
        if (string.IsNullOrEmpty(c.Job.VideoArchivePath) || !File.Exists(c.Job.VideoArchivePath))
            throw PipelineException.Config("archive_missing", "Arxivdagi video topilmadi; upload qilinmaydi.");

        JobStateMachine.EnsureTransition(c.Job.Status, JobStatus.Uploading);
        c.Job.Status = JobStatus.Uploading; _x.Store.Update(c.Job);
        rec.State = StageState.Running; rec.Attempts++; rec.StartedAtUtc = _x.Clock.UtcNow; rec.ErrorCode = null; rec.ErrorMessage = null;
        _x.Store.SaveStage(rec);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tags = script.Tags.Concat(c.S.YouTube.DefaultTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        var req = new UploadRequest
        {
            VideoPath = c.Job.VideoArchivePath!, Title = Trunc(c.Job.DisplayTitle, 100), Description = c.Job.Description ?? "",
            Tags = tags, CategoryId = c.S.YouTube.CategoryId, Language = c.Job.Language, PublishAtUtc = c.Job.PublishAtUtc,
            MadeForKids = c.S.YouTube.MadeForKids, ContainsSyntheticMedia = c.S.YouTube.ContainsSyntheticMedia, ThumbnailPath = c.Job.ThumbnailPath,
            License = c.S.YouTube.License == "creativeCommon" ? "creativeCommon" : "youtube", Embeddable = c.S.YouTube.Embeddable,
            PublicStatsViewable = c.S.YouTube.PublicStatsViewable, NotifySubscribers = c.S.YouTube.NotifySubscribers,
            RecordingDateUtc = c.S.YouTube.SetRecordingDate ? _x.Clock.UtcNow : null, LocationDescription = c.S.YouTube.LocationDescription?.Trim() ?? "",
        };
        try
        {
            var sink = new UploadSink(this, c);
            var videoId = await _x.Uploader.UploadAsync(req, sink, c.Log, c.Ct).ConfigureAwait(false);
            c.Job.YouTubeVideoId = videoId;
            c.Job.Status = JobStatus.Processing;
            _x.Store.Update(c.Job);
            _x.Secrets.Set(SecretNames.UploadSessionPrefix + c.Job.Id, null);
            rec.State = StageState.Done; rec.FinishedAtUtc = _x.Clock.UtcNow; rec.DurationMs = sw.ElapsedMilliseconds;
            var outputs = Deserialize(rec.OutputsJson); outputs.Values["video_id"] = videoId;
            rec.OutputsJson = JsonSerializer.Serialize(outputs);
            _x.Store.SaveStage(rec);
            c.Log.Info($"YouTube'ga private yuklandi, publish vaqti {c.Job.PublishAtUtc:u}: {c.Job.YouTubeUrl} ({sw.Elapsed.TotalSeconds:0}s).", "Upload");
            if (!string.IsNullOrEmpty(req.ThumbnailPath) && File.Exists(req.ThumbnailPath))
            {
                try
                {
                    var ok = await _x.Uploader.SetThumbnailAsync(videoId, req.ThumbnailPath!, c.Ct).ConfigureAwait(false);
                    c.Log.Info(ok ? "Thumbnail o'rnatildi." : "Thumbnail o'rnatilmadi (kanal tasdiqlanmagan bo'lishi mumkin).", "Upload");
                }
                catch (Exception e) { c.Log.Warn($"Thumbnail o'rnatilmadi: {e.Message}", "Upload"); }
            }
        }
        catch (Exception ex)
        {
            rec.State = StageState.Failed; rec.FinishedAtUtc = _x.Clock.UtcNow; rec.DurationMs = sw.ElapsedMilliseconds;
            rec.ErrorCode = (ex as PipelineException)?.Code ?? "upload_error"; rec.ErrorMessage = Shorten(ex.Message, 1000);
            _x.Store.SaveStage(rec);
            throw;
        }
        await PollAfterUploadAsync(c).ConfigureAwait(false);
    }

    private async Task PollAfterUploadAsync(JobContext c)
    {
        var until = _x.Clock.UtcNow + _x.PostUploadPoll;
        while (true)
        {
            try
            {
                var st = await RefreshYouTubeStatusAsync(c.Job, c.Log, c.Ct).ConfigureAwait(false);
                if (st != JobStatus.Processing) return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { c.Log.Warn($"YouTube holatini tekshirib bo'lmadi: {e.Message}", "Upload"); }
            if (_x.Clock.UtcNow >= until) { c.Log.Info("YouTube hali qayta ishlamoqda: holat Processing qoldi (bu yangi upload uchun sabab emas).", "Upload"); return; }
            await Task.Delay(TimeSpan.FromSeconds(30), c.Ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// YouTube holatini yangilaydi: Processing → Scheduled → Published. Sana o'tganining o'zi Published degani emas -
    /// faqat YouTube privacyStatus=public bo'lsa Published. Tasdiqlangan natijadan keyin jobs papkasi tozalanadi.
    /// </summary>
    public async Task<JobStatus> RefreshYouTubeStatusAsync(VideoJob job, IJobLog log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.YouTubeVideoId) || !_x.Uploader.IsConnected) return job.Status;
        var st = await _x.Uploader.GetStatusAsync(job.YouTubeVideoId!, ct).ConfigureAwait(false);
        // Chaqiruvchining obyekti joyida yangilanadi: keyingi Update() eskirgan qiymatlarni qayta yozmasligi uchun.
        var fresh = job;
        fresh.YouTubeCheckedAtUtc = _x.Clock.UtcNow;
        var before = fresh.Status;
        if (!st.Exists)
        {
            fresh.Status = JobStatus.NeedsAttention;
            fresh.AttentionReason = "YouTube'da video topilmadi (o'chirilgan bo'lishi mumkin). Avtomatik qayta yuklanmaydi.";
        }
        else
        {
            fresh.YouTubePrivacy = st.PrivacyStatus; fresh.YouTubeUploadStatus = st.UploadStatus; fresh.YouTubeProcessingStatus = st.ProcessingStatus;
            if (st.UploadStatus is "failed" or "rejected" or "deleted" || st.ProcessingStatus is "failed" or "terminated")
            {
                fresh.Status = JobStatus.NeedsAttention;
                fresh.AttentionReason = $"YouTube video holati: upload={st.UploadStatus}, processing={st.ProcessingStatus} {st.FailureReason}{st.RejectionReason}".Trim();
            }
            else if (st.PrivacyStatus == "public")
            {
                fresh.Status = JobStatus.Published; fresh.PublishedAtUtc ??= st.PublishedAtUtc ?? _x.Clock.UtcNow;
            }
            else if (st.UploadStatus == "processed" && st.PublishAtUtc is not null)
                fresh.Status = JobStatus.Scheduled;
            else if (st.UploadStatus == "processed" && st.PublishAtUtc is null && st.PrivacyStatus == "private")
            {
                fresh.Status = JobStatus.NeedsAttention;
                fresh.AttentionReason = "Video private, lekin YouTube'da publish vaqti o'rnatilmagan. Yangi vaqt tanlang.";
            }
            else fresh.Status = JobStatus.Processing;
        }
        _x.Store.Update(fresh);
        if (before != fresh.Status) log.Info($"YouTube holati: {before} → {fresh.Status} (privacy={st.PrivacyStatus}, upload={st.UploadStatus}, processing={st.ProcessingStatus}).", "YouTube");
        if (fresh.Status is JobStatus.Scheduled or JobStatus.Published) CleanupWorkDir(fresh, log);
        return fresh.Status;
    }

    /// <summary>Faqat ilovaga tegishli job papkasi chegarasida tozalaydi. Arxiv, records va shablonlar tegilmaydi.</summary>
    public void CleanupWorkDir(VideoJob job, IJobLog log)
    {
        if (job.WorkDirCleaned || string.IsNullOrEmpty(job.WorkDir)) return;
        if (string.IsNullOrEmpty(job.VideoArchivePath) || !File.Exists(job.VideoArchivePath)) { log.Warn("Arxiv fayli topilmadi - jobs papkasi tozalanmadi."); return; }
        try
        {
            var snap = string.IsNullOrEmpty(job.SettingsSnapshotJson) ? null : SettingsSnapshot.FromJson(job.SettingsSnapshotJson);
            var root = snap?.Settings.Paths.WorkDir;
            if (string.IsNullOrEmpty(root)) return;
            var dirName = Path.GetFileName(PathGuard.Full(job.WorkDir));
            if (dirName != $"job-{job.Id}") { log.Warn($"Kutilmagan job papka nomi, tozalanmadi: {job.WorkDir}"); return; }
            PathGuard.SafeDeleteDirectory(root, job.WorkDir, m => log.Debug(m));
            job.WorkDirCleaned = true;
            _x.Store.Update(job);
            log.Info($"Vaqtinchalik jobs papkasi tozalandi: {job.WorkDir} (arxiv va loglar saqlanadi).");
        }
        catch (Exception e) { log.Warn($"Tozalashda xato: {e.Message}"); }
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed class UploadSink : IUploadStateSink
    {
        private readonly JobRunner _r; private readonly JobContext _c;
        private int _lastPct = -1;
        public UploadSink(JobRunner r, JobContext c) { _r = r; _c = c; }
        private string Key => SecretNames.UploadSessionPrefix + _c.Job.Id;

        public string? LoadSessionUri() => _r._x.Secrets.Get(Key);

        public void SaveSessionUri(string? uri, DateTimeOffset startedAtUtc)
        {
            // Resumable session URI maxfiy: DPAPI store'da, loglarga yozilmaydi.
            _r._x.Secrets.Set(Key, uri);
            var rec = _r._x.Store.GetStage(_c.Job.Id, StageKind.Upload);
            var o = Deserialize(rec.OutputsJson);
            if (uri is null) o.Values.Remove("session_started_utc"); else o.Values["session_started_utc"] = startedAtUtc.ToString("O");
            rec.OutputsJson = JsonSerializer.Serialize(o);
            _r._x.Store.SaveStage(rec);
        }

        public DateTimeOffset? SessionStartedAtUtc()
        {
            var v = Deserialize(_r._x.Store.GetStage(_c.Job.Id, StageKind.Upload).OutputsJson).Get("session_started_utc");
            return v is null ? null : DateTimeOffset.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public void SaveVideoId(string videoId)
        {
            _c.Job.YouTubeVideoId = videoId;
            _r._x.Store.Update(_c.Job);
        }

        public void Progress(long sent, long total)
        {
            var pct = total == 0 ? 100 : (int)(sent * 100 / total);
            if (pct / 10 != _lastPct / 10) { _lastPct = pct; _c.Log.Info($"Upload: {pct}% ({sent / 1048576} / {total / 1048576} MB)", "Upload"); }
        }
    }
}
