using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Qa;
using YtAuto.Core.Scheduling;
using YtAuto.Core.Script;

namespace YtAuto.Tests;

public class StateMachineTests
{
    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Scripting, true)]
    [InlineData(JobStatus.Failed, JobStatus.Rendering, true)]
    [InlineData(JobStatus.Archived, JobStatus.Uploading, true)]
    [InlineData(JobStatus.Uploading, JobStatus.Processing, true)]
    [InlineData(JobStatus.Processing, JobStatus.Scheduled, true)]
    [InlineData(JobStatus.Scheduled, JobStatus.Published, true)]
    [InlineData(JobStatus.Published, JobStatus.Queued, false)]
    [InlineData(JobStatus.Cancelled, JobStatus.Queued, false)]
    [InlineData(JobStatus.Queued, JobStatus.Published, false)]
    [InlineData(JobStatus.NeedsAttention, JobStatus.Queued, true)]
    [InlineData(JobStatus.Scheduled, JobStatus.Scripting, false)]
    public void Transitions(JobStatus from, JobStatus to, bool ok) => Assert.Equal(ok, JobStateMachine.CanTransition(from, to));

    [Fact]
    public void NeedsAttentionAndUploadedAreNotClaimable()
    {
        Assert.DoesNotContain(JobStatus.NeedsAttention, JobStateMachine.ClaimableStatuses);
        Assert.DoesNotContain(JobStatus.Processing, JobStateMachine.ClaimableStatuses);
        Assert.DoesNotContain(JobStatus.Scheduled, JobStateMachine.ClaimableStatuses);
        Assert.DoesNotContain(JobStatus.Cancelled, JobStateMachine.ClaimableStatuses);
    }
}

public class PublishDateTests
{
    private static readonly TimeZoneInfo Tz = TimeZones.Find("Asia/Tashkent");
    private static DateTimeOffset At(int y, int m, int d, int h = 18) => TimeZones.ToUtc(new DateOnly(y, m, d), new TimeOnly(h, 0), Tz);
    private static readonly DateTimeOffset Now = At(2026, 11, 14, 12);

    [Fact] public void NoPrevious_Today() => Assert.Equal(new DateOnly(2026, 11, 14), PublishDateSuggester.SuggestDate(Array.Empty<(JobStatus, DateTimeOffset)>(), Now, Tz));

    [Fact]
    public void LastPlusTwoDays_IncludesQueued_IgnoresCancelled()
    {
        var slots = new[] { (JobStatus.Scheduled, At(2026, 11, 16)), (JobStatus.Queued, At(2026, 11, 18)), (JobStatus.Cancelled, At(2026, 11, 30)) };
        Assert.Equal(new DateOnly(2026, 11, 20), PublishDateSuggester.SuggestDate(slots, Now, Tz));
    }

    [Fact]
    public void PastResult_Today()
    {
        var slots = new[] { (JobStatus.Published, At(2026, 10, 1)) };
        Assert.Equal(new DateOnly(2026, 11, 14), PublishDateSuggester.SuggestDate(slots, Now, Tz));
    }

    [Fact]
    public void UsesConfiguredTimeAndZone()
    {
        var utc = PublishDateSuggester.SuggestUtc(Array.Empty<(JobStatus, DateTimeOffset)>(), Now, new YouTubeSettings { PublishTime = "18:00", TimeZoneId = "Asia/Tashkent" });
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.Zero), utc); // 18:00 +05:00
    }
}

public class AlignmentTests
{
    [Fact]
    public void MapsAsrTimesOntoOriginalWords_AndInterpolatesMismatches()
    {
        var text = "Hello world, this is a test of 2 voices.";
        var asr = new List<WordTiming>
        {
            new() { Word = " Hello", Start = 0.0, End = 0.4 }, new() { Word = " world,", Start = 0.45, End = 0.9 },
            new() { Word = " this", Start = 1.2, End = 1.4 }, new() { Word = " is", Start = 1.45, End = 1.55 },
            new() { Word = " a", Start = 1.6, End = 1.65 }, new() { Word = " test", Start = 1.7, End = 2.0 },
            new() { Word = " of", Start = 2.05, End = 2.15 }, new() { Word = " two", Start = 2.2, End = 2.5 },
            new() { Word = " voices.", Start = 2.55, End = 3.1 },
        };
        var r = TranscriptAligner.Align(text, asr, 3.3);
        Assert.Equal(9, r.Words.Count);
        Assert.Equal("Hello", r.Words[0].Word);
        Assert.Equal(1.2, r.Words[2].Start, 3);
        Assert.False(r.Words[7].Matched); // "2" vs "two" - yashirilmaydi
        Assert.Contains(r.Mismatches, m => m.Contains("\"2\""));
        Assert.InRange(r.Words[7].Start, 2.15, 2.55); // qo'shnilar orasida interpolyatsiya
        for (int i = 1; i < r.Words.Count; i++) Assert.True(r.Words[i].Start >= r.Words[i - 1].Start);
    }

