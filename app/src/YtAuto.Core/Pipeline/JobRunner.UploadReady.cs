using YtAuto.Core.Domain;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

public sealed partial class JobRunner
{
    /// <summary>Arxivda tayyor MP4 bor va hali YouTube'ga yuklanmagan: uni qayta tayyorlamasdan to'g'ridan-to'g'ri yuklash mumkin.</summary>
    public static bool HasReadyArchive(VideoJob job) =>
        string.IsNullOrEmpty(job.YouTubeVideoId) && !string.IsNullOrEmpty(job.VideoArchivePath) && File.Exists(job.VideoArchivePath)
        && job.Status is not (JobStatus.Published or JobStatus.Cancelled);

    /// <summary>
    /// Bot (yoki boshqa joy) so'ragan "tayyor videoni yuklash" vazifalaridan SHU kompyuterda fayli borlari.
    /// Video fayli boshqa kompyuterda bo'lsa, so'rovni o'sha kompyuter bajaradi.
    /// </summary>
    public static IReadOnlyList<long> LocalUploadRequests(IJobStore store) =>
        store.UploadRequestIds().Where(id => store.Get(id) is { } j && HasReadyArchive(j)).ToList();

    /// <summary>
    /// Worker (--poll har 2 daqiqada, jadval va tanaffuslarda): botdan kelgan "tayyor videoni YouTube'ga yuklash" so'rovlarini bajaradi.
    /// OS mutex ostida chaqiriladi, shuning uchun shu kompyuterdagi o'lgan worker'larning band qilishlari avval bo'shatiladi.
    /// </summary>
    public async Task<List<RunSummary>> RunUploadRequestsAsync(string runId, CancellationToken ct)
    {
        var results = new List<RunSummary>();
        var ids = LocalUploadRequests(_x.Store);
        if (ids.Count == 0) return results;
        var released = _x.Store.ReleaseStaleClaims(runId);
        if (released > 0) _x.RunLog.Warn($"{released} ta eskirgan band qilish bo'shatildi (oldingi worker kutilmaganda to'xtagan).");
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            _x.RunLog.Info($"#{id}: botdan \"tayyor videoni YouTube'ga yuklash\" so'rovi — video qayta tayyorlanmaydi, arxivdagi fayl yuklanadi.");
            try
            {
                var r = await UploadReadyAsync(id, runId, publishNow: false, ct).ConfigureAwait(false);
                results.Add(r);
                _x.RunLog.Info($"#{id}: yuklash so'rovi natijasi {r.Outcome}{(r.Message is null ? "" : " — " + r.Message)}.");
            }
            catch (InvalidOperationException e) { _x.RunLog.Warn($"#{id}: yuklash so'rovi hozir bajarilmadi: {e.Message}"); }
        }
        return results;
    }

    /// <summary>
    /// "Tayyor videoni YouTube'ga yuklash": arxivdagi tayyor MP4 to'g'ridan-to'g'ri YouTube API orqali yuklanadi -
    /// Gemini, ovoz, Claude, OpenFilm qayta ishga tushirilmaydi. Title/tavsif/teglar vazifada saqlangan qiymatlardan.
    /// Chiqish vaqti o'tib ketgan bo'lsa - hozir + 1 soat; <paramref name="publishNow"/> - darhol public.
    /// Xato bo'lsa vazifa worker navbatiga QAYTMAYDI (aks holda u videoni qayta yasashi mumkin) - "E'tibor kerak" bo'ladi.
    /// </summary>
    public async Task<RunSummary> UploadReadyAsync(long jobId, string runId, bool publishNow, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summary = new RunSummary { JobId = jobId, PublishNow = publishNow };
        var job = _x.Store.Get(jobId) ?? throw new InvalidOperationException("Vazifa topilmadi.");
        if (!string.IsNullOrEmpty(job.YouTubeVideoId))
        {
            _x.Store.SetUploadRequested(jobId, false);
            summary.Outcome = RunOutcome.Completed; summary.Message = "Video allaqachon YouTube'ga yuklangan."; return summary;
        }
        if (!HasReadyArchive(job))
            throw new InvalidOperationException("Arxivda tayyor video topilmadi — avval video tayyorlanishi kerak.");
        if (!_x.Store.TryClaim(jobId, runId, _x.Clock.UtcNow))
            throw new InvalidOperationException("Worker hozir shu vazifa ustida ishlamoqda — tugashini kuting.");
        _x.Store.SetUploadRequested(jobId, false); // so'rov qabul qilindi: xato bo'lsa ham har --poll da qayta urinilmaydi
        var log = _x.JobLogFactory(job.Id);
        try
        {
            var ctx = PrepareContext(job, runId, log, _x.Settings.Load());
            ctx.Ct = ct;
            ctx.PublishNow = publishNow;
            await UploadArchivedAsync(ctx).ConfigureAwait(false);
            summary.Outcome = RunOutcome.Completed;
            summary.Message = job.YouTubeUrl;
        }
        catch (Exception ex)
        {
            var pe = ex as PipelineException;
            job.LastErrorCode = pe?.Code ?? "upload_error";
            job.LastErrorMessage = Shorten(ex.Message, 600);
            job.Status = JobStatus.NeedsAttention;
            job.AttentionReason = "YouTube'ga yuklash muvaffaqiyatsiz: " + job.LastErrorMessage + " — 'Tayyor videoni YouTube'ga yuklash'ni qayta bosing.";
            _x.Store.Update(job);
            log.Error($"Tayyor videoni yuklash xatosi [{job.LastErrorCode}]: {ex.Message}", "Upload");
            summary.Outcome = RunOutcome.NeedsAttention; summary.ErrorCode = job.LastErrorCode; summary.Message = ex.Message;
        }
        finally
        {
            try { PurgeFinishedJob(job); } catch (Exception e) { _x.RunLog.Warn($"#{job.Id}: tozalashda xato: {e.Message}"); }
            _x.Store.ReleaseClaim(jobId, runId);
            summary.Elapsed = sw.Elapsed;
        }
        return summary;
    }

    /// <summary>Faqat yuklash bosqichi: saqlangan ssenariydan teglar olinadi, holat Archived qilinadi va YouTube'ga yuklanadi.</summary>
    private async Task UploadArchivedAsync(JobContext ctx)
    {
        var job = ctx.Job;
        var script = new VideoScript();
        var recordScript = Path.Combine(ctx.RecordDir, "script.json");
        var path = File.Exists(ctx.ScriptJson) ? ctx.ScriptJson : File.Exists(recordScript) ? recordScript : null;
        if (path is not null) { try { script = VideoScript.FromJson(File.ReadAllText(path)); } catch { } }

        job.Status = JobStatus.Archived; job.LastErrorCode = null; job.LastErrorMessage = null; job.AttentionReason = null;
        _x.Store.Update(job);
        ctx.Log.Info($"Tayyor video to'g'ridan-to'g'ri YouTube'ga yuklanmoqda (qayta tayyorlanmaydi): {job.VideoArchivePath}", "Upload");
        await UploadStageAsync(ctx, script).ConfigureAwait(false);
    }
}
