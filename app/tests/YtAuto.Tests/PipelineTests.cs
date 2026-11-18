using System.Globalization;
using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Media;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Qa;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

// ------------------------------------------------------------------ fakes
internal sealed class MemSettings : ISettingsStore { public AppSettings S = new(); public AppSettings Load() => S.Clone(); public void Save(AppSettings s) => S = s.Clone(); }
internal sealed class MemSecrets : ISecretStore { public readonly Dictionary<string, string> D = new(); public string? Get(string n) => D.GetValueOrDefault(n); public void Set(string n, string? v) { if (v is null) D.Remove(n); else D[n] = v; } }
internal sealed class FakeClock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow; }
internal sealed class FakeResources : IResourceMonitor { public double AvailableRamGb() => 4; public double TotalRamGb() => 8; public double FreeDiskGb(string p) => 100; }
internal sealed class FakePaths : IAppPaths
{
    public FakePaths(string root) { DataRoot = root; }
    public string DataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "db.sqlite");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string RecordsDir => Path.Combine(DataRoot, "records");
    public string ToolsDir => Path.Combine(DataRoot, "tools");
    public string RecordDirFor(long id) { var d = Path.Combine(RecordsDir, $"job-{id}"); Directory.CreateDirectory(d); return d; }
}
internal sealed class NullLog : IJobLog
{
    public readonly List<string> Lines = new();
    public void Info(string m, string? s = null) => Lines.Add("I " + m);
    public void Warn(string m, string? s = null) => Lines.Add("W " + m);
    public void Error(string m, string? s = null) => Lines.Add("E " + m);
    public void Debug(string m, string? s = null) { }
}

internal static class TestScript
{
    public const string Narration = "Water boils when it gets hot enough. Bubbles of steam form at the bottom of the pot. They rise and burst at the surface. That is why boiling water looks busy.";
    public static VideoScript Make() => new()
    {
        NarrationText = Narration, SubtitleText = Narration, YouTubeTitle = "Why water boils", YouTubeDescription = "Short explainer.",
        Scenes = new()
        {
            new() { Index = 1, Narration = "Water boils when it gets hot enough.", Visual = "pot", OnScreenText = "Heat" },
            new() { Index = 2, Narration = "Bubbles of steam form at the bottom of the pot. They rise and burst at the surface.", Visual = "bubbles", OnScreenText = "Steam bubbles" },
            new() { Index = 3, Narration = "That is why boiling water looks busy.", Visual = "busy", OnScreenText = "Boiling" },
        },
    };
}

internal sealed class FakeScript : IScriptGenerator
{
    public int Calls; public ScriptRequest? Last;
    public Task<ScriptResult> GenerateAsync(ScriptRequest r, IJobLog log, CancellationToken ct)
    { Calls++; Last = r; var s = TestScript.Make(); return Task.FromResult(new ScriptResult { Script = s, RawModelOutput = s.ToJson(), ModelUsed = "fake" }); }
}

