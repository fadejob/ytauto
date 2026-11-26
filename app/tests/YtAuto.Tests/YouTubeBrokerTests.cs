using System.Net;
using System.Text;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.YouTube;

namespace YtAuto.Tests;

/// <summary>
/// Bot umumiy OAuth client'i bilan ulangan kanal: access token botdan olinadi (client secret kompyuterda yo'q),
/// o'z client'i bilan ulangan kanal esa avvalgidek Google'dan yangilanadi.
/// </summary>
public class YouTubeBrokerTests
{
    private sealed class FakeWeb : HttpMessageHandler
    {
        public readonly List<(string Url, string Body, string? Auth)> Calls = new();
        public HttpStatusCode BotStatus = HttpStatusCode.OK;
        public string BotBody = "{\"accessToken\":\"ya29.from-bot\",\"expiresIn\":3000}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r.RequestUri!.ToString(), body, r.Headers.Authorization?.ToString()));
            if (r.RequestUri!.AbsolutePath == "/api/device/youtube-token")
                return new HttpResponseMessage(BotStatus) { Content = new StringContent(BotBody, Encoding.UTF8, "application/json") };
            if (r.RequestUri.Host == "oauth2.googleapis.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"ya29.from-google\",\"expires_in\":3600}", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static (YouTubeAuth Auth, FakeWeb Web) Make(YouTubeSettings s, Guid? guest = null, bool account = true)
    {
        var web = new FakeWeb();
        var http = new HttpClient(web);
        var auth = new YouTubeAuth(new MemSecrets(), () => s, http);
        auth.Broker = new BotYouTubeTokenBroker(http, () => account ? ("https://bot.test", "yt_devicetoken") : null, () => guest).GetAsync;
        return (auth, web);
    }

    [Fact]
    public async Task Brokered_channel_gets_token_from_bot_not_google()
    {
        var guest = Guid.NewGuid();
        var (auth, web) = Make(new YouTubeSettings { RemoteRefreshToken = "1//rt", RemoteBrokered = true }, guest);
        Assert.Equal("ya29.from-bot", await auth.GetAccessTokenAsync(CancellationToken.None));
        var call = Assert.Single(web.Calls);
        Assert.Equal("https://bot.test/api/device/youtube-token", call.Url);
        Assert.Equal("Bearer yt_devicetoken", call.Auth);
        Assert.Contains(guest.ToString(), call.Body);
        // Keshdan: ikkinchi chaqiruv botga bormaydi.
        Assert.Equal("ya29.from-bot", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Single(web.Calls);
    }

    [Fact]
    public async Task Own_client_channel_still_refreshes_with_google()
    {
        var (auth, web) = Make(new YouTubeSettings { RemoteRefreshToken = "1//rt", ClientId = "id", RemoteClientSecret = "secret" });
        Assert.Equal("ya29.from-google", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("https://oauth2.googleapis.com/token", Assert.Single(web.Calls).Url);
    }

    [Fact]
    public async Task Bot_refusal_asks_to_reconnect()
    {
        var (auth, web) = Make(new YouTubeSettings { RemoteRefreshToken = "1//rt", RemoteBrokered = true });
        web.BotStatus = HttpStatusCode.Conflict;
        web.BotBody = "{\"error\":\"YouTube ruxsati bekor qilingan — kanalni qayta ulang.\"}";
        var e = await Assert.ThrowsAsync<PipelineException>(() => auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(ErrorCategory.Configuration, e.Category);
        Assert.Contains("qayta ulang", e.Message);
    }

    [Fact]
    public async Task Bot_outage_is_transient()
    {
        var (auth, web) = Make(new YouTubeSettings { RemoteRefreshToken = "1//rt", RemoteBrokered = true });
        web.BotStatus = HttpStatusCode.BadGateway;
        var e = await Assert.ThrowsAsync<PipelineException>(() => auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(ErrorCategory.Transient, e.Category);
    }

    [Fact]
    public async Task Brokered_without_device_account_is_config_error()
    {
        var (auth, _) = Make(new YouTubeSettings { RemoteRefreshToken = "1//rt", RemoteBrokered = true }, account: false);
        var e = await Assert.ThrowsAsync<PipelineException>(() => auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("youtube_broker_missing", e.Code);
    }
}
