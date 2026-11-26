using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Media;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Qa;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Gemini;

namespace YtAuto.Tests;

/// <summary>
/// Video boshlanishi (foydalanuvchi kiritgan savol - hook) va format bo'yicha uzunlik: 16:9 - 2-3 daqiqa, Shorts/kvadrat - 55 soniyagacha.
/// </summary>
public class HookLengthTests
{
    private const string Hook = "Why do elephants never forget?";

    private static VideoScript Script(string text) => new() { NarrationText = text, SubtitleText = text, YouTubeTitle = "Elephants", YouTubeDescription = "d" };
    private static string Words(int n, string w = "elephant") => string.Join(" ", Enumerable.Repeat(w, n)) + ".";

    [Fact]
    public void Lengths_MatchFormatRules()
    {
        Assert.Equal((120, 180), (VideoLength.Landscape.MinSec, VideoLength.Landscape.MaxSec));
        Assert.Equal(55, VideoLength.Short.MaxSec);
        Assert.Same(VideoLength.Short, VideoLength.For(VideoFormat.Shorts9x16));
        Assert.Same(VideoLength.Short, VideoLength.For(VideoFormat.Square1x1));
        Assert.Same(VideoLength.Landscape, VideoLength.For(VideoFormat.Landscape16x9));
        // TTS ~2 so'z/soniya: Shorts ovozi 52s -> 104 so'z; 16:9 ideal 150s -> 300 so'z.
        Assert.Equal(104, VideoLength.Short.MaxWords("en"));
        Assert.Equal(300, VideoLength.Landscape.TargetWords("en"));
        Assert.True(VideoLength.Short.MaxAudioSec < VideoLength.Short.MaxSec); // yakun uchun joy
    }

    [Fact]
    public void HookRule_IgnoresCaseAndPunctuation_PrependsOnlyWhenMissing()
    {
        Assert.True(HookRule.StartsWithHook("why do Elephants never forget... Because memory matters.", Hook));
        Assert.False(HookRule.StartsWithHook("Elephants never forget. Why?", Hook));
        var s = Script("Their brains are huge.");
        Assert.True(HookRule.Prepend(s, "  Why do   elephants never forget? "));
        Assert.Equal(Hook + " Their brains are huge.", s.SubtitleText);
        Assert.Equal(s.SubtitleText, s.NarrationText);
        Assert.False(HookRule.Prepend(s, Hook)); // ikkinchi marta qo'yilmaydi
        Assert.False(HookRule.Prepend(Script("x"), ""));
    }

    [Fact]
    public void HookEnd_FromWordTimings()
    {
        var cues = new List<SubtitleCue>
        {
            new() { Start = 0, End = 2.5, Text = "Why do elephants", Words = { new() { Word = "Why", End = 0.4 }, new() { Word = "do", End = 0.7 }, new() { Word = "elephants", End = 1.4 } } },
            new() { Start = 2.5, End = 4, Text = "never forget? They", Words = { new() { Word = "never", End = 2.9 }, new() { Word = "forget?", End = 3.6 }, new() { Word = "They", End = 3.9 } } },
        };
        Assert.Equal(3.6, HookRule.EndSec(cues, Hook));
        Assert.Null(HookRule.EndSec(cues, ""));
    }

    [Fact]
    public void Validator_EnforcesFormatWindow_AndHookStart()
    {
        var w = VideoLength.Short;
        Assert.Empty(ScriptValidator.Validate(Script(Hook + " " + Words(80)), "en", 0, w, Hook));
        Assert.Contains(ScriptValidator.Validate(Script(Hook + " " + Words(200)), "en", 0, w, Hook), p => p.Contains("formatga mos emas"));
        Assert.Contains(ScriptValidator.Validate(Script(Hook + " " + Words(20)), "en", 0, w, Hook), p => p.Contains("formatga mos emas"));
        Assert.Contains(ScriptValidator.Validate(Script(Words(80) + " " + Hook), "en", 0, w, Hook), p => p.Contains("hook"));
        // 16:9: 2-3 daqiqa - 100 so'z kam, 300 so'z mos.
        Assert.NotEmpty(ScriptValidator.Validate(Script(Words(100)), "en", 0, VideoLength.Landscape, null));
        Assert.Empty(ScriptValidator.Validate(Script(Words(300)), "en", 0, VideoLength.Landscape, null));
    }

    [Fact]
    public void Prompt_HasHookBlockAndWindow_FactSheetPromptDoesNot()
    {
        var r = new ScriptRequest { Topic = "Elephants", Language = "en", Format = "vertical 9:16 short", Hook = Hook, Length = VideoLength.Short };
        var p = GeminiScriptGenerator.FillScript(DefaultPrompts.Script, r, "");
        Assert.Contains("OPENING HOOK", p);
        Assert.Contains("\"" + Hook + "\"", p);
        Assert.Contains("Never exceed 104 words", p);
        Assert.DoesNotContain("no fixed duration", p);
        // Shablonda {length_rule} bo'lmasa ham qoida qo'shiladi.
        Assert.Contains("Never exceed 104 words", GeminiScriptGenerator.FillScript("Topic: {topic}", r, ""));
        var fact = GeminiScriptGenerator.Fill(DefaultPrompts.FactSheet, r, "");
        Assert.DoesNotContain("OPENING HOOK", fact);
    }