    [Fact]
    public void SubtitleCuesRespectMaxCharsAndUseOriginalText()
    {
        var text = "Every night at one o'clock, a small program wakes up on this computer. First, it takes the oldest topic from the queue.";
        var tokens = TextNormalizer.Tokenize(text);
        var words = tokens.Select((t, i) => new AlignedWord { Word = t.Word, CharStart = t.Start, CharEnd = t.Start + t.Length, Start = i * 0.3, End = i * 0.3 + 0.25, Matched = true }).ToList();
        var cues = SubtitleBuilder.BuildCues(text, words, 42);
        Assert.All(cues, c => Assert.True(c.Text.Length <= 50, c.Text));
        Assert.Equal(text.Replace(" ", ""), string.Concat(cues.Select(c => c.Text)).Replace(" ", ""));
        var vtt = SubtitleBuilder.ToVtt(cues);
        var parsed = VttParser.Parse(vtt);
        Assert.Equal(cues.Count, parsed.Count);
        Assert.Equal(cues[0].Text, parsed[0].Text); // so'z teglari olib tashlangach asl matn
        Assert.Contains("<00:00:00.300>", vtt);
    }
}

public class ScriptValidatorTests
{
    private static VideoScript Good(int words)
    {
        var narration = string.Join(" ", Enumerable.Range(0, words).Select(i => "word")) + ".";
        var half = words / 2;
        return new VideoScript
        {
            NarrationText = narration, YouTubeTitle = "A title", YouTubeDescription = "Desc",
            Scenes = new()
            {
                new() { Index = 1, Narration = string.Join(" ", Enumerable.Repeat("word", half)), Visual = "v", OnScreenText = "two words" },
                new() { Index = 2, Narration = string.Join(" ", Enumerable.Repeat("word", words - half)) + ".", Visual = "v", OnScreenText = "x" },
            },
        };
    }

    [Fact] public void AcceptsGoodScript() => Assert.Empty(ScriptValidator.Validate(Good(150), "en", 60));

    [Fact]
    public void RejectsStageDirections()
    {
        var s = Good(150); s.NarrationText = "[Music] " + s.NarrationText + " (pause) Scene 2: hello";
        var p = ScriptValidator.Validate(s, "en", 60);
        Assert.True(p.Count(x => x.Contains("texnik ko'rsatma")) >= 2);
    }

    [Fact] public void RejectsLengthMismatch() => Assert.Contains(ScriptValidator.Validate(Good(40), "en", 120), p => p.Contains("uzunligi"));

    [Fact]
    public void DropsSourcesNotFromGrounding()
    {
        var s = Good(150);
        s.GroundedSources = new() { new() { Url = "https://a.example/x" } };
        s.Sources = new() { new() { Url = "https://a.example/x/" }, new() { Url = "https://invented.example" } };
        ScriptValidator.SanitizeSources(s);
        Assert.Single(s.Sources);
        Assert.True(s.SourcesVerified);
    }
}

public class TimelineQaTests
{
    // Oldingi sinovdagi haqiqiy film.html: sahnalar orasida 0.7s va oxirida 0.27s bo'shliq bor edi.
    private const string PrevTestFilm = """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=1280, height=720"></head><body>
<section>
  <iframe src="scene1.html#t=0,4.46" at="0"></iframe>
  <iframe src="scene2.html#t=0,5.2" at="5.16"></iframe>
  <iframe src="scene3.html#t=0,3.64" at="10.36"></iframe>
</section>
<section><audio src="assets/voice.wav" volume="1"></audio></section>
</body></html>
""";

