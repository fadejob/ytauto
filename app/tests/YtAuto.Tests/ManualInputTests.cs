using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Tests;

/// <summary>Botdan "yuklab olingan" ovozni sinovda sintetik WAV bilan almashtiradi.</summary>
internal sealed class FakeAudioImporter : IAudioImporter
{
    public int Calls;
    public string? LastUrl;
    public Task ImportAsync(string url, string outWav, IJobLog log, CancellationToken ct)
    {
        Calls++; LastUrl = url;
        FakeTts.WriteSpeechLikeWav(outWav, TestScript.Narration.Length / 15.0);
        return Task.CompletedTask;
    }
}

/// <summary>Botda qo'lda kiritilgan subtitr/title (Gemini o'tkazib yuboriladi) va tayyor ovoz (VoiceStudio o'tkazib yuboriladi).</summary>
public class ManualInputTests
{
    private static VideoJob AddManual(Harness h, bool withAudio)
    {
        var j = new VideoJob
        {
            Topic = "Boiling water", Language = "en", VoiceProfileName = "v", Format = VideoFormat.Landscape16x9,
            PublishAtUtc = h.Clock.UtcNow.AddDays(3), Status = JobStatus.Queued, CreatedAtUtc = h.Clock.UtcNow.AddMinutes(-10),
            ManualScript = TestScript.Narration, ManualTitle = "My own title",
            ManualAudioUrl = withAudio ? "https://x.supabase.co/storage/v1/object/sign/templates/audio/a/voice.mp3?token=t1" : null,
        };
        h.Store.Insert(j);
        return j;
    }

    [Fact]
    public async Task ManualScript_SkipsGemini_UsesGivenTitle_StillMakesVoice()
    {
        using var h = new Harness();
        var job = AddManual(h, withAudio: false);
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Equal(0, h.Script.Calls);
        Assert.Equal("My own title", j.Title);
        Assert.True(h.Tts.Synth > 0);
    }

    [Fact]
    public async Task ManualAudio_SkipsGeminiAndVoiceStudio()
    {
        using var h = new Harness();
        var importer = new FakeAudioImporter();
        h.AudioImporter = importer;
        h.Settings.S.VoiceStudio.ExePath = Path.Combine(h.Tmp.Path, "missing.exe"); // VoiceStudio kerak emas
        var job = AddManual(h, withAudio: true);
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Equal(0, h.Script.Calls);
        Assert.Equal(0, h.Tts.Opens);
        Assert.Equal(1, importer.Calls);
        Assert.Equal(1, h.Aligner.Calls); // subtitr vaqtlari baribir ovozdan olinadi
        Assert.Equal("My own title", j.Title);
        Assert.True(File.Exists(j.AudioArchivePath));
    }

    [Fact]
    public async Task ManualAudio_WithoutImporter_NeedsAttention()
    {
        using var h = new Harness();
        var job = AddManual(h, withAudio: true);
        var r = await h.Run();
        Assert.Equal(RunOutcome.NeedsAttention, r.Outcome);
        Assert.Equal(0, h.Script.Calls);
        Assert.Equal(0, h.Tts.Opens);
    }
}
