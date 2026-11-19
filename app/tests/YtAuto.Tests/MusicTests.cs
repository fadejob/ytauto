using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Media;

namespace YtAuto.Tests;

/// <summary>Fon musiqasi: bot kutubxonasi o'qilishi, trek tanlash, montaj ko'rsatmasi, YouTube tavsifidagi muallif va kesh xavfsizligi.</summary>
public class MusicTests
{
    private const string Id1 = "caf821fe-95c0-4108-9e28-aeba217d747c";
    private const string Id2 = "0b6c2b1e-1a2b-4c3d-8e9f-0123456789ab";

    private static MusicTrack Track(string id = Id1, string title = "Calm Space") => new()
    {
        Id = id, Title = title, Creator = "Duda", License = "by", LicenseVersion = "3.0", LicenseUrl = "https://creativecommons.org/licenses/by/3.0/",
        LandingUrl = "https://www.jamendo.com/track/1097", DurationSec = 290, Url = "https://abc.supabase.co/storage/v1/object/sign/templates/x.mp3?token=t",
    };

    [Fact]
    public void ReadMusic_ParsesBotJson_AndDropsUnsafeTracks()
    {
        var json = $$"""
        {"enabled":true,"volume":0.5,"tracks":[
          {"id":"{{Id1}}","title":"Calm Space","creator":"Duda","license":"by","licenseVersion":"3.0","licenseUrl":"https://creativecommons.org/licenses/by/3.0/","landingUrl":"https://www.jamendo.com/track/1","durationSec":290,"url":"https://abc.supabase.co/a.mp3"},
          {"id":"{{Id2}}","title":"SA track","license":"by-sa","url":"https://abc.supabase.co/b.mp3"},
          {"id":"not-a-guid","title":"Bad id","license":"by","url":"https://abc.supabase.co/c.mp3"},
          {"id":"{{Id2}}","title":"Plain http","license":"cc0","url":"http://abc.supabase.co/d.mp3"}
        ]}
        """;
        var m = SqliteSettingsStore.ReadMusic(json);
        Assert.True(m.Enabled);
        Assert.Equal(0.2, m.Volume); // nutqni bosmasligi uchun 0.2 dan oshmaydi
        var t = Assert.Single(m.Tracks);
        Assert.Equal(("Calm Space", "Duda", "by", 290), (t.Title, t.Creator, t.License, t.DurationSec));
    }

    [Fact]
    public void ReadMusic_EmptyOrBroken_IsOff()
    {
        Assert.Empty(SqliteSettingsStore.ReadMusic(null).Tracks);
        Assert.Empty(SqliteSettingsStore.ReadMusic("{oops").Tracks);
        Assert.False(SqliteSettingsStore.ReadMusic("""{"enabled":false,"tracks":[]}""").Enabled);
    }

    [Fact]
    public void PickFor_RotatesByJob_AndRespectsSwitch()
    {
        var m = new MusicSettings { Enabled = true, Tracks = { Track(Id1, "A"), Track(Id2, "B") } };
        Assert.Equal("A", m.PickFor(10)!.Title);
        Assert.Equal("B", m.PickFor(11)!.Title);
        m.Enabled = false;
        Assert.Null(m.PickFor(10));
        Assert.Null(new MusicSettings { Enabled = true }.PickFor(1));
    }

    [Fact]
    public void Credit_NamesTitleAuthorLicenseAndSource()
    {
        Assert.Equal("Music: \"Calm Space\" by Duda, licensed under CC BY 3.0 (https://creativecommons.org/licenses/by/3.0/) - https://www.jamendo.com/track/1097", Track().Credit());
        Assert.StartsWith("Music: \"X\", licensed under CC0", new MusicTrack { Title = "X", License = "cc0" }.Credit());
    }

    [Fact]
    public void Credits_AreJoined_AndKeptInDescription()
    {
        var credits = JobRunner.JoinCredits("Image credits:\n- a", "", "Music: \"Calm Space\" by Duda");
        Assert.Equal("Image credits:\n- a\n\nMusic: \"Calm Space\" by Duda", credits);
        Assert.Equal("Music: m\n\nSubscribe!", JobRunner.ComposeDescription("From Gemini", JobRunner.JoinCredits("", "Music: m"), "Subscribe!"));
    }

    [Theory]
    [InlineData("abc.supabase.co", true)]
    [InlineData("prod-1.storage.jamendo.com", true)]
    [InlineData("upload.wikimedia.org", true)]
    [InlineData("supabase.co.evil.com", false)]
    [InlineData("example.com", false)]
    public void Cache_OnlyTrustedHosts(string host, bool ok) => Assert.Equal(ok, MusicCache.Allowed(host));

