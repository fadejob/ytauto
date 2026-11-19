using System.Diagnostics;
using System.Text.Json;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;

namespace YtAuto.Core.Pipeline;

public sealed class PipelineServices
{
    public required IJobStore Store { get; init; }
    public required ISettingsStore Settings { get; init; }
    public required IScriptGenerator ScriptGenerator { get; init; }
    public required ITtsService Tts { get; init; }
    /// <summary>Botdan yuklangan tayyor ovoz uchun (berilmasa bunday vazifalar "E'tibor kerak" bo'ladi).</summary>
    public IAudioImporter? AudioImporter { get; init; }
    public required IAligner Aligner { get; init; }
    public required IEditorAgent Editor { get; init; }
    public required IFilmTool Film { get; init; }
    public required IMediaProbe Media { get; init; }
    /// <summary>Gemini vision: shablon uslubi, rasm rejasi, shablonga moslik (berilmasa - o'tkazib yuboriladi).</summary>
    public Visuals.IVisualDirector? Visuals { get; init; }
    /// <summary>Ochiq manbalardan real rasm/ikonlar (berilmasa - faqat chizilgan grafika).</summary>
    public Visuals.IMediaLibrary? MediaLibrary { get; init; }
    /// <summary>Stok rasm va video parchalari nomzodlari (OpenFilm qidiradi; berilmasa yoki kalit yo'q bo'lsa - faqat MediaLibrary).</summary>
    public Visuals.IStockMediaSource? Stock { get; init; }
    /// <summary>Fon musiqasi fayllarini (bot serveridan) yuklab, lokal keshda saqlaydi (berilmasa - musiqasiz).</summary>
    public IMusicCache? Music { get; init; }
    public required IVideoUploader Uploader { get; init; }
    public required IResourceMonitor Resources { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required IAppPaths Paths { get; init; }
    public required IClock Clock { get; init; }
    public required Func<long, IJobLog> JobLogFactory { get; init; }
    public required IJobLog RunLog { get; init; }
    public string AppVersion { get; init; } = "1.0.0";
    /// <summary>Video YouTube'da public bo'lganda (bir marta): bot kanal kontekstini yangilashi uchun belgi qo'yiladi.</summary>
    public Action<VideoJob>? OnPublished { get; init; }
    /// <summary>«Mening ovozim» namunasi holati (version, "ready"|"error", xabar): bot foydalanuvchiga ko'rsatadi.</summary>
    public Action<string, string, string>? OnVoiceCloneStatus { get; init; }
    /// <summary>Upload'dan keyin YouTube qayta ishlashini kutish (0 - kutmaslik).</summary>
    public TimeSpan PostUploadPoll { get; init; } = TimeSpan.FromMinutes(8);
    /// <summary>Faqat lokal bosqichlarni bajarish (self-test): Archive'dan keyin to'xtaydi.</summary>
    public bool StopBeforeUpload { get; init; }
}

public enum RunOutcome { QueueEmpty, Completed, Failed, NeedsAttention, Cancelled, StoppedBeforeUpload }

public sealed class RunSummary
{
    public RunOutcome Outcome { get; set; }
    public long? JobId { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public TimeSpan Elapsed { get; set; }
    /// <summary>Vazifa "Hozir chiqarish" so'rovi bo'yicha bajarildi.</summary>
    public bool PublishNow { get; set; }
}

/// <summary>
/// Worker'ning yagona ishi: navbatdan bitta vazifani olib, tugallanmagan bosqichidan davom ettirish.
/// Og'ir bosqichlar ketma-ket; har bosqichdan keyin checkpoint; xato toifasiga qarab retry yoki NeedsAttention.
/// </summary>
public sealed partial class JobRunner
{
    private readonly PipelineServices _x;
    public JobRunner(PipelineServices services) => _x = services;

    public Task<RunSummary> RunOnceAsync(string runId, CancellationToken ct) => RunOnceAsync(runId, publishNowOnly: false, ct);

