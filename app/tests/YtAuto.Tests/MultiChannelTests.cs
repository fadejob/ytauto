using YtAuto.Core.Domain;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Tests;

/// <summary>Bir hisobda bir nechta YouTube kanali: har vazifa o'z kanali tokeni va konteksti bilan ishlaydi.</summary>
public class MultiChannelTests
{
    private const string Channels = """
        {"active":"UC_B","channels":[
          {"id":"UC_A","title":"Alpha","refreshToken":"rt-a","scope":"","connectedAt":""},
          {"id":"UC_B","title":"Beta","refreshToken":"rt-b","scope":"","connectedAt":""}]}
        """;

    private static Func<string, string?> Db(Dictionary<string, string> d) => k => d.TryGetValue(k, out var v) ? v : null;
    private static string Ctx(string script) => $$"""{"enabled":true,"script":"{{script}}","montage":"m","voiceProfile":"","updatedAt":""}""";

    private static Dictionary<string, string> Shared() => new()
    {
        [SqliteSettingsStore.ChannelsKey] = Channels,
        [SqliteSettingsStore.LegacyOAuthKey] = """{"refreshToken":"rt-b","channelTitle":"Beta"}""",
        [SqliteSettingsStore.ChannelContextKey] = Ctx("ctx-active"),
        [SqliteSettingsStore.ChannelContextKey + ":UC_A"] = Ctx("ctx-a"),
        [SqliteSettingsStore.ChannelContextKey + ":UC_B"] = Ctx("ctx-b"),
    };

    [Fact]
    public void NoScope_UsesActiveChannel()
    {
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(Shared()), null);
        Assert.Equal(("rt-b", "Beta", "UC_B"), (s.YouTube.RemoteRefreshToken, s.YouTube.ChannelTitle, s.YouTube.ChannelId));
        Assert.Equal("ctx-b", s.ChannelContext.Script);
        Assert.False(s.YouTube.RemoteOnly);
    }

    [Fact]
    public void JobScope_UsesJobChannel_NotActive()
    {
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(Shared()), "UC_A");
        Assert.Equal(("rt-a", "Alpha", "UC_A"), (s.YouTube.RemoteRefreshToken, s.YouTube.ChannelTitle, s.YouTube.ChannelId));
        Assert.Equal("ctx-a", s.ChannelContext.Script);
    }

    [Fact]
    public void JobWithoutChannel_UsesActiveChannel()
    {
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(Shared()), "");
        Assert.Equal("rt-b", s.YouTube.RemoteRefreshToken);
    }

    [Fact]
    public void DisconnectedJobChannel_BlocksUpload_InsteadOfUsingAnotherChannel()
    {
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(Shared()), "UC_GONE");
        Assert.Equal("", s.YouTube.RemoteRefreshToken);
        Assert.True(s.YouTube.RemoteOnly);       // kompyuterning lokal tokeniga ham tushmaydi
        Assert.True(s.YouTube.MissingChannel);
        Assert.False(s.ChannelContext.Enabled);  // boshqa kanal konteksti ham qo'shilmaydi
    }

    [Fact]
    public void ActiveWithoutOwnContext_FallsBackToActiveCopy()
    {
        var d = Shared();
        d.Remove(SqliteSettingsStore.ChannelContextKey + ":UC_B");
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(d), null);
        Assert.Equal("ctx-active", s.ChannelContext.Script);
        // Faol bo'lmagan kanalga nusxa berilmaydi.
        d.Remove(SqliteSettingsStore.ChannelContextKey + ":UC_A");
        var a = new AppSettings();
        SqliteSettingsStore.ApplyChannel(a, Db(d), "UC_A");
        Assert.False(a.ChannelContext.Enabled);
    }

    [Fact]
    public void LegacySingleConnection_StillWorks()
    {
        var d = new Dictionary<string, string>
        {
            [SqliteSettingsStore.LegacyOAuthKey] = """{"refreshToken":"rt-old","channelTitle":"Old"}""",
            [SqliteSettingsStore.ChannelContextKey] = Ctx("ctx-old"),
        };
        var s = new AppSettings();
        SqliteSettingsStore.ApplyChannel(s, Db(d), null);
        Assert.Equal(("rt-old", "Old"), (s.YouTube.RemoteRefreshToken, s.YouTube.ChannelTitle));
        Assert.Equal("ctx-old", s.ChannelContext.Script);
    }

    [Fact]
    public void ReadChannels_SkipsBrokenEntries_AndFixesActive()
    {
        var l = SqliteSettingsStore.ReadChannels("""{"active":"nope","channels":[{"id":"","refreshToken":"x"},{"id":"UC_1","refreshToken":""},{"id":"UC_2","title":"Two","refreshToken":"rt2"}]}""");
        Assert.NotNull(l);
        Assert.Single(l!.Channels);
        Assert.Equal("UC_2", l.Active);
        Assert.Null(SqliteSettingsStore.ReadChannels("not json"));
    }
}
