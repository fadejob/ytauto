using System.Text.Json;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Worker;

/// <summary>
/// Lokal zanjir sinovi (alohida ma'lumotlar papkasida, foydalanuvchi navbatiga tegmaydi):
/// tayyor ssenariy → VoiceStudio TTS → alignment → Claude montaji → OpenFilm render → QA → arxiv. Gemini va YouTube chaqirilmaydi.
/// Qayta ishga tushirilsa, tugallanmagan vazifani davom ettiradi (resume sinovi). `--new` - yangi vazifa.
/// </summary>
internal static class SelfTest
{
    private sealed class StaticScriptGenerator : IScriptGenerator
    {
        public Task<ScriptResult> GenerateAsync(ScriptRequest request, IJobLog log, CancellationToken ct)
        {
            log.Info("Self-test: Gemini o'rniga tayyor ssenariy ishlatildi.", "Script");
            var s = new VideoScript
            {
                YouTubeTitle = "How a Night Worker Builds a Video",
                NarrationText = "Every night at one o'clock, a small program wakes up on this computer. " +
                                "First, it takes the oldest topic from the queue and writes a short script. " +
                                "Then a local voice engine reads the script aloud, and a speech model finds the exact time of every word. " +
                                "Next, an editor builds the scenes, and the video is rendered and checked frame by frame. " +
                                "Only a video that passes every check is saved and scheduled. By morning, the work is done.",
                Scenes = new()
                {
                    new() { Index = 1, Narration = "Every night at one o'clock, a small program wakes up on this computer.", Visual = "A dark desk scene: a clock showing 01:00 and a small glowing computer icon that lights up.", OnScreenText = "01:00 - worker wakes" },
                    new() { Index = 2, Narration = "First, it takes the oldest topic from the queue and writes a short script.", Visual = "A vertical queue of topic cards; the bottom (oldest) card slides out and turns into a page of text lines.", OnScreenText = "Oldest topic → script" },
                    new() { Index = 3, Narration = "Then a local voice engine reads the script aloud, and a speech model finds the exact time of every word.", Visual = "A waveform grows from left to right; small word labels snap onto the waveform with time ticks.", OnScreenText = "Voice + word timing" },
                    new() { Index = 4, Narration = "Next, an editor builds the scenes, and the video is rendered and checked frame by frame.", Visual = "Four scene thumbnails assemble on a timeline; a check mark passes over each frame.", OnScreenText = "Build, render, check" },
                    new() { Index = 5, Narration = "Only a video that passes every check is saved and scheduled. By morning, the work is done.", Visual = "A shield with a check mark, then a calendar page with a sunrise.", OnScreenText = "Checked → scheduled" },
                },
                YouTubeDescription = "A self-test video made by the local pipeline.",
                Tags = new() { "selftest" },
            };
            s.SubtitleText = s.NarrationText;
            s.OnScreenTexts = s.Scenes.Select(x => x.OnScreenText).ToList();
            return Task.FromResult(new ScriptResult { Script = s, RawModelOutput = s.ToJson(), ModelUsed = "static-selftest" });
        }
    }