    /// <summary>
    /// Bitta vazifa. "Hozir chiqarish" so'rovlari har doim birinchi olinadi; <paramref name="publishNowOnly"/> bo'lsa oddiy navbatga tegilmaydi
    /// (masofaviy buyruqni kutuvchi --poll rejimi).
    /// </summary>
    public async Task<RunSummary> RunOnceAsync(string runId, bool publishNowOnly, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var summary = new RunSummary();
        var released = _x.Store.ReleaseStaleClaims(runId);
        if (released > 0) _x.RunLog.Warn($"{released} ta eskirgan band qilish bo'shatildi (oldingi worker kutilmaganda to'xtagan).");

        var job = _x.Store.ClaimPublishNow(runId, _x.Clock.UtcNow);
        if (job is null && !publishNowOnly) job = _x.Store.ClaimNext(runId, _x.Clock.UtcNow);
        if (job is null)
        {
            if (publishNowOnly) _x.RunLog.Debug("\"Hozir chiqarish\" so'rovi yo'q.");
            else _x.RunLog.Info("Navbatda bajariladigan vazifa yo'q. Og'ir dasturlar ochilmaydi.");
            summary.Outcome = RunOutcome.QueueEmpty;
            return summary;
        }
        var publishNow = job.PublishNow;
        summary.JobId = job.Id; summary.PublishNow = publishNow;
        var log = _x.JobLogFactory(job.Id);
        _x.RunLog.Info($"Vazifa #{job.Id} band qilindi{(publishNow ? " (HOZIR CHIQARISH)" : "")}: \"{job.Topic}\" (holat {job.Status}, urinishlar {job.RunAttempts}).");

        using var cancelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorCancelAsync(job.Id, runId, cancelCts);
        try
        {
            if (publishNow)
            {
                log.Info($"\"Hozir chiqarish\" so'rovi: video navbatdan tashqari tayyorlanadi va YouTube'ga darhol public chiqariladi (holat {job.Status}).");
                if (job.Status == JobStatus.NeedsAttention && string.IsNullOrEmpty(job.YouTubeVideoId))
                {
                    // Foydalanuvchining aniq buyrug'i: urinishlar hisoblagichi yangilanadi, tayyor bosqichlar saqlanadi.
                    job.Status = JobStatus.Queued; job.RunAttempts = 0; job.QaFixCycles = 0; job.AttentionReason = null; job.NextAttemptAtUtc = null;
                    _x.Store.Update(job);
                }
                if (!string.IsNullOrEmpty(job.YouTubeVideoId) && !JobStateMachine.IsInProgress(job.Status))
                {
                    try { await MakeUploadedPublicAsync(job, log, cancelCts.Token).ConfigureAwait(false); }
                    catch (PipelineException pe) when (pe.Category == ErrorCategory.Transient)
                    {
                        // Video joyida (yuklangan) qoladi: holat o'zgartirilmaydi, so'rov biroz keyin qayta bajariladi.
                        job.NextAttemptAtUtc = _x.Clock.UtcNow.AddMinutes(10); job.LastErrorCode = pe.Code; job.LastErrorMessage = Shorten(pe.Message, 600);
                        _x.Store.Update(job);
                        log.Warn($"Videoni public qilib bo'lmadi ({pe.Message}); 10 daqiqadan keyin qayta uriniladi.", "Upload");
                        summary.Outcome = RunOutcome.Failed; summary.Message = pe.Message;
                        return summary;
                    }
                    summary.Outcome = job.Status == JobStatus.Published ? RunOutcome.Completed : RunOutcome.NeedsAttention;
                    summary.Message = job.Status == JobStatus.Published ? null : job.AttentionReason;
                    return summary;
                }
            }
            if (publishNow && HasReadyArchive(job))
            {
                // Video allaqachon tayyor: hech narsa qayta tayyorlanmaydi (montaj bosqichi uzilgan bo'lsa ham) - arxivdagi fayl darhol public yuklanadi.
                // Xato bo'lsa vazifa navbatga qaytmaydi (aks holda video qayta yasalishi mumkin) - "E'tibor kerak".
                try
                {
                    var readyCtx = PrepareContext(job, runId, log, _x.Settings.Load());
                    readyCtx.Ct = cancelCts.Token; readyCtx.PublishNow = true;
                    await UploadArchivedAsync(readyCtx).ConfigureAwait(false);
                    job.NextAttemptAtUtc = null;
                    _x.Store.Update(job);
                    summary.Outcome = RunOutcome.Completed; summary.Message = job.YouTubeUrl;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    job.LastErrorCode = (ex as PipelineException)?.Code ?? "upload_error";
                    job.LastErrorMessage = Shorten(ex.Message, 600);
                    job.Status = JobStatus.NeedsAttention;
                    job.AttentionReason = "YouTube'ga yuklash muvaffaqiyatsiz: " + job.LastErrorMessage + " — 'Tayyor videoni YouTube'ga yuklash'ni qayta bosing.";
                    _x.Store.Update(job);
                    log.Error($"Tayyor videoni hozir chiqarish xatosi [{job.LastErrorCode}]: {ex.Message}", "Upload");
                    summary.Outcome = RunOutcome.NeedsAttention; summary.ErrorCode = job.LastErrorCode; summary.Message = ex.Message;
                }
                return summary;
            }
            if (JobStateMachine.IsInProgress(job.Status))
            {
                job.RunAttempts++;
                log.Warn($"Oldingi ishga tushish {job.Status} bosqichida uzilib qolgan. Saqlangan fayllar tekshirilib, davom ettiriladi (urinish {job.RunAttempts}).");
                _x.Store.Update(job);
            }
            var settingsNow = _x.Settings.Load();
            if (job.RunAttempts >= Math.Max(1, settingsNow.Limits.MaxRunAttempts))
                throw new AttentionRequiredException("retry_limit", $"Qayta urinishlar limiti tugadi ({job.RunAttempts}). Oxirgi xato: {job.LastErrorMessage}");

            var ctx = PrepareContext(job, runId, log, settingsNow);
            ctx.Ct = cancelCts.Token;
            ctx.PublishNow = publishNow;
            await ExecutePipelineAsync(ctx).ConfigureAwait(false);
            if (publishNow && !string.IsNullOrEmpty(job.YouTubeVideoId) && job.Status != JobStatus.Published)
                await MakeUploadedPublicAsync(job, log, cancelCts.Token).ConfigureAwait(false);

            summary.Outcome = _x.StopBeforeUpload && job.Status == JobStatus.Archived ? RunOutcome.StoppedBeforeUpload : RunOutcome.Completed;
            job.LastErrorCode = null; job.LastErrorMessage = null; job.NextAttemptAtUtc = null;
            _x.Store.Update(job);
            log.Info($"Ishga tushish yakunlandi: holat {job.Status}.");
        }
        catch (OperationCanceledException) when (_x.Store.IsCancelRequested(job.Id))
        {
            job.Status = JobStatus.Cancelled; job.AttentionReason = null;
            _x.Store.Update(job);
            log.Warn("Vazifa foydalanuvchi tomonidan bekor qilindi. Worker ochgan jarayonlar yopildi.");
            summary.Outcome = RunOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            summary.Outcome = HandleFailure(job, ex, log, out var code);
            summary.ErrorCode = code; summary.Message = ex.Message;
        }
        finally
        {
            cancelCts.Cancel();
            try { await monitor.ConfigureAwait(false); } catch { /* ignore */ }
            // "Hozir chiqarish" bayrog'i faqat vaqtinchalik xatoda qoladi (backoff bilan qayta uriniladi); qolgan natijalarda olib tashlanadi.
            if (publishNow && summary.Outcome != RunOutcome.Failed)
            {
                try { _x.Store.SetPublishNow(job.Id, false); job.PublishNow = false; }
                catch (Exception e) { _x.RunLog.Warn($"#{job.Id}: 'hozir chiqarish' bayrog'ini olib bo'lmadi: {e.Message}"); }
            }
            // Ish tugagan bo'lsa (YouTube'da Scheduled/Published): papkalar va qo'shimchalar o'chiriladi, faqat video + audio qoladi.
            try { PurgeFinishedJob(job); }
            catch (Exception e) { _x.RunLog.Warn($"#{job.Id}: tozalashda xato: {e.Message}"); }
            _x.Store.ReleaseClaim(job.Id, runId);
            summary.Elapsed = sw.Elapsed;
        }
        return summary;
    }

