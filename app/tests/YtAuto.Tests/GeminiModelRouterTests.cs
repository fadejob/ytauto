using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Gemini;

namespace YtAuto.Tests;

/// <summary>Model router: asosiy model limitda/band/topilmasa zaxira modellarga o'tib ish oxirigacha yetkaziladi.</summary>
public class GeminiModelRouterTests
{
    private const string Primary = "gemini-2.5-flash";

    private sealed class FakeGemini : HttpMessageHandler
    {
        /// <summary>model → HTTP holati (yo'q bo'lsa 200).</summary>
        public readonly Dictionary<string, int> Status = new();
        public string LimitBody = """{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED"}}""";
        public readonly List<(string Model, string Key)> Calls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var model = Uri.UnescapeDataString(path[(path.IndexOf("/models/", StringComparison.Ordinal) + 8)..path.LastIndexOf(':')]);
            Calls.Add((model, req.Headers.GetValues("x-goog-api-key").Single()));
            var code = Status.TryGetValue(model, out var c) ? c : 200;
            if (code != 200)
            {
                var body = code == 429 ? LimitBody : "{\"error\":{\"code\":" + code + ",\"message\":\"err " + code + "\"}}";
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
            var script = new JsonObject { ["youtube_title"] = "Why water boils", ["subtitle_text"] = TestScript.Narration }.ToJsonString();
            var ok = new JsonObject
            {
                ["candidates"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = script }) }, ["finishReason"] = "STOP",
                }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ok.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }

    private static ScriptRequest Req() => new() { Topic = "Why water boils", PromptTemplate = "{topic}", Model = Primary, UseSearchGrounding = false };

    private static GeminiScriptGenerator Gen(FakeGemini fake, params string[] fallbacks) =>
        new(new MemSecrets(), new HttpClient(fake)) { RemoteKeys = () => new[] { "A", "B" }, FallbackModels = () => fallbacks };

    [Fact]
    public async Task PrimaryLimitedOnAllKeys_SwitchesToFallback_AndFinishes()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 429;
        var log = new NullLog();
        var r = await Gen(fake, "gemini-2.5-flash-lite").GenerateAsync(Req(), log, CancellationToken.None);

        Assert.Equal("Why water boils", r.Script.YouTubeTitle);
        Assert.Equal(new[] { (Primary, "A"), (Primary, "B"), ("gemini-2.5-flash-lite", "A") }, fake.Calls);
        Assert.Contains(log.Lines, l => l.Contains("limiti tugadi (429)") && l.Contains("gemini-2.5-flash-lite"));
        Assert.Contains(log.Lines, l => l.Contains("zaxira modeldan"));
    }

    [Fact]
    public async Task LimitedModel_IsSkippedOnNextCall()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 429;
        var gen = Gen(fake, "gemini-2.5-flash-lite");
        await gen.GenerateAsync(Req(), new NullLog(), CancellationToken.None);
        fake.Calls.Clear();
        await gen.GenerateAsync(Req(), new NullLog(), CancellationToken.None);
        Assert.Equal(new[] { ("gemini-2.5-flash-lite", "A") }, fake.Calls); // limitdagi model qayta urilmaydi
    }

    [Fact]
    public async Task MissingModel_IsRememberedAndSkipped()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 429; fake.Status["gemini-gone"] = 404;
        var gen = Gen(fake, "gemini-gone", "gemini-2.5-flash-lite");
        await gen.GenerateAsync(Req(), new NullLog(), CancellationToken.None);
        Assert.Contains(("gemini-gone", "A"), fake.Calls);
        Assert.Equal(("gemini-2.5-flash-lite", "A"), fake.Calls[^1]);
        Assert.DoesNotContain("gemini-gone", gen.RouteModels(Primary, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task OverloadedPrimary_UsesFallbackImmediately()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 503;
        var r = await Gen(fake, "gemini-2.5-flash-lite").GenerateAsync(Req(), new NullLog(), CancellationToken.None);
        Assert.Equal("Why water boils", r.Script.YouTubeTitle);
        Assert.Equal(new[] { (Primary, "A"), ("gemini-2.5-flash-lite", "A") }, fake.Calls);
    }

    [Fact]
    public async Task AllModelsLimited_StopsWithConfigurationError()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 429; fake.Status["gemini-2.5-flash-lite"] = 429;
        var e = await Assert.ThrowsAsync<PipelineException>(() => Gen(fake, "gemini-2.5-flash-lite").GenerateAsync(Req(), new NullLog(), CancellationToken.None));
        Assert.Equal(ErrorCategory.Configuration, e.Category);
        Assert.Equal("gemini_quota_exhausted", e.Code);
        Assert.Contains("2 ta modelda", e.Message);
        Assert.Equal(4, fake.Calls.Count); // har model x har kalit bir martadan
    }

    [Fact]
    public async Task BadRequestOnPrimary_IsNotHiddenByFallback()
    {
        var fake = new FakeGemini(); fake.Status[Primary] = 400;
        var e = await Assert.ThrowsAsync<PipelineException>(() => Gen(fake, "gemini-2.5-flash-lite").GenerateAsync(Req(), new NullLog(), CancellationToken.None));
        Assert.Equal("gemini_request", e.Code);
        Assert.All(fake.Calls, c => Assert.Equal(Primary, c.Model));
    }

    [Fact]
    public void RateInfo_ReadsRetryDelayAndDailyQuota()
    {
        var raw = """{"error":{"code":429,"details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"34.5s"}]}}""";
        Assert.Equal((34500, true), GeminiScriptGenerator.RateInfo(raw));
        Assert.Equal(((int?)null, false), GeminiScriptGenerator.RateInfo("""{"error":{"code":429}}"""));
    }

    [Fact]
    public void BotConfig_ReadsFallbackModels()
    {
        using var t = new TempDir();
        var db = new YtAuto.Infrastructure.Data.SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        var store = new YtAuto.Infrastructure.Data.SqliteSettingsStore(db, () => new AppSettings(), db);

        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey, """{"gemini":{"model":"gemini-2.5-flash","fallbackModels":["gemini-2.5-flash-lite"," bad model ","gemini-2.5-flash-lite"]}}""");
        Assert.Equal(new[] { "gemini-2.5-flash-lite" }, store.Load().Gemini.FallbackModels);

        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey, """{"gemini":{"fallbackModels":[]}}""");
        Assert.Empty(store.Load().Gemini.FallbackModels); // botda bo'sh qoldirilgan - zaxirasiz

        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey, """{"gemini":{"model":"gemini-2.5-flash"}}""");
        Assert.Equal(GeminiSettings.DefaultFallbackModels, store.Load().Gemini.FallbackModels); // eski bot - standart ro'yxat
    }
}