internal sealed class FakeTts : ITtsService
{
    public int Opens, Synth;
    public Task<ITtsSession> OpenAsync(VoiceStudioSettings s, IJobLog log, CancellationToken ct) { Opens++; return Task.FromResult<ITtsSession>(new Session(this)); }
    private sealed class Session : ITtsSession
    {
        private readonly FakeTts _o; public Session(FakeTts o) => _o = o;
        public bool StartedByUs => true; public string Version => "fake"; public Dictionary<string, double> Metrics { get; } = new();
        public Task EnsureModelAvailableAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public Task SynthesizeAsync(string text, VoiceProfile v, string outWav, CancellationToken ct) { _o.Synth++; WriteSpeechLikeWav(outWav, text.Length / 15.0); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public static void WriteSpeechLikeWav(string p, double sec)
    {
        int sr = 16000; int n = (int)(sr * sec);
        using var bw = new BinaryWriter(File.Create(p));
        bw.Write("RIFF"u8); bw.Write(36 + n * 2); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16); bw.Write("data"u8); bw.Write(n * 2);
        for (int i = 0; i < n; i++) bw.Write((short)(Math.Sin(i * 2 * Math.PI * 180 / sr) * 0.3 * 32767 * (0.6 + 0.4 * Math.Sin(i * 2 * Math.PI * 3 / sr))));
    }
}

internal sealed class FakeAligner : IAligner
{
    public int Calls;
    public Dictionary<string, double> LastMetrics { get; } = new();
    public string Describe() => "fake";
    public Task<IReadOnlyList<WordTiming>> TranscribeWordsAsync(string wav, string lang, string outJson, IJobLog log, CancellationToken ct)
    {
        Calls++;
        var d = Wav.ReadInfo(wav).Duration; var words = TextNormalizer.Tokenize(TestScript.Narration);
        var step = d / words.Count;
        File.WriteAllText(outJson, "{}");
        return Task.FromResult<IReadOnlyList<WordTiming>>(words.Select((w, i) => new WordTiming { Word = " " + w.Word, Start = i * step, End = i * step + step * 0.8 }).ToList());
    }
}

internal sealed class FakeEditor : IEditorAgent
{
    public int Calls; public int GappyCalls; public int TransientFailures; public Func<Task>? DuringRun; public string? LastInstructions;
    /// <summary>Haqiqiy Claude kabi film/cover.html (muqova) ham yozadi.</summary>
    public bool WriteCover = true;
    /// <summary>Tuzatish sikllarida Claude ko'rgan qa_feedback.md matnlari.</summary>
    public readonly List<string> Feedbacks = new();
    public string Version => "fake-claude";
    public async Task<EditorRunResult> RunAsync(string dir, string prompt, TimeSpan timeout, string prefix, IReadOnlyList<string> extra, IJobLog log, CancellationToken ct)
    {
        Calls++;
        var ins = Path.Combine(dir, "instructions.md");
        if (File.Exists(ins)) LastInstructions = File.ReadAllText(ins);
        var fb = Path.Combine(dir, "qa_feedback.md");
        if (File.Exists(fb)) Feedbacks.Add(File.ReadAllText(fb));
        if (DuringRun is not null) await DuringRun();
        if (TransientFailures > 0) { TransientFailures--; return new EditorRunResult { Run = new ToolRunResult { ExitCode = 1, StderrTail = "network glitch" }, IsError = true }; }
        Assert.True(File.Exists(Path.Combine(dir, "instructions.md")));
        var d = Wav.ReadInfo(Path.Combine(dir, "film", "assets", "voice.wav")).Duration;
        var inv = CultureInfo.InvariantCulture;
        // Haqiqiy Claude kabi kadr o'lchamini instructions.md dan oladi.
        var vp = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(dir, "instructions.md")), @"width=(\d+), height=(\d+)");
        var viewport = vp.Success ? $"width={vp.Groups[1].Value}, height={vp.Groups[2].Value}" : "width=1920, height=1080";
        string html;
        if (GappyCalls > 0)
        {
            GappyCalls--;
            html = $"<meta name=\"viewport\" content=\"{viewport}\"><section><iframe src=\"a.html#t=0,{(d / 3).ToString(inv)}\" at=\"0\"></iframe><iframe src=\"b.html#t=0,{(d / 3).ToString(inv)}\" at=\"{(d / 2).ToString(inv)}\"></iframe></section><section><audio src=\"assets/voice.wav\" at=\"0\"></audio></section>";
        }
        else
            html = $"<meta name=\"viewport\" content=\"{viewport}\"><section><iframe src=\"a.html#t=0,{(d / 2).ToString(inv)}\" at=\"0\"></iframe><iframe src=\"b.html#t=0,{(d / 2 + 0.2).ToString(inv)}\" at=\"{(d / 2).ToString(inv)}\"></iframe></section><section><audio src=\"assets/voice.wav\" at=\"0\"></audio></section>";
        File.WriteAllText(Path.Combine(dir, "film", "film.html"), html);
        File.WriteAllText(Path.Combine(dir, "film", "BUILD_NOTES.md"), "notes");
        if (WriteCover) File.WriteAllText(Path.Combine(dir, "film", "cover.html"), $"<meta name=\"viewport\" content=\"{viewport}\"><h1>Cover</h1>");
        return new EditorRunResult { Run = new ToolRunResult { ExitCode = 0 }, ResultText = "DONE", SessionId = Guid.NewGuid().ToString() };
    }
}