    public static async Task<int> RunAsync(AppServices svc, CancellationToken ct)
    {
        var live = AppServices.DefaultSettings();
        // Foydalanuvchining asosiy sozlamalaridan tashqi dastur yo'llarini olamiz (agar mavjud bo'lsa).
        var mainSettings = new Infrastructure.Data.SqliteSettingsStore(new Infrastructure.Data.SqliteJobStore(new AppPaths(null).DatabasePath), AppServices.DefaultSettings).Load();
        var s = svc.Settings.Load();
        if (s.Paths.WorkDir == live.Paths.WorkDir || !s.Paths.WorkDir.StartsWith(svc.Paths.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            s = mainSettings.Clone();
            s.Paths.WorkDir = Path.Combine(svc.Paths.DataRoot, "jobs");
            s.Paths.VideoArchiveDir = Path.Combine(svc.Paths.DataRoot, "archive", "video");
            s.Paths.AudioArchiveDir = Path.Combine(svc.Paths.DataRoot, "archive", "audio");
            s.Gemini.UseSearchGrounding = false;
            if (s.VoiceProfiles.Count == 0) s.VoiceProfiles = live.VoiceProfiles;
            s.Defaults.Language = "en";
            s.Templates.RemoveAll(t => t.Name == "selftest");
            svc.Settings.Save(s);
        }

        var all = svc.Store.ListByStatus(Enum.GetValues<JobStatus>());
        var pending = all.LastOrDefault(j => j.Status is not (JobStatus.Archived or JobStatus.Cancelled or JobStatus.Published));
        VideoJob job;
        if (pending is not null && !Environment.GetCommandLineArgs().Contains("--new"))
        {
            job = pending;
            if (job.Status is JobStatus.Failed or JobStatus.NeedsAttention) svc.Store.Requeue(job.Id, resetAttempts: true);
            Console.WriteLine($"Self-test: tugallanmagan vazifa #{job.Id} ({job.Status}) davom ettiriladi.");
        }
        else
        {
            job = new VideoJob
            {
                Topic = "Self-test: night worker", Notes = "local chain test", Language = "en", TargetDurationSec = 0,
                Format = VideoFormat.Landscape16x9, VoiceProfileName = s.Defaults.VoiceProfile,
                TemplateName = Environment.GetCommandLineArgs().SkipWhile(a => a != "--template").Skip(1).FirstOrDefault() ?? "",
                PublishAtUtc = DateTimeOffset.UtcNow.AddDays(2), Status = JobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            svc.Store.Insert(job);
            Console.WriteLine($"Self-test: yangi vazifa #{job.Id} yaratildi (data: {svc.Paths.DataRoot}).");
        }

        var watchDirs = new[]
        {
            Path.GetDirectoryName(s.VoiceStudio.ExePath) ?? "", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceStudio"),
            Path.GetDirectoryName(s.OpenFilm.ExePath) ?? "", Path.GetDirectoryName(s.Claude.ExecutablePath) ?? "",
        };
        var before = ProcessSnapshot.Under(watchDirs).Select(p => (p.Pid, p.StartUtc)).ToHashSet();
        var ramBefore = svc.Resources.AvailableRamGb();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        svc.Store.StartRun(svc.RunId, Environment.ProcessId, self.StartTime.ToUniversalTime(), "selftest");
        var runner = new JobRunner(svc.Pipeline(stopBeforeUpload: true, scriptOverride: new StaticScriptGenerator()));
        var result = await runner.RunOnceAsync(svc.RunId, ct);
        svc.Store.FinishRun(svc.RunId, result.Outcome.ToString(), result.JobId, result.Message);
        await Task.Delay(2000, CancellationToken.None);
        var after = ProcessSnapshot.Under(watchDirs).Where(p => !before.Contains((p.Pid, p.StartUtc))).ToList();

        var fresh = svc.Store.Get(job.Id)!;
        var report = new
        {
            job_id = job.Id, outcome = result.Outcome.ToString(), status = fresh.Status.ToString(), error = result.Message,
            total_minutes = Math.Round(sw.Elapsed.TotalMinutes, 2), ram_free_before_gb = Math.Round(ramBefore, 2), ram_free_after_gb = Math.Round(svc.Resources.AvailableRamGb(), 2),
            leftover_processes_started_by_run = after.Select(p => $"{p.Pid} {p.Path}").ToList(),
            audio_sec = fresh.AudioDurationSec, video = fresh.VideoArchivePath, audio = fresh.AudioArchivePath, qa_fix_cycles = fresh.QaFixCycles,
            stages = svc.Store.GetStages(job.Id).Select(st => new
            {
                stage = st.Stage.ToString(), state = st.State.ToString(), attempts = st.Attempts, seconds = Math.Round((st.DurationMs ?? 0) / 1000.0, 1),
                error = st.ErrorMessage, metrics = st.MetricsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, double>>(st.MetricsJson),
            }),
            record_dir = svc.Paths.RecordDirFor(job.Id),
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(svc.Paths.RecordDirFor(job.Id), $"selftest-report-{DateTime.Now:yyyyMMdd-HHmmss}.json"), json);
        Console.WriteLine(json);
        return result.Outcome is RunOutcome.StoppedBeforeUpload or RunOutcome.Completed ? 0 : 1;
    }
}
