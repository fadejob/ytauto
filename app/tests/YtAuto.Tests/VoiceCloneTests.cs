using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

/// <summary>Klon profil chaqiruvlarini yozib oladi; sintez FakeTts kabi sintetik WAV.</summary>
internal sealed class CloneTts : ITtsService
{
    public readonly List<(string Name, string Wav, string RefText, string Version)> Ensured = new();
    public readonly List<string> Voices = new();
    public Exception? EnsureError;
    public Task<ITtsSession> OpenAsync(VoiceStudioSettings s, IJobLog log, CancellationToken ct) => Task.FromResult<ITtsSession>(new S(this));
    private sealed class S : ITtsSession
    {
        private readonly CloneTts _o; public S(CloneTts o) => _o = o;
        public bool StartedByUs => true; public string Version => "fake"; public Dictionary<string, double> Metrics { get; } = new();
        public Task EnsureModelAvailableAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public Task SynthesizeAsync(string text, VoiceProfile v, string outWav, CancellationToken ct) { _o.Voices.Add(v.Voice); FakeTts.WriteSpeechLikeWav(outWav, text.Length / 15.0); return Task.CompletedTask; }
        public Task EnsureCloneProfileAsync(string name, string refWav, string refText, string version, CancellationToken ct)
        {
            if (_o.EnsureError is not null) throw _o.EnsureError;
            Assert.True(File.Exists(refWav));
            _o.Ensured.Add((name, refWav, refText, version));
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
        h.Settings.S.VoiceClone = new VoiceCloneSettings { ProfileName = Profile, Url = "https://x.supabase.co/sample.webm?token=1", Version = version, RefText = "Hello! I am recording my voice." };
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
        var a = SqliteSettingsStore.ReadVoiceClone("""{"version":"v1","profileName":"YtAuto u-abcd1234","url":"https://x.supabase.co/s.wav","refText":"Hello there.","status":"pending"}""");
        Assert.True(a.Active);
        Assert.Equal("Hello there.", a.RefText);
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
        Assert.Equal((Profile, "v1", "Hello! I am recording my voice."), (e.Name, e.Version, e.RefText));
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

    // ------------------------------------------------------------------ har kanalning o'z namunasi
    private const string ChA = "YtAuto u-abcd1234-0000000a", ChB = "YtAuto u-abcd1234-0000000b";
    private const string ChannelsJson = """{"active":"UCa","channels":[{"id":"UCa","title":"A","refreshToken":"r1"},{"id":"UCb","title":"B","refreshToken":"r2"}]}""";
    private static string CloneJson(string profile, string version) =>
        $$"""{"version":"{{version}}","profileName":"{{profile}}","url":"https://x.supabase.co/{{version}}.wav","refText":"","status":"pending"}""";

    [Fact]
    public async Task ChannelClone_JobUsesSampleOfItsVoiceProfile()
    {
        var (h, tts, imp, reports) = Setup("shared1");
        using var _ = h;
        h.Settings.S.VoiceProfiles.Add(new VoiceProfile { Name = "Mening ovozim · A", Model = TtsVoices.Model, Voice = ChA });
        h.Settings.S.VoiceProfiles.Add(new VoiceProfile { Name = "Mening ovozim · B", Model = TtsVoices.Model, Voice = ChB });
        h.Settings.S.VoiceClones =
        [
            h.Settings.S.VoiceClone,
            new VoiceCloneSettings { ProfileName = ChA, Url = "https://x.supabase.co/a.wav", Version = "va" },
            new VoiceCloneSettings { ProfileName = ChB, Url = "https://x.supabase.co/b.wav", Version = "vb" },
        ];
        var j = h.AddJob();
        j.VoiceProfileName = "Mening ovozim · B";
        h.Store.Update(j);
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        var e = Assert.Single(tts.Ensured);
        Assert.Equal((ChB, "vb"), (e.Name, e.Version)); // B kanali namunasi, umumiy yoki A emas
        Assert.All(tts.Voices, v => Assert.Equal(ChB, v));
        Assert.Contains(reports, x => x.V == "vb" && x.S == "ready");
        Assert.Equal(1, imp.Calls);
    }

    [Fact]
    public void ReadVoiceClones_SharedAndEveryConnectedChannel()
    {
        var d = new Dictionary<string, string>
        {
            [SqliteSettingsStore.ChannelsKey] = ChannelsJson,
            ["voice.clone"] = CloneJson(Profile, "s1"),
            ["voice.clone:UCb"] = CloneJson(ChB, "vb"),
            ["voice.clone:UCgone"] = CloneJson("YtAuto u-abcd1234-0000000c", "vc"), // uzilgan kanal - o'qilmaydi
        };
        var all = SqliteSettingsStore.ReadVoiceClones(k => d.GetValueOrDefault(k)).ToList();
        Assert.Equal(["voice.clone", "voice.clone:UCb"], all.Select(x => x.Key));
        Assert.Equal(ChB, all[1].Clone.ProfileName);
    }

    [Fact]
    public void WriteStatus_GoesToTheKeyHoldingThatVersion()
    {
        using var h = new Harness();
        h.Store.SetSetting(SqliteSettingsStore.ChannelsKey, ChannelsJson);
        h.Store.SetSetting("voice.clone", CloneJson(Profile, "s1"));
        h.Store.SetSetting("voice.clone:UCa", CloneJson(ChA, "va"));
        SqliteSettingsStore.WriteVoiceCloneStatus(h.Store, "va", "ready", "ok");
        Assert.Contains("\"ready\"", h.Store.GetSetting("voice.clone:UCa"));
        Assert.Contains("\"pending\"", h.Store.GetSetting("voice.clone")); // umumiy namuna tegilmaydi
    }

    [Fact]
    public void CacheFiles_SharedProfileDoesNotClaimChannelFiles()
    {
        using var h = new Harness();
        var dir = VoiceCloneCache.Dir(h.Paths);
        Directory.CreateDirectory(dir);
        var shared = VoiceCloneCache.WavPath(h.Paths, new VoiceCloneSettings { ProfileName = Profile, Version = "s1" });
        var channel = VoiceCloneCache.WavPath(h.Paths, new VoiceCloneSettings { ProfileName = ChA, Version = "va" });
        foreach (var f in new[] { shared, shared + ".applied", channel, channel + ".applied" }) File.WriteAllText(f, "x");
        var files = VoiceCloneCache.Files(h.Paths, Profile).ToList();
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.StartsWith(channel, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, VoiceCloneCache.Files(h.Paths, ChA).Count());
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
