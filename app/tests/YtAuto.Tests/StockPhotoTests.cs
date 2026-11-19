using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Media;
using Xunit;

namespace YtAuto.Tests;

/// <summary>Fotolar sifat tartibida: Pexels → Pixabay → Openverse → Wikimedia; kalitsiz manbalar o'tkazib yuboriladi.</summary>
public class StockPhotoTests
{
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0, 0, 0, 0, 0, 0 };

    /// <summary>Tarmoqsiz soxta manbalar: har host uchun javob, so'rovlar yoziladi.</summary>
    private sealed class FakeWeb : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public HttpStatusCode PexelsStatus = HttpStatusCode.OK;
        public HttpStatusCode PixabayStatus = HttpStatusCode.OK;
        /// <summary>Pexels faqat bitta foto qaytaradi (takror ishlatilmasligini sinash uchun).</summary>
        public int PexelsPhotos = 2;

        public int Count(string host) => Requests.Count(r => r.RequestUri!.Host == host);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req);
            var u = req.RequestUri!;
            return Task.FromResult(u.Host switch
            {
                "api.iconify.design" => Json("{}"),
                "api.pexels.com" when PexelsStatus != HttpStatusCode.OK => new HttpResponseMessage(PexelsStatus),
                "api.pexels.com" => Json(new JsonObject
                {
                    ["photos"] = new JsonArray(Enumerable.Range(1, PexelsPhotos).Select(i => (JsonNode)new JsonObject
                    {
                        ["width"] = 4000, ["height"] = 2600, ["url"] = $"https://www.pexels.com/photo/p-{i}/", ["photographer"] = "Ann", ["alt"] = "terminal " + i,
                        ["src"] = new JsonObject { ["original"] = $"https://images.pexels.com/photos/{i}/a.jpeg", ["large2x"] = $"https://images.pexels.com/photos/{i}/a.jpeg?w=940" },
                    }).ToArray()),
                }.ToJsonString()),
                "pixabay.com" when PixabayStatus != HttpStatusCode.OK => new HttpResponseMessage(PixabayStatus),
                "pixabay.com" => Json(new JsonObject
                {
                    ["hits"] = new JsonArray(new JsonObject
                    {
                        ["imageWidth"] = 5000, ["imageHeight"] = 3000, ["pageURL"] = "https://pixabay.com/photos/x-1/", ["user"] = "Bob", ["tags"] = "card, terminal",
                        ["largeImageURL"] = "https://cdn.pixabay.com/photo/x_1280.jpg",
                    }),
                }.ToJsonString()),
                "api.openverse.org" => Json(new JsonObject
                {
                    ["results"] = new JsonArray(new JsonObject
                    {
                        ["url"] = "https://live.staticflickr.com/1.jpg", ["title"] = "t", ["creator"] = "Cy", ["license"] = "by", ["license_version"] = "2.0",
                        ["foreign_landing_url"] = "https://flickr.com/1", ["width"] = 2000, ["height"] = 1200, ["filetype"] = "jpg",
                    }),
                }.ToJsonString()),
                "commons.wikimedia.org" => Json("{}"),
                "images.pexels.com" or "cdn.pixabay.com" or "live.staticflickr.com" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
    }

    private static MediaPlan Photos(params string[] queries) =>
        new() { Items = queries.Select((q, i) => new MediaPlanItem { Kind = MediaKinds.Photo, Query = q, At = i }).ToList() };

    private static async Task<(MediaManifest M, FakeWeb Web, NullLog Log)> Fetch(MediaPlan plan, StockPhotoKeys keys, Action<FakeWeb>? setup = null)
    {
        var web = new FakeWeb(); setup?.Invoke(web);
        var lib = new OpenMediaLibrary(new HttpClient(web), () => keys);
        var dir = Path.Combine(Path.GetTempPath(), "ytauto-stock-" + Guid.NewGuid().ToString("N")[..8]);
        var log = new NullLog();
        try { return (await lib.FetchAsync(plan, dir, log, CancellationToken.None), web, log); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Pexels_IsFirst_WhenKeySet_KeyGoesInHeaderNotUrl()
    {
        var (m, web, _) = await Fetch(Photos("card terminal"), new StockPhotoKeys("pexels-key-123456", "pixabay-key-123456"));
        var a = Assert.Single(m.Assets);
        Assert.Equal("pexels", a.Provider);
        Assert.Equal("Pexels License", a.License);
        Assert.False(a.NeedsCredit);
        Assert.Equal(0, web.Count("pixabay.com"));
        Assert.Equal(0, web.Count("api.openverse.org"));
        var req = web.Requests.First(r => r.RequestUri!.Host == "api.pexels.com");
        Assert.Equal("pexels-key-123456", req.Headers.GetValues("Authorization").Single());
        Assert.DoesNotContain("pexels-key", req.RequestUri!.ToString());
        // 1920 px variant yuklanadi.
        Assert.Contains(web.Requests, r => r.RequestUri!.Host == "images.pexels.com" && r.RequestUri.Query.Contains("w=1920"));
        Assert.Equal("", m.CreditsBlock());
    }

    [Fact]
    public async Task NoKeys_SkipsStockSites_UsesOpenverse()
    {
        var (m, web, log) = await Fetch(Photos("card terminal"), StockPhotoKeys.None);
        Assert.Equal("openverse", Assert.Single(m.Assets).Provider);
        Assert.Equal(0, web.Count("api.pexels.com"));
        Assert.Equal(0, web.Count("pixabay.com"));
        Assert.Contains(log.Lines, l => l.Contains("openverse → wikimedia") && l.Contains("Pexels/Pixabay"));
    }

    [Fact]
    public async Task RateLimitedPexels_IsDisabledForTheVideo_PixabayTakesOver()
    {
        var (m, web, log) = await Fetch(Photos("card terminal", "server room"), new StockPhotoKeys("pexels-key-123456", "pixabay-key-123456"),
            w => w.PexelsStatus = HttpStatusCode.TooManyRequests);
        Assert.Equal(2, m.Assets.Count);
        Assert.Equal("pixabay", m.Assets[0].Provider);
        Assert.Equal("Pixabay Content License", m.Assets[0].License);
        Assert.Equal(1, web.Count("api.pexels.com")); // ikkinchi foto uchun Pexels qayta so'ralmaydi
        Assert.Contains(log.Lines, l => l.StartsWith("W ") && l.Contains("pexels") && l.Contains("limit"));
    }

    [Fact]
    public async Task InvalidPixabayKey_FallsBackToOpenverse()
    {
        var (m, _, _) = await Fetch(Photos("card terminal"), new StockPhotoKeys("", "bad-key-1234567"), w => w.PixabayStatus = HttpStatusCode.BadRequest);
        Assert.Equal("openverse", Assert.Single(m.Assets).Provider);
    }

    [Fact]
    public async Task SamePhoto_IsNotUsedTwiceInOneVideo()
    {
        var (m, _, _) = await Fetch(Photos("card terminal", "payment terminal"), new StockPhotoKeys("pexels-key-123456", ""), w => w.PexelsPhotos = 1);
        Assert.Equal(2, m.Assets.Count);
        Assert.Equal("pexels", m.Assets[0].Provider);
        Assert.Equal("openverse", m.Assets[1].Provider);
        Assert.Equal(m.Assets.Count, m.Assets.Select(a => a.SourceUrl).Distinct().Count());
    }

    [Fact]
    public void Candidates_DropSmallImages_PreferLandscape()
    {
        var json = JsonNode.Parse("""
        {"photos":[
          {"width":900,"height":600,"url":"https://p/small","src":{"original":"https://i/small.jpg"}},
          {"width":2000,"height":3000,"url":"https://p/tall","src":{"original":"https://i/tall.jpg"}},
          {"width":3000,"height":2000,"url":"https://p/wide","src":{"original":"https://i/wide.jpg?x=1"}}
        ]}
        """);
        var c = OpenMediaLibrary.PexelsCandidates(json);
        Assert.Equal(new[] { "https://p/wide", "https://p/tall" }, c.Select(x => x.Page));
        Assert.Equal("https://i/wide.jpg?x=1&auto=compress&cs=tinysrgb&w=1920", c[0].Url);

        var px = OpenMediaLibrary.PixabayCandidates(JsonNode.Parse("""
        {"hits":[{"imageWidth":1000,"imageHeight":800,"pageURL":"https://px/a","largeImageURL":"https://c/a.jpg"},
                 {"imageWidth":4000,"imageHeight":3000,"pageURL":"https://px/b","largeImageURL":"https://c/b.jpg"}]}
        """));
        Assert.Equal("https://px/b", Assert.Single(px).Page);
    }
}
