using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Gemini;

namespace YtAuto.Tests;

/// <summary>Oqim: Gemini JSON (subtitr + title) → ovoz subtitrdan → Claude'ga subtitr + shablon + ovoz + prompt; title YouTube'gacha saqlanadi.</summary>
public class FlowTests
{
    private sealed class SubtitleOnlyScript : IScriptGenerator
    {
        public Task<ScriptResult> GenerateAsync(ScriptRequest r, IJobLog log, CancellationToken ct)
        {
            var s = new VideoScript { SubtitleText = TestScript.Narration, YouTubeTitle = "Suv nega qaynaydi" }; // sahnalar, tavsif yo'q
            return Task.FromResult(new ScriptResult { Script = s, RawModelOutput = s.ToJson(), ModelUsed = "fake" });
        }
    }

    private sealed class RecordingTts : ITtsService
    {
        public readonly List<string> Texts = new();
        public Task<ITtsSession> OpenAsync(VoiceStudioSettings s, IJobLog log, CancellationToken ct) => Task.FromResult<ITtsSession>(new S(this));
        private sealed class S : ITtsSession
        {
            private readonly RecordingTts _o; public S(RecordingTts o) => _o = o;
            public bool StartedByUs => true; public string Version => "fake"; public Dictionary<string, double> Metrics { get; } = new();
            public Task EnsureModelAvailableAsync(string m, CancellationToken ct) => Task.CompletedTask;
            public Task SynthesizeAsync(string text, VoiceProfile v, string outWav, CancellationToken ct) { _o.Texts.Add(text); FakeTts.WriteSpeechLikeWav(outWav, text.Length / 15.0); return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void GeminiSchemaRequiresOnlySubtitleAndTitle()
    {
        var schema = GeminiScriptGenerator.ScriptSchema();
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "subtitle_text", "youtube_title" }, required);
        Assert.Null(schema["properties"]!["scenes"]);
    }

    [Fact]
    public void OldNarrationOnlyAnswerBecomesSubtitle()
    {
        var s = new VideoScript { NarrationText = "Old format text here.", YouTubeTitle = "t" };
        ScriptValidator.Canonicalize(s);
        Assert.Equal("Old format text here.", s.SubtitleText);
        Assert.Equal(s.SubtitleText, s.NarrationText);
    }

    [Fact]
    public async Task VoiceFromSubtitle_ClaudeGetsSubtitleVoicePrompt_TitleUsedForYouTube()
    {
        using var h = new Harness();
        var tts = new RecordingTts();
        var runner = new JobRunner(new PipelineServices
        {
            Store = h.Store, Settings = h.Settings, Secrets = h.Secrets, ScriptGenerator = new SubtitleOnlyScript(), Tts = tts, Aligner = h.Aligner,
            Editor = h.Editor, Film = h.Film, Media = new FakeMedia(), Uploader = h.Uploader, Resources = new FakeResources(), Paths = h.Paths,
            Clock = h.Clock, JobLogFactory = _ => h.Log, RunLog = h.Log, PostUploadPoll = TimeSpan.Zero, StopBeforeUpload = true,
        });
        var job = h.AddJob();
        var r = await runner.RunOnceAsync("flow", CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedBeforeUpload, r.Outcome);

        // Ovoz aynan subtitr matnidan
        Assert.Equal(TextNormalizer.NormalizedWords(TestScript.Narration), TextNormalizer.NormalizedWords(string.Join(" ", tts.Texts)));
        // Title saqlangan (YouTube'ga chiqarishda ishlatiladi)
        Assert.Equal("Suv nega qaynaydi", h.Store.Get(job.Id)!.Title);
        // Claude'ga: prompt + subtitr + ovoz; sahnalar jadvali yo'q
        var instr = File.ReadAllText(Path.Combine(h.Paths.RecordDirFor(job.Id), "instructions.md"));
        Assert.Contains("PROMPT-V1", instr);
        Assert.Contains("subtitles.txt", instr);
        Assert.Contains("voice.wav", instr);
        Assert.Contains("Water boils", instr);
        Assert.DoesNotContain("Scene plan", instr);
        Assert.Contains("Template: none", instr);
    }

    /// <summary>UI'dagi "O'zgarishlarni saqlash" bilan bir xil amallar: maydonlar, snapshot reset, navbatga qaytarish.</summary>
    private static void EditJob(Harness h, long id, Action<VideoJob> change)
    {
        var j = h.Store.Get(id)!;
        change(j);
        j.SettingsSnapshotJson = null;
        h.Store.Update(j);
        h.Store.Requeue(id, resetAttempts: true);
    }

    [Fact]
    public async Task EditingTopicOfUnpublishedVideo_RebuildsFromScript_EditingFormatOnlyFromMontage()
    {
        using var h = new Harness();
        var job = h.AddJob();
        await h.Runner(stopBeforeUpload: true).RunOnceAsync("r1", CancellationToken.None);
        Assert.Equal((1, 1, 1), (h.Script.Calls, h.Aligner.Calls, h.Editor.Calls));
        var synth = h.Tts.Synth;

        // Faqat format o'zgardi: ssenariy va ovoz qayta qilinmaydi; subtitr (qator uzunligi formatga bog'liq) va montaj qayta.
        EditJob(h, job.Id, j => j.Format = VideoFormat.Square1x1);
        await h.Runner(stopBeforeUpload: true).RunOnceAsync("r2", CancellationToken.None);
        Assert.Equal(1, h.Script.Calls);
        Assert.Equal(synth, h.Tts.Synth);
        Assert.Equal(2, h.Editor.Calls);

        // Mavzu o'zgardi: ssenariydan boshlab hammasi qayta.
        EditJob(h, job.Id, j => { j.Topic = "Yangi mavzu"; j.Title = null; });
        var r = await h.Runner(stopBeforeUpload: true).RunOnceAsync("r3", CancellationToken.None);
        Assert.Equal(RunOutcome.StoppedBeforeUpload, r.Outcome);
        Assert.Equal(2, h.Script.Calls);
        Assert.Equal(3, h.Editor.Calls);
        Assert.Equal("Yangi mavzu", h.Store.Get(job.Id)!.Topic);
        Assert.Equal(0, h.Uploader.Initiations);
    }

    [Fact]
    public void LegacyDefaultPromptsAreMigrated_CustomPromptsKept()
    {
        using var t = new TempDir();
        var db = new SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        var store = new SqliteSettingsStore(db, () => new AppSettings());
        var s = new AppSettings();
        s.Gemini.ScriptPrompt = DefaultPrompts.ScriptPrevious.Replace("{length_rule}", DefaultPrompts.LegacyLengthLine);
        s.Claude.MontagePrompt = DefaultPrompts.MontagePrevious;
        store.Save(s);
        var loaded = store.Load();
        Assert.Equal(DefaultPrompts.Script, loaded.Gemini.ScriptPrompt);
        Assert.Equal(DefaultPrompts.Montage, loaded.Claude.MontagePrompt);

        s.Gemini.ScriptPrompt = "Mening o'z promptim {topic}";
        store.Save(s);
        Assert.Equal("Mening o'z promptim {topic}", store.Load().Gemini.ScriptPrompt);
    }
}