    private async Task MonitorCancelAsync(long jobId, string runId, CancellationTokenSource cts)
    {
        var lastBeat = DateTime.UtcNow;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                try
                {
                    if (_x.Store.IsCancelRequested(jobId)) { cts.Cancel(); return; }
                    if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(60)) { _x.Store.Heartbeat(jobId, runId); lastBeat = DateTime.UtcNow; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { _x.RunLog.Debug("Bekor qilish/heartbeat tekshiruvi o'tmadi (tarmoq?): " + e.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private RunOutcome HandleFailure(VideoJob job, Exception ex, IJobLog log, out string code)
    {
        var settings = _x.Settings.Load();
        var pe = ex as PipelineException;
        code = pe?.Code ?? "internal_error";
        var category = pe?.Category ?? (ex is OperationCanceledException ? ErrorCategory.Transient : ErrorCategory.Internal);
        job.LastErrorCode = code;
        job.LastErrorMessage = Shorten(ex.Message, 600);
        log.Error($"Xato [{category}/{code}]: {ex.Message}");
        if (pe is null) log.Debug(ex.ToString());

        RunOutcome outcome;
        if (ex is AttentionRequiredException || category is ErrorCategory.Configuration or ErrorCategory.Quality or ErrorCategory.Ambiguous)
        {
            job.Status = JobStatus.NeedsAttention;
            job.AttentionReason = job.LastErrorMessage;
            outcome = RunOutcome.NeedsAttention;
        }
        else
        {
            job.RunAttempts++;
            if (job.RunAttempts >= Math.Max(1, settings.Limits.MaxRunAttempts))
            {
                job.Status = JobStatus.NeedsAttention;
                job.AttentionReason = $"Qayta urinishlar limiti tugadi ({job.RunAttempts}). Oxirgi xato: {job.LastErrorMessage}";
                outcome = RunOutcome.NeedsAttention;
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.NextAttemptAtUtc = _x.Clock.UtcNow.AddMinutes(10 * job.RunAttempts);
                outcome = RunOutcome.Failed;
                log.Warn($"Keyingi ishga tushishda qayta uriniladi ({job.RunAttempts}/{settings.Limits.MaxRunAttempts}).");
            }
        }
        _x.Store.Update(job);
        return outcome;
    }

    private JobContext PrepareContext(VideoJob job, string runId, IJobLog log, AppSettings current)
    {
        SettingsSnapshot snap;
        if (string.IsNullOrEmpty(job.SettingsSnapshotJson))
        {
            snap = new SettingsSnapshot
            {
                TakenAtUtc = _x.Clock.UtcNow,
                Settings = current.Clone(),
                AppVersion = _x.AppVersion,
            };
            var tpl = snap.Settings.FindTemplate(job.TemplateName);
            snap.TemplateVersion = TemplateVersion(tpl);
            snap.IntegrationVersions["claude"] = SafeVersion(() => _x.Editor.Version);
            snap.IntegrationVersions["openfilm"] = SafeVersion(() => _x.Film.Version);
            snap.IntegrationVersions["ffmpeg"] = SafeVersion(() => _x.Media.Version);
            snap.IntegrationVersions["aligner"] = SafeVersion(() => _x.Aligner.Describe());
            job.SettingsSnapshotJson = snap.ToJson();
            _x.Store.Update(job);
            log.Info($"Sozlamalar, promptlar va shablon versiyasi snapshot qilindi (shablon: {(tpl?.Name ?? "yo'q")}, versiya {snap.TemplateVersion}).");
        }
        else snap = SettingsSnapshot.FromJson(job.SettingsSnapshotJson);

        var s = snap.Settings;
        if (string.IsNullOrWhiteSpace(s.Paths.WorkDir)) throw PipelineException.Config("workdir_missing", "Vaqtinchalik ish papkasi sozlanmagan.");
        var workRoot = PathGuard.Full(s.Paths.WorkDir);
        var dir = Path.Combine(workRoot, $"job-{job.Id}");
        job.WorkDir = dir;
        var ctx = new JobContext
        {
            Job = job, Snapshot = snap, WorkRoot = workRoot, Dir = dir,
            RecordDir = _x.Paths.RecordDirFor(job.Id), Log = log, RunId = runId,
        };
        Directory.CreateDirectory(workRoot);
        if (PathGuard.IsReparsePoint(workRoot)) throw PipelineException.Config("workdir_link", $"Ish papkasi symlink/junction bo'lmasligi kerak: {workRoot}");
        ctx.EnsureDirs();
        FileOps.WriteAllTextAtomic(Path.Combine(ctx.RecordDir, "settings_snapshot.json"), job.SettingsSnapshotJson!);

        var free = _x.Resources.FreeDiskGb(workRoot);
        if (free < s.Limits.MinFreeDiskGb)
            throw PipelineException.Transient("disk_low", $"Diskda joy kam: {free:0.0} GB bo'sh, kamida {s.Limits.MinFreeDiskGb} GB kerak ({workRoot}).");
        CheckWritable(workRoot);
        _x.Store.Update(job);
        log.Info($"Ish papkasi: {dir}. Bo'sh disk {free:0.0} GB, bo'sh RAM {_x.Resources.AvailableRamGb():0.0}/{_x.Resources.TotalRamGb():0.0} GB.");
        return ctx;
    }

    private static string SafeVersion(Func<string> f) { try { return f(); } catch (Exception e) { return "unknown: " + e.Message; } }

    public static string TemplateVersion(TemplateDefinition? t)
    {
        if (t is null || t.Kind == TemplateKind.None) return "none";
        try
        {
            if (t.Kind == TemplateKind.OpenFilmProject && Directory.Exists(t.Path))
                return "dir:" + Hashing.DirectoryManifest(t.Path, f => !f.Contains(Path.DirectorySeparatorChar + ".film" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar));
            if (File.Exists(t.Path)) { var fi = new FileInfo(t.Path); return $"file:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"; }
        }
        catch { }
        return "missing";
    }

    private static void CheckWritable(string dir)
    {
        var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch (Exception e) { throw PipelineException.Config("not_writable", $"Papkaga yozib bo'lmadi: {dir} ({e.Message})"); }
    }

    private void Preflight(JobContext ctx)
    {
        // Tashqi dasturlar yo'llari JORIY sozlamadan (foydalanuvchi xato yo'lni tuzatsa, retry darhol ishlaydi);
        // prompt, shablon, ovoz, arxiv va limitlar - snapshot'dan.
        var live = _x.Settings.Load();
        var s = ctx.S; var problems = new List<string>();
        s = new AppSettings
        {
            VoiceStudio = live.VoiceStudio, Claude = live.Claude, OpenFilm = live.OpenFilm,
            Paths = s.Paths, Templates = s.Templates, VoiceProfiles = s.VoiceProfiles,
        };
        bool Pending(StageKind k) => _x.Store.GetStage(ctx.Job.Id, k).State != StageState.Done;
        if (Pending(StageKind.Voice) && !ctx.Job.HasManualAudio && !File.Exists(s.VoiceStudio.ExePath) && string.IsNullOrWhiteSpace(s.VoiceStudio.BaseUrl))
            problems.Add($"VoiceStudio topilmadi: {s.VoiceStudio.ExePath}");
        if (Pending(StageKind.Voice) && ctx.Job.HasManualAudio && _x.AudioImporter is null)
            problems.Add("Tayyor ovozni yuklab olish servisi ulanmagan (bu kompyuterdagi YtAuto versiyasini yangilang).");
        if (Pending(StageKind.Align) && !File.Exists(s.VoiceStudio.PythonPath))
            problems.Add($"Alignment uchun VoiceStudio Python muhiti topilmadi: {s.VoiceStudio.PythonPath}");
        if (Pending(StageKind.Edit) && !File.Exists(s.Claude.ExecutablePath))
            problems.Add($"Claude Code topilmadi: {s.Claude.ExecutablePath}");
        if ((Pending(StageKind.Edit) || Pending(StageKind.Render)) && !File.Exists(s.OpenFilm.ExePath))
            problems.Add($"OpenFilm topilmadi: {s.OpenFilm.ExePath}");
        if (Pending(StageKind.Archive))
        {
            if (string.IsNullOrWhiteSpace(s.Paths.VideoArchiveDir) || string.IsNullOrWhiteSpace(s.Paths.AudioArchiveDir))
                problems.Add("Video/ovoz arxiv papkalari sozlanmagan.");
            else if (PathGuard.IsUnder(ctx.WorkRoot, s.Paths.VideoArchiveDir) || PathGuard.IsUnder(ctx.WorkRoot, s.Paths.AudioArchiveDir))
                problems.Add("Arxiv papkasi vaqtinchalik ish papkasi ichida bo'lmasligi kerak (tozalashda o'chib ketadi).");
        }
        var tpl = s.FindTemplate(ctx.Job.TemplateName);
        if (!string.IsNullOrWhiteSpace(ctx.Job.TemplateName) && tpl is null) problems.Add($"Shablon topilmadi: {ctx.Job.TemplateName}");
        if (tpl is { Kind: TemplateKind.OpenFilmProject } && !File.Exists(Path.Combine(tpl.Path, "film.html")))
            problems.Add($"OpenFilm shablon papkasida film.html yo'q: {tpl.Path}");
        if (tpl is { Kind: TemplateKind.Mp4Reference } && !File.Exists(tpl.Path))
            problems.Add($"MP4 reference topilmadi: {tpl.Path}");
        if (problems.Count > 0) throw PipelineException.Config("preflight", string.Join(" ", problems));
    }

    private async Task ExecutePipelineAsync(JobContext ctx)
    {
        Preflight(ctx);
        await RunStageAsync(ctx, StageKind.Script, ScriptInputHash(ctx), () => ScriptStageAsync(ctx)).ConfigureAwait(false);
        var script = LoadScript(ctx);
        await RunStageAsync(ctx, StageKind.Voice, VoiceInputHash(ctx, script), () => VoiceStageAsync(ctx, script)).ConfigureAwait(false);
        await RunStageAsync(ctx, StageKind.Align, AlignInputHash(ctx, script), () => AlignStageAsync(ctx, script)).ConfigureAwait(false);

        // Montaj -> render -> sifat tekshiruvi; kamchilik bo'lsa cheklangan tuzatish sikli (TTS takrorlanmaydi).
        while (true)
        {
            await RunStageAsync(ctx, StageKind.Edit, EditInputHash(ctx, script), () => EditStageAsync(ctx, script)).ConfigureAwait(false);
            try
            {
                await RunStageAsync(ctx, StageKind.Render, RenderInputHash(ctx), () => RenderStageAsync(ctx)).ConfigureAwait(false);
                await RunStageAsync(ctx, StageKind.Validate, ValidateInputHash(ctx), () => ValidateStageAsync(ctx, script)).ConfigureAwait(false);
                break;
            }
            catch (EditorFixableException fx)
            {
                var max = ctx.S.Limits.MaxQaFixCycles;
                var summary = string.Join("; ", fx.Report.Errors.Take(6).Select(e => e.Message));
                if (ctx.Job.QaFixCycles >= max)
                    throw PipelineException.Quality("qa_failed", $"Sifat tekshiruvi {max} ta tuzatish siklidan keyin ham o'tmadi. Video YouTube'ga yuborilmadi. Kamchiliklar: {summary}");
                ctx.Job.QaFixCycles++;
                _x.Store.Update(ctx.Job);
                FileOps.WriteAllTextAtomic(ctx.QaFeedback, fx.Report.ToFeedbackMarkdown());
                FileOps.WriteAllTextAtomic(Path.Combine(ctx.RecordDir, $"qa_feedback_{ctx.Job.QaFixCycles}.md"), fx.Report.ToFeedbackMarkdown());
                ctx.Log.Warn($"Sifat kamchiliklari Claude'ga qaytarildi (tuzatish sikli {ctx.Job.QaFixCycles}/{max}): {summary}", "Validate");
            }
        }

        await RunStageAsync(ctx, StageKind.Archive, ArchiveInputHash(ctx), () => ArchiveStageAsync(ctx, script)).ConfigureAwait(false);
        if (_x.StopBeforeUpload)
        {
            ctx.Log.Info("Self-test rejimi: upload bosqichi o'tkazib yuborildi.");
            return;
        }
        await UploadStageAsync(ctx, script).ConfigureAwait(false);
    }

    /// <summary>
    /// Bosqichni checkpoint bilan bajaradi: avval tugagan va natija fayllari joyida bo'lsa - o'tkazib yuboradi.
    /// Bosqich qayta bajarilsa, undan keyingi bosqichlar bekor qilinadi.
    /// </summary>
    private async Task<StageOutputs> RunStageAsync(JobContext ctx, StageKind kind, string inputHash, Func<Task<StageOutputs>> exec)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        var rec = _x.Store.GetStage(ctx.Job.Id, kind);
        if (rec.State == StageState.Done)
        {
            var outputs = Deserialize(rec.OutputsJson);
            string why = "";
            var valid = rec.InputHash == inputHash && outputs.Files.All(f => FileOps.StillValid(f, out why));
            if (valid)
            {
                ctx.Log.Info($"{kind}: oldin tugagan, natijalar joyida - o'tkazib yuborildi.", kind.ToString());
                return outputs;
            }
            ctx.Log.Warn($"{kind}: qayta bajariladi ({(rec.InputHash != inputHash ? "kirish ma'lumotlari o'zgargan" : why)}).", kind.ToString());
        }

        foreach (var d in JobStateMachine.Downstream(kind))
        {
            var dr = _x.Store.GetStage(ctx.Job.Id, d);
            if (dr.State is StageState.Done or StageState.Failed or StageState.Running)
            {
                if (d == StageKind.Upload && !string.IsNullOrEmpty(ctx.Job.YouTubeVideoId)) continue; // yuklangan videoni hech qachon "bekor" qilmaymiz
                dr.State = StageState.Pending; _x.Store.SaveStage(dr);
            }
        }

        var target = JobStateMachine.StatusFor(kind);
        JobStateMachine.EnsureTransition(ctx.Job.Status, target);
        ctx.Job.Status = target;
        _x.Store.Update(ctx.Job);

        rec.State = StageState.Running; rec.Attempts++; rec.StartedAtUtc = _x.Clock.UtcNow; rec.FinishedAtUtc = null;
        rec.ErrorCode = null; rec.ErrorMessage = null; rec.InputHash = inputHash;
        _x.Store.SaveStage(rec);
        ctx.StageMetrics.Clear();
        var ramBefore = _x.Resources.AvailableRamGb();
        ctx.Log.Info($"{kind}: boshlandi (urinish {rec.Attempts}, bo'sh RAM {ramBefore:0.00} GB).", kind.ToString());
        var sw = Stopwatch.StartNew();
        try
        {
            var outputs = await exec().ConfigureAwait(false);
            rec.State = StageState.Done;
            rec.FinishedAtUtc = _x.Clock.UtcNow;
            rec.DurationMs = sw.ElapsedMilliseconds;
            rec.OutputsJson = JsonSerializer.Serialize(outputs);
            ctx.StageMetrics["ram_free_before_gb"] = Math.Round(ramBefore, 2);
            ctx.StageMetrics["ram_free_after_gb"] = Math.Round(_x.Resources.AvailableRamGb(), 2);
            rec.MetricsJson = JsonSerializer.Serialize(ctx.StageMetrics);
            _x.Store.SaveStage(rec);
            ctx.Log.Info($"{kind}: tugadi ({sw.Elapsed.TotalSeconds:0.0}s). {FormatMetrics(ctx.StageMetrics)}", kind.ToString());
            return outputs;
        }
        catch (Exception ex)
        {
            rec.State = StageState.Failed;
            rec.FinishedAtUtc = _x.Clock.UtcNow;
            rec.DurationMs = sw.ElapsedMilliseconds;
            rec.ErrorCode = (ex as PipelineException)?.Code ?? (ex is OperationCanceledException ? "cancelled" : "internal_error");
            rec.ErrorMessage = Shorten(ex.Message, 1000);
            rec.MetricsJson = JsonSerializer.Serialize(ctx.StageMetrics);
            _x.Store.SaveStage(rec);
            throw;
        }
    }

    private static string FormatMetrics(Dictionary<string, double> m) =>
        m.Count == 0 ? "" : string.Join(", ", m.Select(kv => $"{kv.Key}={kv.Value:0.##}"));

    internal static StageOutputs Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? new StageOutputs() : JsonSerializer.Deserialize<StageOutputs>(json) ?? new StageOutputs();

    internal static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
