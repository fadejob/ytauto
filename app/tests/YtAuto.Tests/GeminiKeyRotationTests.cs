using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Gemini;

namespace YtAuto.Tests;

/// <summary>Bir nechta Gemini kaliti: 429 bo'lsa keyingisiga o'tiladi, hammasi limitda bo'lsa ish to'xtaydi.</summary>
public class GeminiKeyRotationTests
{
    private sealed class FakeGemini : HttpMessageHandler
    {
        public readonly HashSet<string> Limited = new();
        public readonly List<string> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var key = req.Headers.GetValues("x-goog-api-key").Single();
            Calls.Add(key);
            if (Limited.Contains(key))
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("""{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED"}}""", Encoding.UTF8, "application/json"),
                });
            var script = new JsonObject { ["youtube_title"] = "Why water boils", ["subtitle_text"] = TestScript.Narration }.ToJsonString();
            var body = new JsonObject
            {
                ["candidates"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = script }) },
                    ["finishReason"] = "STOP",
                }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private static ScriptRequest Req() => new() { Topic = "Why water boils", PromptTemplate = "{topic}", Model = "gemini-2.5-flash", UseSearchGrounding = false };

    [Fact]
    public async Task RateLimitedKey_SwitchesToNextKey_AndRemembersIt()
    {
        var fake = new FakeGemini(); fake.Limited.Add("A");
        var gen = new GeminiScriptGenerator(new MemSecrets(), new HttpClient(fake)) { RemoteKeys = () => new[] { "A", "B" } };
        var log = new NullLog();

        var r = await gen.GenerateAsync(Req(), log, CancellationToken.None);
        Assert.Equal("Why water boils", r.Script.YouTubeTitle);
        Assert.Equal(new[] { "A", "B" }, fake.Calls);
        Assert.Contains(log.Lines, l => l.Contains("1-kaliti limiti tugadi") && l.Contains("2-kalit"));

        // Keyingi chaqiruv limitdagi kalitni qayta urmasdan, ishlagan kalitdan boshlanadi.
        fake.Calls.Clear();
        await gen.GenerateAsync(Req(), log, CancellationToken.None);
        Assert.Equal(new[] { "B" }, fake.Calls);
    }

    [Fact]
    public async Task AllKeysRateLimited_StopsWithConfigurationError()
    {
        var fake = new FakeGemini(); fake.Limited.UnionWith(new[] { "A", "B", "C" });
        var gen = new GeminiScriptGenerator(new MemSecrets(), new HttpClient(fake)) { RemoteKeys = () => new[] { "A", "B", "C" } };

        var e = await Assert.ThrowsAsync<PipelineException>(() => gen.GenerateAsync(Req(), new NullLog(), CancellationToken.None));
        Assert.Equal(ErrorCategory.Configuration, e.Category); // NeedsAttention: qayta urinilmaydi
        Assert.Equal("gemini_quota_exhausted", e.Code);
        Assert.Equal(new[] { "A", "B", "C" }, fake.Calls);
    }

    [Fact]
    public void BotConfig_ReadsKeyListAndLegacySingleKey()
    {
        using var t = new TempDir();
        var db = new YtAuto.Infrastructure.Data.SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey, """{"gemini":{"apiKeys":["K1"," K2 ","K1"],"apiKey":"K0"}}""");
        var store = new YtAuto.Infrastructure.Data.SqliteSettingsStore(db, () => new AppSettings(), db);
        Assert.Equal(new[] { "K1", "K2", "K0" }, store.Load().Gemini.RemoteApiKeys);
    }
}