    [Fact]
    public void Cache_SniffsAudioFormats()
    {
        using var tmp = new TempDir();
        string F(byte[] b) { var p = Path.Combine(tmp.Path, Guid.NewGuid().ToString("N")); File.WriteAllBytes(p, b.Concat(new byte[32]).ToArray()); return p; }
        Assert.Equal(".mp3", MusicCache.Sniff(F("ID3"u8.ToArray())));
        Assert.Equal(".mp3", MusicCache.Sniff(F(new byte[] { 0xFF, 0xFB, 0x90 })));
        Assert.Equal(".ogg", MusicCache.Sniff(F("OggS"u8.ToArray())));
        Assert.Null(MusicCache.Sniff(F("<html>"u8.ToArray())));
    }

    [Fact]
    public async Task Pipeline_PutsMusicIntoMontage_AndCreditIntoDescription()
    {
        using var h = new Harness();
        var cache = new FakeMusicCache(h.Tmp.Path);
        h.MusicCache = cache;
        h.Settings.S.Music = new MusicSettings { Enabled = true, Volume = 0.1, Tracks = { Track() } };
        h.AddJob();
        await h.Run();

        Assert.Equal(1, cache.Calls);
        Assert.Contains("`film/assets/music.mp3`: background music \"Calm Space\"", h.Editor.LastInstructions);
        Assert.Contains("Background music: `assets/music.mp3` on a SECOND audio track", h.Editor.LastInstructions);
        Assert.Contains("volume exactly 0.10", h.Editor.LastInstructions);
        Assert.Contains("Music: \"Calm Space\" by Duda, licensed under CC BY 3.0", h.Uploader.Last!.Description);
    }

    [Fact]
    public async Task Pipeline_MusicFailure_DoesNotStopVideo()
    {
        using var h = new Harness();
        h.MusicCache = new FakeMusicCache(h.Tmp.Path) { Fail = true };
        h.Settings.S.Music = new MusicSettings { Enabled = true, Tracks = { Track() } };
        h.AddJob();
        await h.Run();

        Assert.NotNull(h.Uploader.Last);
        Assert.DoesNotContain("Music:", h.Uploader.Last!.Description);
        Assert.Contains("Optional music on another track", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task Pipeline_WithoutMusic_KeepsOldInstructions()
    {
        using var h = new Harness();
        h.MusicCache = new FakeMusicCache(h.Tmp.Path);
        h.AddJob();
        await h.Run();
        Assert.Contains("Optional music on another track", h.Editor.LastInstructions);
        Assert.DoesNotContain("Music:", h.Uploader.Last!.Description);
    }

    private sealed class FakeMusicCache : IMusicCache
    {
        private readonly string _dir;
        public int Calls;
        public bool Fail;
        public FakeMusicCache(string dir) => _dir = dir;
        public Task<string> GetLocalAsync(MusicTrack track, IJobLog log, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("server rad etdi");
            var p = Path.Combine(_dir, track.Id + ".mp3");
            File.WriteAllBytes(p, BitConverter.GetBytes(95.0).Concat(new byte[20_000]).ToArray()); // FakeMedia davomiylikni boshidagi double'dan o'qiydi
            return Task.FromResult(p);
        }
    }
}

/// <summary>Har mavzuning o'z musiqasi (bot mavzu oynasi).</summary>
public class TopicMusicTests
{
    private const string A = "caf821fe-95c0-4108-9e28-aeba217d747c";
    private const string B = "0b6c2b1e-1a2b-4c3d-8e9f-0123456789ab";

    private static MusicSettings Lib(bool enabled) => new()
    {
        Enabled = enabled,
        Tracks = { new MusicTrack { Id = A, Title = "A", License = "by", Url = "https://x.supabase.co/a" }, new MusicTrack { Id = B, Title = "B", License = "by", Url = "https://x.supabase.co/b" } },
    };

    [Fact]
    public void ReadMusic_ParsesTopicChoices_AndIgnoresJunk()
    {
        var m = SqliteSettingsStore.ReadMusic($$$"""{"enabled":true,"tracks":[],"topics":{"101":"{{{B}}}","102":"none","abc":"{{{A}}}","103":"../x","104":5}}""");
        Assert.Equal(2, m.Topics.Count);
        Assert.Equal(B, m.Topics[101]);
        Assert.Equal("none", m.Topics[102]);
    }

    [Fact]
    public void PickFor_TopicChoiceWins()
    {
        var m = Lib(enabled: true);
        m.Topics[10] = B;   // navbat bo'yicha A bo'lardi
        m.Topics[11] = "none";
        Assert.Equal("B", m.PickFor(10)!.Title);
        Assert.Null(m.PickFor(11));
        Assert.Equal("A", m.PickFor(12)!.Title); // tanlov yo'q - navbat
    }

    [Fact]
    public void PickFor_ChosenTrackPlays_EvenWhenAutoIsOff()
    {
        var m = Lib(enabled: false);
        m.Topics[7] = A;
        Assert.Equal("A", m.PickFor(7)!.Title);
        Assert.Null(m.PickFor(8));
    }

    [Fact]
    public void PickFor_RemovedTrack_FallsBackToAuto()
    {
        var m = Lib(enabled: true);
        m.Topics[11] = "11111111-2222-3333-4444-555555555555";
        Assert.Equal("B", m.PickFor(11)!.Title);
    }
}
