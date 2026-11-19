using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Claude;
using YtAuto.Infrastructure.Media;
using YtAuto.Infrastructure.OpenFilm;
using Xunit;

namespace YtAuto.Tests;

/// <summary>
/// Qo'lda ishga tushiriladigan uchidan-uchiga sinov: haqiqiy OpenFilm (stok qidiruv + render), ffmpeg va Claude Code; ssenariy, ovoz,
/// Gemini va YouTube - soxta (bazaga va kanalga hech narsa ketmaydi). Pexels o'rniga lokal mock server (OPENFILM_PEXELS_URL).
/// Faqat YTAUTO_MANUAL_STOCK_OUT o'rnatilganda ishlaydi; natija (film/, record, mp4) o'sha papkaga nusxalanadi.
/// <code>
/// set YTAUTO_MANUAL_STOCK_OUT=C:\tmp\stock-run
/// set YTAUTO_MANUAL_PEXELS_URL=http://127.0.0.1:8766
/// set YTAUTO_MANUAL_CLAUDE=C:\...\claude.exe
/// set YTAUTO_MANUAL_OPENFILM=C:\...\OpenFilm.exe
/// dotnet test --filter ManualStockRunTests
/// </code>
/// </summary>
public class ManualStockRunTests
{
    [Fact]
    public async Task RealOpenFilmSearch_ClaudeReviewsAndDesigns_RealRender()
    {
        var outDir = Environment.GetEnvironmentVariable("YTAUTO_MANUAL_STOCK_OUT");
        if (string.IsNullOrWhiteSpace(outDir)) return; // oddiy test yugurishida o'tkazib yuboriladi
        Environment.SetEnvironmentVariable("OPENFILM_PEXELS_URL", Environment.GetEnvironmentVariable("YTAUTO_MANUAL_PEXELS_URL"));

        using var h = new Harness();
        var s = h.Settings.S;
        s.OpenFilm.ExePath = Environment.GetEnvironmentVariable("YTAUTO_MANUAL_OPENFILM")!;
        s.Claude.ExecutablePath = Environment.GetEnvironmentVariable("YTAUTO_MANUAL_CLAUDE")!;
        s.Claude.TimeoutMinutes = 40; s.Claude.MaxBudgetUsd = 6;
        s.Claude.MontagePrompt = "Make a short, polished explainer video about why water boils.";
        s.Visuals.MaxStockScenes = 1;
        var log = new ConsoleishLog(Path.Combine(outDir, "run.log"));

        var film = new OpenFilmTool(() => h.Settings.S.OpenFilm);
        var media = new FfmpegMediaProbe(() => film.FfmpegDir);
        var vis = new FakeVisuals { Plan = new MediaPlan { Items = { new() { Kind = "photo", Query = "boiling water pot", Purpose = "water boiling in a pot, bubbles rising", At = 0 } } } };
        var stock = new OpenFilmStockSearch(film, () => new StockPhotoKeys("test-key", ""), Path.Combine(h.Tmp.Path, "openfilm-stock-home"));
        var runner = new JobRunner(new PipelineServices
        {
            Store = h.Store, Settings = h.Settings, Secrets = h.Secrets, ScriptGenerator = h.Script, Tts = h.Tts, Aligner = h.Aligner,
            Editor = new ClaudeEditorAgent(() => h.Settings.S.Claude), Film = film, Media = media, Uploader = h.Uploader,
            Resources = new FakeResources(), Paths = h.Paths, Clock = h.Clock, JobLogFactory = _ => log, RunLog = log,
            PostUploadPoll = TimeSpan.Zero, StopBeforeUpload = true, Visuals = vis, Stock = stock,
        });
        var job = h.AddJob();
        var r = await runner.RunOnceAsync("manual-" + Guid.NewGuid().ToString("N")[..6], CancellationToken.None);
        var j = h.Store.Get(job.Id)!;
        log.Info($"RESULT {r.Outcome} status={j.Status} error={j.LastErrorCode} {j.LastErrorMessage}");

        Directory.CreateDirectory(outDir);
        var edit = Path.Combine(j.WorkDir ?? "", "edit");
        if (Directory.Exists(edit)) CopyDir(edit, Path.Combine(outDir, "edit"));
        var rec = Path.Combine(h.Paths.RecordsDir, $"job-{job.Id}");
        if (Directory.Exists(rec)) CopyDir(rec, Path.Combine(outDir, "record"));
        if (File.Exists(j.VideoArchivePath)) File.Copy(j.VideoArchivePath!, Path.Combine(outDir, "final.mp4"), true);
    }

    private static void CopyDir(string src, string dst)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel.Contains(".film" + Path.DirectorySeparatorChar + "cache")) continue;
            var t = Path.Combine(dst, rel); Directory.CreateDirectory(Path.GetDirectoryName(t)!); File.Copy(f, t, true);
        }
    }

    private sealed class ConsoleishLog : IJobLog
    {
        private readonly string _path; private readonly object _gate = new();
        public ConsoleishLog(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        private void W(string lvl, string m, string? s) { lock (_gate) File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} {lvl} [{s}] {m}\n"); }
        public void Info(string m, string? s = null) => W("I", m, s);
        public void Warn(string m, string? s = null) => W("W", m, s);
        public void Error(string m, string? s = null) => W("E", m, s);
        public void Debug(string m, string? s = null) => W("D", m, s);
    }
}