    [Fact]
    public void DetectsGapsAndEarlyEnd_FromPreviousTest()
    {
        var tl = FilmTimelineParser.Parse(PrevTestFilm, src => src.EndsWith(".wav") ? 14.29 : null);
        var r = new QaReport();
        QualityRules.CheckTimeline(tl, 14.29, r, VideoFormat.Landscape16x9);
        Assert.Contains(r.Errors, e => e.Code == "gap" && Math.Abs(e.AtSec!.Value - 4.46) < 0.01);
        Assert.Single(r.Errors, e => e.Code == "gap"); // 10.36 da sahnalar uzluksiz
        Assert.Contains(r.Errors, e => e.Code == "ends_early" && Math.Abs(e.AtSec!.Value - 14.0) < 0.01); // 0.29s qora dum
        Assert.False(r.Passed);
    }

    [Fact]
    public void ContinuousTimelinePasses()
    {
        var html = """
<meta name="viewport" content="width=1920, height=1080">
<section>
 <iframe src="s1.html#t=0,5" at="0"></iframe><iframe src="s2.html#t=0,5.5" at="5"></iframe><iframe src="s3.html#t=0,4" at="10.5"></iframe>
</section>
<section><audio src="assets/bed.mp3#t=0,14" volume="0.15"></audio></section>
<section><audio src="assets/voice.wav#t=0,14.29" at="0"></audio></section>
""";
        var r = new QaReport();
        QualityRules.CheckTimeline(FilmTimelineParser.Parse(html), 14.29, r, VideoFormat.Landscape16x9);
        Assert.True(r.Passed, string.Join("\n", r.Issues));
    }

    [Fact]
    public void LoudMusicAndWrongAspectAreErrors()
    {
        var html = """
<meta name="viewport" content="width=1920, height=1080">
<section><iframe src="s1.html#t=0,10" at="0"></iframe></section>
<section><audio src="assets/music.mp3#t=0,10" volume="0.8"></audio><audio src="assets/voice.wav#t=0,10"></audio></section>
""";
        var r = new QaReport();
        QualityRules.CheckTimeline(FilmTimelineParser.Parse(html), 10, r, VideoFormat.Shorts9x16);
        Assert.Contains(r.Errors, e => e.Code == "music_too_loud");
        Assert.Contains(r.Errors, e => e.Code == "aspect_ratio");
    }

    [Fact]
    public void BlackInsideIntentionalPageIsInfo_BlackInGapIsError()
    {
        var tl = FilmTimelineParser.Parse("""<section><iframe src="dark.html#t=0,3" at="0"></iframe><iframe src="s2.html#t=0,3" at="4"></iframe></section>""");
        var r = new QaReport();
        QualityRules.CheckBlack(new[] { new BlackSegment { Start = 0.5, End = 2.5 }, new BlackSegment { Start = 3.05, End = 3.95 } }, tl, 7, new HashSet<string> { "dark.html" }, r);
        Assert.Contains(r.Issues, i => i.Code == "black_intentional" && i.Severity == QaSeverity.Info);
        Assert.Contains(r.Errors, i => i.Code == "black_gap");
    }

    [Fact]
    public void CutsOnEveryCueIsFlagged()
    {
        var tl = FilmTimelineParser.Parse("""<section><iframe src="a.html#t=0,2" at="0"></iframe><iframe src="b.html#t=0,2" at="2"></iframe><iframe src="c.html#t=0,2" at="4"></iframe><iframe src="d.html#t=0,2" at="6"></iframe><iframe src="e.html#t=0,2" at="8"></iframe></section>""");
        var cues = Enumerable.Range(0, 5).Select(i => new SubtitleCue { Start = i * 2, End = i * 2 + 1.9, Text = "x" }).ToList();
        var r = new QaReport();
        QualityRules.CheckCutsVsCues(tl, cues, r);
        Assert.Contains(r.Issues, i => i.Code == "cuts_follow_cues");
    }

    [Fact]
    public void StreamsAndClipping()
    {
        var r = new QaReport();
        QualityRules.CheckRenderedStreams(10.0, 14.3, true, false, 1920, 1080, VideoFormat.Landscape16x9, r);
        QualityRules.CheckAudioLevels(0.0, -14, -14, r);
        Assert.Contains(r.Errors, e => e.Code == "no_audio_stream");
        Assert.Contains(r.Errors, e => e.Code == "video_shorter_than_audio");
        Assert.Contains(r.Errors, e => e.Code == "clipping");
    }
}