internal sealed class FakeFilm : IFilmTool, IFilmSession
{
    public int Renders, Looks;
    public string Version => "fake-openfilm";
    public string FfmpegDir => "";
    public Task<string> GetSkillTextAsync(string name, CancellationToken ct) => Task.FromResult("skill text");
    public string WriteShims(string dir) { Directory.CreateDirectory(dir); return dir; }
    public Task<IFilmSession> OpenSessionAsync(IJobLog log, CancellationToken ct) => Task.FromResult<IFilmSession>(this);
    public readonly List<string> LookArgs = new();
    /// <summary>Haqiqiy `openfilm look [what] t` kabi: bitta kadr so'ralsa .film/cache/look ga PNG yozib, yo'lini chiqaradi.</summary>
    public Task<ToolRunResult> LookAsync(string filmDir, IReadOnlyList<string> args, string p, TimeSpan t, CancellationToken ct)
    {
        Looks++; LookArgs.Add(string.Join(' ', args));
        if (args.Count == 0 || args[0].StartsWith("--")) return Task.FromResult(new ToolRunResult { ExitCode = 0 });
        if (args[0].EndsWith(".html") && !File.Exists(Path.Combine(filmDir, args[0]))) return Task.FromResult(new ToolRunResult { ExitCode = 1, StderrTail = "no such page" });
        var png = Path.Combine(filmDir, ".film", "cache", "look", $"frame {Looks}.png"); // yo'lda bo'sh joy: haqiqiy profil papkalari kabi
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, new byte[] { 137, 80, 78, 71 });
        return Task.FromResult(new ToolRunResult { ExitCode = 0, StdoutTail = $"frame {args[^1]}s: {png}\n" });
    }
    public Task<ToolRunResult> RenderAsync(string filmDir, string outMp4, RenderOptions o, CancellationToken ct)
    {
        Renders++;
        var d = Wav.ReadInfo(Path.Combine(filmDir, "assets", "voice.wav")).Duration;
        var bytes = new byte[20_000]; BitConverter.GetBytes(d).CopyTo(bytes, 0); bytes[100] = (byte)Renders;
        File.WriteAllBytes(outMp4, bytes);
        return Task.FromResult(new ToolRunResult { ExitCode = 0 });
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMedia : IMediaProbe
{
    public string Version => "fake-ffmpeg";
    public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct)
    {
        if (path.EndsWith(".wav")) return Task.FromResult(new MediaInfo { Duration = Wav.ReadInfo(path).Duration, HasAudio = true });
        var d = BitConverter.ToDouble(File.ReadAllBytes(path), 0);
        return Task.FromResult(new MediaInfo { Duration = d + 0.05, HasAudio = true, HasVideo = true, Width = 1920, Height = 1080 });
    }
    public Task<(bool Ok, string Errors)> DecodeCheckAsync(string path, CancellationToken ct) => Task.FromResult((true, ""));
    public Task<IReadOnlyList<BlackSegment>> BlackDetectAsync(string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<BlackSegment>>(Array.Empty<BlackSegment>());
    public Task<(double MaxVolumeDb, double? IntegratedLufs)> AudioLevelsAsync(string path, CancellationToken ct) => Task.FromResult((-1.5, (double?)-14.2));
    public Task ExtractFrameAsync(string video, double at, string outImage, int width, CancellationToken ct) { File.WriteAllBytes(outImage, new byte[10]); FrameSources.Add(video); return Task.CompletedTask; }
    public readonly List<string> FrameSources = new();
    public int Embeds;
    /// <summary>Haqiqiy ffmpeg kabi: yangi fayl asl videodan katta (video baytlari + muqova).</summary>
    public Task EmbedCoverAsync(string mp4, string image, string outMp4, CancellationToken ct)
    { Embeds++; File.WriteAllBytes(outMp4, File.ReadAllBytes(mp4).Concat(File.ReadAllBytes(image)).ToArray()); return Task.CompletedTask; }
    public Task<(double Mean, double Std)> FrameLumaAsync(string video, double at, CancellationToken ct) => Task.FromResult((90.0, 40.0));
}

internal sealed class FakeUploader : IVideoUploader
{
    public bool Connected = true; public int Initiations, Resumes; public int FailAfterSession; public bool LoseSession;
    public readonly HashSet<string> Public = new(); public int MakePublicCalls; public bool? LastPublishNow;
    public bool IsConnected => Connected;
    public UploadRequest? Last;
    public Task<string> UploadAsync(UploadRequest r, IUploadStateSink state, IJobLog log, CancellationToken ct)
    {
        Last = r;
        Assert.True(File.Exists(r.VideoPath));
        var existing = state.LoadSessionUri();
        if (existing is not null)
        {
            Resumes++;
            if (LoseSession) throw PipelineException.Ambiguous("upload_session_lost", "lost");
            state.SaveVideoId("vid-1"); return Task.FromResult("vid-1");
        }
        Initiations++;
        LastPublishNow = r.PublishNow;
        if (r.PublishNow) Public.Add("vid-1");
        state.SaveSessionUri("https://upload.example/session?upload_id=SECRET", DateTimeOffset.UtcNow);
        if (FailAfterSession > 0) { FailAfterSession--; throw PipelineException.Transient("youtube_network", "connection reset"); }
        state.SaveVideoId("vid-1");
        return Task.FromResult("vid-1");
    }
    public Task<YouTubeVideoStatus> GetStatusAsync(string id, CancellationToken ct) => Task.FromResult(Public.Contains(id)
        ? new YouTubeVideoStatus { VideoId = id, Exists = true, UploadStatus = "processed", PrivacyStatus = "public", ProcessingStatus = "succeeded", PublishedAtUtc = DateTimeOffset.UtcNow }
        : new YouTubeVideoStatus { VideoId = id, Exists = true, UploadStatus = "processed", PrivacyStatus = "private", ProcessingStatus = "succeeded", PublishAtUtc = DateTimeOffset.UtcNow.AddDays(2) });
    public Task MakePublicAsync(string id, CancellationToken ct) { MakePublicCalls++; Public.Add(id); return Task.CompletedTask; }
    public Task<string?> FindRecentUploadAsync(string title, DateTimeOffset since, CancellationToken ct) => Task.FromResult<string?>(null);
    public readonly List<string> Thumbnails = new();
    public Task<bool> SetThumbnailAsync(string id, string img, CancellationToken ct) { Assert.True(File.Exists(img)); Thumbnails.Add(img); return Task.FromResult(true); }
}

// ------------------------------------------------------------------ harness
internal sealed class Harness : IDisposable
{
    public readonly TempDir Tmp = new();
    public readonly SqliteJobStore Store;
    public readonly MemSettings Settings = new();
    public readonly MemSecrets Secrets = new();
    public readonly FakeClock Clock = new();
    public readonly FakeScript Script = new();
    public readonly FakeTts Tts = new();
    public readonly FakeAligner Aligner = new();
    public readonly FakeEditor Editor = new();
    public readonly FakeFilm Film = new();
    public readonly FakeUploader Uploader = new();
    public readonly FakeMedia Media = new();
    public readonly NullLog Log = new();
    public readonly FakePaths Paths;
    public IAudioImporter? AudioImporter;

    public Harness()
    {
        Paths = new FakePaths(Tmp.Path);
        Store = new SqliteJobStore(Paths.DatabasePath);
        var s = new AppSettings();
        s.Paths.WorkDir = Path.Combine(Tmp.Path, "jobs");
        s.Paths.VideoArchiveDir = Path.Combine(Tmp.Path, "archive", "video");
        s.Paths.AudioArchiveDir = Path.Combine(Tmp.Path, "archive", "audio");
        s.VoiceProfiles.Add(new VoiceProfile { Name = "v", Model = TtsVoices.Model, Voice = "x" });
        s.Claude.MontagePrompt = "PROMPT-V1";
        s.OpenFilm.LookFullAfterRender = false;
        // Preflight tashqi dastur yo'llarini tekshiradi: sinovda mavjud fayllarga ishora qilamiz.
        var exe = Environment.ProcessPath!;
        s.VoiceStudio.ExePath = exe; s.VoiceStudio.PythonPath = exe; s.Claude.ExecutablePath = exe; s.OpenFilm.ExePath = exe;
        Settings.S = s;
    }

    public Action<VideoJob>? OnPublished;
    public Action<string, string, string>? OnVoiceCloneStatus;
    /// <summary>Berilsa FakeTts o'rniga ishlatiladi (masalan klon profil chaqiruvlarini yozib oluvchi TTS).</summary>
    public ITtsService? TtsOverride;

    public JobRunner Runner(bool stopBeforeUpload = false) => new(new PipelineServices
    {
        Store = Store, Settings = Settings, Secrets = Secrets, ScriptGenerator = Script, Tts = TtsOverride ?? Tts, AudioImporter = AudioImporter, Aligner = Aligner, Editor = Editor,
        Film = Film, Media = Media, Uploader = Uploader, Resources = new FakeResources(), Paths = Paths, Clock = Clock,
        JobLogFactory = _ => Log, RunLog = Log, PostUploadPoll = TimeSpan.Zero, StopBeforeUpload = stopBeforeUpload, OnPublished = OnPublished,
        OnVoiceCloneStatus = OnVoiceCloneStatus, Visuals = Visuals, MediaLibrary = MediaLib,
    });
    public YtAuto.Core.Visuals.IVisualDirector? Visuals;
    public YtAuto.Core.Visuals.IMediaLibrary? MediaLib;

    public VideoJob AddJob(string topic = "Boiling water", int minutesAgo = 10, int publishInDays = 3)
    {
        var j = new VideoJob
        {
            Topic = topic, Language = "en", TargetDurationSec = 12, VoiceProfileName = "v", Format = VideoFormat.Landscape16x9,
            PublishAtUtc = Clock.UtcNow.AddDays(publishInDays), Status = JobStatus.Queued, CreatedAtUtc = Clock.UtcNow.AddMinutes(-minutesAgo),
        };
        Store.Insert(j);
        return j;
    }

    public Task<RunSummary> Run(string id = "run") => Runner().RunOnceAsync(id + Guid.NewGuid().ToString("N")[..6], CancellationToken.None);
    public void Dispose() => Tmp.Dispose();
}

// ------------------------------------------------------------------ tests
public class PipelineTests
{
    [Fact]
    public async Task FullRun_ProducesScheduledVideo_ArchivesAndCleansWorkDir()
    {
        using var h = new Harness();
        var job = h.AddJob();
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Equal("vid-1", j.YouTubeVideoId);
        Assert.True(File.Exists(j.VideoArchivePath));
        Assert.True(File.Exists(j.AudioArchivePath));
        Assert.True(j.WorkDirCleaned);
        Assert.False(Directory.Exists(j.WorkDir));
        // Ish tugagach faqat video va audio qoladi: records, subtitr, thumbnail va metadata o'chiriladi.
        Assert.False(Directory.Exists(Path.Combine(h.Paths.RecordsDir, $"job-{job.Id}")));
        Assert.Null(j.SubtitleArchivePath);
        Assert.Null(j.ThumbnailPath);
        Assert.Equal(new[] { ".mp4" }, Directory.GetFiles(Path.GetDirectoryName(j.VideoArchivePath)!).Select(Path.GetExtension).ToArray());
        Assert.Equal(new[] { ".wav" }, Directory.GetFiles(Path.GetDirectoryName(j.AudioArchivePath)!).Select(Path.GetExtension).ToArray());
        Assert.Equal(1, h.Uploader.Initiations);
        Assert.Null(h.Secrets.Get(SecretNames.UploadSessionPrefix + job.Id)); // sessiya URI tozalandi
        Assert.Equal("Why water boils", j.Title);
    }

    [Fact]
    public async Task OneJobPerRun_EmptyQueueOpensNothing()
    {
        using var h = new Harness();
        h.AddJob("first", 20); h.AddJob("second", 10);
        await h.Run();
        Assert.Equal(1, h.Script.Calls);
        Assert.Single(h.Store.ListByStatus(JobStatus.Queued));
        await h.Run();
        Assert.Equal(2, h.Script.Calls);
        var tts = h.Tts.Opens;
        var r = await h.Run();
        Assert.Equal(RunOutcome.QueueEmpty, r.Outcome);
        Assert.Equal(tts, h.Tts.Opens);
    }

    [Fact]
    public async Task ResumeAfterFailure_DoesNotRepeatFinishedStages()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Editor.TransientFailures = 1;
        var r1 = await h.Run();
        Assert.Equal(RunOutcome.Failed, r1.Outcome);
        var j1 = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Failed, j1.Status);
        Assert.Equal(1, j1.RunAttempts);
        var synth = h.Tts.Synth;

        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1); // backoff o'tdi
        var r2 = await h.Run();
        Assert.Equal(RunOutcome.Completed, r2.Outcome);
        Assert.Equal(1, h.Script.Calls);     // ssenariy qayta yozilmadi
        Assert.Equal(synth, h.Tts.Synth);    // TTS takrorlanmadi
        Assert.Equal(1, h.Aligner.Calls);    // timing takrorlanmadi
        Assert.Equal(2, h.Editor.Calls);
        Assert.Contains(h.Log.Lines, l => l.Contains("Script: oldin tugagan"));
    }

    [Fact]
    public async Task CrashMidStage_IsDetectedAndResumed()
    {
        using var h = new Harness();
        var job = h.AddJob();
        // Birinchi ishga tushish: Archive'gacha (upload'siz)
        await h.Runner(stopBeforeUpload: true).RunOnceAsync("r1", CancellationToken.None);
        // Kompyuter o'chib qolgandek: holat "Rendering", bosqich "Running", band qilish qolib ketgan
        var j = h.Store.Get(job.Id)!; j.Status = JobStatus.Rendering; h.Store.Update(j);
        var st = h.Store.GetStage(job.Id, StageKind.Upload); st.State = StageState.Running; h.Store.SaveStage(st);
        h.Store.ClaimNext("dead-run", h.Clock.UtcNow);
        var renders = h.Film.Renders;
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(renders, h.Film.Renders); // render natijasi joyida - qayta render qilinmadi
        Assert.Equal(1, h.Store.Get(job.Id)!.RunAttempts); // uzilish urinish sifatida hisoblandi
    }

    [Fact]
    public async Task QaFailure_GoesBackToEditor_WithoutRepeatingTts()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Editor.GappyCalls = 1;
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(1, j.QaFixCycles);
        Assert.Equal(2, h.Editor.Calls);
        Assert.Equal(2, h.Film.Renders);
        Assert.Equal(1, h.Tts.Opens);
        Assert.Contains(h.Log.Lines, l => l.Contains("tuzatish sikli 1/"));  // records\job-N ish tugagach tozalanadi
    }

    [Fact]
    public async Task QaFixLimit_NeedsAttention_AndNothingUploaded()
    {
        using var h = new Harness();
        h.Settings.S.Limits.MaxQaFixCycles = 1;
        var job = h.AddJob();
        h.Editor.GappyCalls = 99;
        var r = await h.Run();
        Assert.Equal(RunOutcome.NeedsAttention, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.NeedsAttention, j.Status);
        Assert.Equal("qa_failed", j.LastErrorCode);
        Assert.Equal(0, h.Uploader.Initiations);
        Assert.Null(j.VideoArchivePath);
    }

    [Fact]
    public async Task UploadFailure_ResumesSession_WithoutRecreatingVideo()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Uploader.FailAfterSession = 1;
        var r1 = await h.Run();
        Assert.Equal(RunOutcome.Failed, r1.Outcome);
        Assert.NotNull(h.Secrets.Get(SecretNames.UploadSessionPrefix + job.Id)); // sessiya saqlangan
        var renders = h.Film.Renders; var edits = h.Editor.Calls;
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1);
        var r2 = await h.Run();
        Assert.Equal(RunOutcome.Completed, r2.Outcome);
        Assert.Equal(renders, h.Film.Renders);
        Assert.Equal(edits, h.Editor.Calls);
        Assert.Equal(1, h.Uploader.Initiations); // yangi upload boshlanmadi
        Assert.Equal(1, h.Uploader.Resumes);
    }

    [Fact]
    public async Task AmbiguousUpload_IsNotBlindlyRepeated()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Uploader.FailAfterSession = 1; h.Uploader.LoseSession = true;
        await h.Run();
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1);
        var r = await h.Run();
        Assert.Equal(RunOutcome.NeedsAttention, r.Outcome);
        Assert.Equal(1, h.Uploader.Initiations);
        Assert.Equal(JobStatus.NeedsAttention, h.Store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task PublishTimePassed_IsMovedToNowPlus35Minutes_AndUploaded()
    {
        using var h = new Harness();
        var job = h.AddJob(publishInDays: 0);
        var j0 = h.Store.Get(job.Id)!; j0.PublishAtUtc = h.Clock.UtcNow.AddHours(-2); h.Store.Update(j0); // muddat o'tib ketgan
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(h.Clock.UtcNow.AddMinutes(JobRunner.LatePublishDelayMinutes), j.PublishAtUtc);
        Assert.Equal(1, h.Uploader.Initiations);
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Contains(h.Log.Lines, l => l.Contains("hozir + 60 daqiqa"));
    }

    [Fact]
    public async Task RetryLimit_MovesToNeedsAttention_AndQueueContinues()
    {
        using var h = new Harness();
        h.Settings.S.Limits.MaxRunAttempts = 2;
        var bad = h.AddJob("bad", 30);
        var good = h.AddJob("good", 10);
        h.Editor.TransientFailures = 2;
        await h.Run();
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1);
        var r2 = await h.Run();
        Assert.Equal(RunOutcome.NeedsAttention, r2.Outcome);
        Assert.Equal(JobStatus.NeedsAttention, h.Store.Get(bad.Id)!.Status);
        var r3 = await h.Run();
        Assert.Equal(good.Id, r3.JobId);
        Assert.Equal(RunOutcome.Completed, r3.Outcome);
    }

    [Fact]
    public async Task SettingsSnapshot_ProtectsRunningJob()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Editor.TransientFailures = 1;
        await h.Run();
        h.Settings.S.Claude.MontagePrompt = "PROMPT-V2-CHANGED";
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1);
        await h.Runner(stopBeforeUpload: true).RunOnceAsync("run-2", CancellationToken.None); // upload'gacha: records hali tozalanmagan
        var j = h.Store.Get(job.Id)!;
        Assert.Contains("PROMPT-V1", j.SettingsSnapshotJson);
        Assert.DoesNotContain("PROMPT-V2", j.SettingsSnapshotJson);
        Assert.Contains("PROMPT-V1", File.ReadAllText(Path.Combine(h.Paths.RecordDirFor(job.Id), "instructions.md")));
    }

    [Fact]
    public async Task CancelDuringRun_StopsAndMarksCancelled()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Editor.DuringRun = async () => { h.Store.RequestCancel(job.Id); await Task.Delay(TimeSpan.FromSeconds(5)); };
        var runTask = h.Run();
        var r = await runTask;
        // Fake editor token'ni kuzatmaydi; keyingi bosqich boshlanishida bekor qilish ishlaydi.
        Assert.Equal(RunOutcome.Cancelled, r.Outcome);
        Assert.Equal(JobStatus.Cancelled, h.Store.Get(job.Id)!.Status);
        Assert.Equal(0, h.Uploader.Initiations);
    }

    [Fact]
    public async Task ConfigError_FailsFast_WithoutCallingGemini()
    {
        using var h = new Harness();
        h.Settings.S.Claude.ExecutablePath = @"C:\nope\claude.exe";
        var job = h.AddJob();
        var r = await h.Run();
        Assert.Equal(RunOutcome.NeedsAttention, r.Outcome);
        Assert.Equal(0, h.Script.Calls);
        Assert.Contains("Claude Code topilmadi", h.Store.Get(job.Id)!.LastErrorMessage);
    }
}