    [Fact]
    public void Qa_VideoLongerThanFormatLimit_IsFixableError()
    {
        var r = new QaReport();
        QualityRules.CheckRenderedStreams(56.2, 52, true, true, 1080, 1920, VideoFormat.Shorts9x16, r, 55);
        Assert.Contains(r.Errors, e => e.Code == "video_too_long");
        var ok = new QaReport();
        QualityRules.CheckRenderedStreams(54.8, 52, true, true, 1080, 1920, VideoFormat.Shorts9x16, ok, 55);
        Assert.True(ok.Passed);
    }

    // ------------------------------------------------------------------ pipeline

    /// <summary>Gemini kabi: har safar berilgan matnni qaytaradi (hook'siz) - ishchi uni hook bilan boshlashi kerak.</summary>
    private sealed class BodyScript(string body) : IScriptGenerator
    {
        public int Calls; public ScriptRequest? Last;
        public Task<ScriptResult> GenerateAsync(ScriptRequest r, IJobLog log, CancellationToken ct)
        {
            Calls++; Last = r;
            var s = Script(body); s.Tags = new() { "a", "b", "c", "d", "e" };
            return Task.FromResult(new ScriptResult { Script = s, RawModelOutput = s.ToJson(), ModelUsed = "fake" });
        }
        public Task<MetadataResult> DescribeAsync(MetadataRequest r, IJobLog log, CancellationToken ct) => Task.FromResult(new MetadataResult());
    }

    /// <summary>Ovozdagi so'zlar - berilgan matn so'zlari, teng taqsimlangan.</summary>
    private sealed class TextAligner(Func<string> text) : IAligner
    {
        public Dictionary<string, double> LastMetrics { get; } = new();
        public string Describe() => "text";
        public Task<IReadOnlyList<WordTiming>> TranscribeWordsAsync(string wav, string lang, string outJson, IJobLog log, CancellationToken ct)
        {
            var d = Wav.ReadInfo(wav).Duration; var words = TextNormalizer.Tokenize(text());
            var step = d / words.Count;
            File.WriteAllText(outJson, "{}");
            return Task.FromResult<IReadOnlyList<WordTiming>>(words.Select((w, i) => new WordTiming { Word = " " + w.Word, Start = i * step, End = i * step + step * 0.8 }).ToList());
        }
    }

    private static (Harness H, BodyScript G, VideoJob J) ShortsJob(string body)
    {
        var h = new Harness();
        var g = new BodyScript(body);
        h.ScriptOverride = g;
        h.AlignerOverride = new TextAligner(() => Hook + " " + body);
        h.Media.RenderSize = (1080, 1920);
        // Botdagi oddiy mavzu kabi: davomiylik berilmagan (0), Shorts, hook bilan.
        var j = new VideoJob
        {
            Topic = "Elephant memory", Language = "en", TargetDurationSec = 0, VoiceProfileName = "v", Format = VideoFormat.Shorts9x16, Hook = Hook,
            PublishAtUtc = h.Clock.UtcNow.AddDays(3), Status = JobStatus.Queued, CreatedAtUtc = h.Clock.UtcNow.AddMinutes(-10),
        };
        h.Store.Insert(j);
        return (h, g, j);
    }

    [Fact]
    public async Task Shorts_StartsWithUserHook_VoiceSqueezedUnder55s_EditorGetsHookAndLimit()
    {
        var (h, g, j) = ShortsJob(Words(95));
        using var _ = h;
        // Upload'dan oldin to'xtaydi: vazifa yozuvlari (script.json) tozalanmaydi.
        await h.Runner(stopBeforeUpload: true).RunOnceAsync("r1", CancellationToken.None);
        var job = h.Store.Get(j.Id)!;
        Assert.True(job.Status == JobStatus.Archived, $"{job.Status}: {job.LastErrorMessage}");
        // Gemini'ga hook va Shorts oralig'i berildi; ikki marta hook'siz yozgach, oxirgi urinishda hook boshiga qo'yildi.
        Assert.Equal(Hook, g.Last!.Hook);
        Assert.Same(VideoLength.Short, g.Last.Length);
        Assert.Equal(3, g.Calls);
        var saved = VideoScript.FromJson(File.ReadAllText(Path.Combine(h.Paths.RecordDirFor(j.Id), "script.json")));
        Assert.StartsWith(Hook + " ", saved.SubtitleText);
        // ~60s ovoz -> 52s gacha tezlashtirildi (ovoz balandligi o'zgarmaydi).
        Assert.Single(h.Media.Tempos);
        Assert.InRange(job.AudioDurationSec!.Value, 45, VideoLength.Short.MaxAudioSec);
        Assert.Contains("Opening hook", h.Editor.LastInstructions);
        Assert.Contains(Hook, h.Editor.LastInstructions);
        Assert.Contains("no later than 55 s", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task Shorts_VoiceFarTooLong_StopsWithClearError_NotA70sShort()
    {
        var (h, _, j) = ShortsJob(Words(100, "extraordinarily"));
        using var _ = h;
        await h.Run();
        var job = h.Store.Get(j.Id)!;
        Assert.Equal(JobStatus.NeedsAttention, job.Status);
        Assert.Equal("voice_too_long", job.LastErrorCode);
        Assert.Empty(h.Media.Tempos);
        Assert.Equal(0, h.Editor.Calls);
    }
}
