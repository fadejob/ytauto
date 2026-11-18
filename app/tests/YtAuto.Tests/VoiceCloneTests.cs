using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

/// <summary>Klon profil chaqiruvlarini yozib oladi; sintez FakeTts kabi sintetik WAV.</summary>
internal sealed class CloneTts : ITtsService
{
    public readonly List<(string Name, string Wav, string Version)> Ensured = new();
    public readonly List<string> Voices = new();
    public Exception? EnsureError;
    public Task<ITtsSession> OpenAsync(VoiceStudioSettings s, IJobLog log, CancellationToken ct) => Task.FromResult<ITtsSession>(new S(this));
    private sealed class S : ITtsSession
    {
        private readonly CloneTts _o; public S(CloneTts o) => _o = o;
        public bool StartedByUs => true; public string Version => "fake"; public Dictionary<string, double> Metrics { get; } = new();
        public Task EnsureModelAvailableAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public Task SynthesizeAsync(string text, VoiceProfile v, string outWav, CancellationToken ct) { _o.Voices.Add(v.Voice); FakeTts.WriteSpeechLikeWav(outWav, text.Length / 15.0); return Task.CompletedTask; }
        public Task EnsureCloneProfileAsync(string name, string refWav, string version, CancellationToken ct)
        {
            if (_o.EnsureError is not null) throw _o.EnsureError;
            Assert.True(File.Exists(refWav));
            _o.Ensured.Add((name, refWav, version));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>«Mening ovozim»: har hisob o'z namunasi bilan, model bitta.</summary>
public class VoiceCloneTests
{
    private const string Profile = "YtAuto u-abcd1234";

    private static (Harness h, CloneTts tts, FakeAudioImporter imp, List<(string V, string S, string M)> reports) Setup(string version = "v1")
    {
        var h = new Harness();
        var tts = new CloneTts(); var imp = new FakeAudioImporter(); var reports = new List<(string, string, string)>();
        h.TtsOverride = tts; h.AudioImporter = imp; h.OnVoiceCloneStatus = (v, s, m) => reports.Add((v, s, m));
        h.Settings.S.VoiceProfiles.Add(new VoiceProfile { Name = "Mening ovozim", Model = TtsVoices.Model, Voice = Profile });
        h.Settings.S.VoiceClone = new VoiceCloneSettings { ProfileName = Profile, Url = "https://x.supabase.co/sample.webm?token=1", Version = version };
        return (h, tts, imp, reports);
    }

    private static VideoJob AddCloneJob(Harness h)
    {
        var j = h.AddJob();
        j.VoiceProfileName = "Mening ovozim";
        h.Store.Update(j);
        return j;
    }

    [Fact]
    public void ReadVoiceClone_ActiveAndDeleted()
    {
        var a = SqliteSettingsStore.ReadVoiceClone("""{"version":"v1","profileName":"YtAuto u-abcd1234","url":"https://x.supabase.co/s.wav","status":"pending"}""");
        Assert.True(a.Active);
        Assert.True(a.Matches(new VoiceProfile { Voice = "ytauto U-ABCD1234" }));
        var d = SqliteSettingsStore.ReadVoiceClone("""{"deleted":true,"profileName":"YtAuto u-abcd1234","deletedAt":"x"}""");
        Assert.True(d.Deleted);
        Assert.False(d.Active);
        Assert.False(SqliteSettingsStore.ReadVoiceClone(null).Active);
    }

    [Fact]
    public async Task CloneJob_DownloadsSample_RegistersProfile_AndReportsReady()
    {
        var (h, tts, imp, reports) = Setup();
        using var _ = h;
        AddCloneJob(h);
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(1, imp.Calls);
        var e = Assert.Single(tts.Ensured);
        Assert.Equal((Profile, "v1"), (e.Name, e.Version));
        Assert.All(tts.Voices, v => Assert.Equal(Profile, v)); // har bo'lak shu foydalanuvchi ovozida
        Assert.Contains(reports, x => x.V == "v1" && x.S == "ready");
    }

    [Fact]
    public async Task SampleCachedPerVersion_OldVersionRemoved()
    {
        var (h, tts, imp, _) = Setup("v1");
        using var _h = h;
        AddCloneJob(h);
        await h.Run();
        var first = VoiceCloneCache.WavPath(h.Paths, h.Settings.S.VoiceClone);
        Assert.True(File.Exists(first));

        AddCloneJob(h);
        await h.Run(); // shu versiya: qayta yuklanmaydi
        Assert.Equal(1, imp.Calls);

        h.Settings.S.VoiceClone.Version = "v2"; // foydalanuvchi yangi namuna yukladi
        AddCloneJob(h);
        await h.Run();
        Assert.Equal(2, imp.Calls);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(VoiceCloneCache.WavPath(h.Paths, h.Settings.S.VoiceClone)));
        Assert.Equal("v2", tts.Ensured[^1].Version);
    }

    [Fact]
    public async Task OtherVoices_DoNotTouchClone()
    {
        var (h, tts, imp, reports) = Setup();
        using var _ = h;
        h.AddJob(); // standart "v" profili
        await h.Run();
        Assert.Empty(tts.Ensured);
        Assert.Equal(0, imp.Calls);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task RegisterFailure_StopsJob_AndReportsError()
    {
        var (h, tts, _, reports) = Setup();
        using var _h = h;
        tts.EnsureError = PipelineException.Config("voice_clone_rejected", "VoiceStudio ovoz namunasini qabul qilmadi (422)");
        var job = AddCloneJob(h);
        var r = await h.Run();
        Assert.NotEqual(RunOutcome.Completed, r.Outcome);
        Assert.Empty(tts.Voices); // boshqa ovozda jimgina o'qilmaydi
        Assert.Contains(reports, x => x.S == "error" && x.M.Contains("422"));
        Assert.Equal(JobStatus.NeedsAttention, h.Store.Get(job.Id)!.Status);
    }
}
