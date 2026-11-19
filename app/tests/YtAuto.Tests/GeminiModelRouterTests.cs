using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;
using YtAuto.Infrastructure.Gemini;

namespace YtAuto.Tests;

/// <summary>Model router: limit, mavjudlik va vazifa turi bo'yicha keyingi modelga o'tish, holat va sarf hisobi.</summary>
public class GeminiModelRouterTests
{
    /// <summary>Google API o'rnida: models ro'yxati va har (kalit, model) uchun berilgan javob.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public readonly List<(string Id, string Display)> Catalog = new();
        /// <summary>"key|model" yoki "*|model" → "day" | "minute" | "404" | "400" | "503".</summary>
        public readonly Dictionary<string, string> Fail = new();
        public readonly List<string> Calls = new();
        public int ListCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var key = req.Headers.GetValues("x-goog-api-key").Single();
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                ListCalls++;
                var models = new JsonArray(Catalog.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = "models/" + c.Id, ["displayName"] = c.Display, ["supportedGenerationMethods"] = new JsonArray("generateContent"),
                }).ToArray());
                return Ok(new JsonObject { ["models"] = models });
            }
            var model = path.Split("/models/")[1].Split(':')[0];
            Calls.Add($"{key}:{model}");
            var f = Fail.GetValueOrDefault($"{key}|{model}") ?? Fail.GetValueOrDefault($"*|{model}");
            return f switch
            {
                "day" => Err(429, """{"error":{"code":429,"message":"You exceeded your current quota","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_requests","quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"41s"}]}}"""),
                "minute" => Err(429, """{"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerMinutePerProjectPerModel-FreeTier"}]},{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"12s"}]}}"""),
                "404" => Err(404, """{"error":{"code":404,"message":"models/x is not found"}}"""),
                "400" => Err(400, """{"error":{"code":400,"message":"Search Grounding is not supported for this model"}}"""),
                "503" => Err(503, """{"error":{"code":503,"message":"The model is overloaded"}}"""),
                _ => Ok(new JsonObject
                {
                    ["candidates"] = new JsonArray(new JsonObject
                    {
                        ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["youtube_title"] = "T", ["subtitle_text"] = TestScript.Narration, ["youtube_description"] = "D", ["tags"] = new JsonArray("a", "b", "c", "d", "e") }.ToJsonString() }) },
                        ["finishReason"] = "STOP",
                    }),
                    ["usageMetadata"] = new JsonObject { ["totalTokenCount"] = 120 },
                }),
            };
        }
        private static Task<HttpResponseMessage> Ok(JsonNode body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
        private static Task<HttpResponseMessage> Err(int code, string body) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static readonly (string, string)[] Catalog =
    {
        ("gemini-3.8-flash-preview-11-2026", "Gemini 3.8 Flash"), ("gemini-3-flash", "Gemini 3 Flash"), ("gemini-3-flash-lite", "Gemini 3 Flash Lite"),
        ("gemini-3.5-flash-lite", "Gemini 3.5 Flash Lite"), ("gemini-2.5-flash", "Gemini 2.5 Flash"),
    };

    private static (GeminiScriptGenerator Gen, FakeApi Api, NullLog Log) Make(string[] keys, GeminiModelRouter? router = null)
    {
        var api = new FakeApi(); api.Catalog.AddRange(Catalog);
        var gen = new GeminiScriptGenerator(new MemSecrets(), new HttpClient(api)) { RemoteKeys = () => keys };
        if (router is not null) gen.Router = router;
        return (gen, api, new NullLog());
    }

    private static ScriptRequest Req(params string[] models) => new() { Topic = "Why water boils", PromptTemplate = "{topic}", Models = models, UseSearchGrounding = false };
    private static readonly string[] Chain3 = { "Gemini 3.8 Flash", "Gemini 3 Flash", "Gemini 3.5 Flash Lite" };

    [Fact]
    public async Task DisplayNames_ResolveToApiIds_ViaModelsList()
    {
        var (gen, api, _) = Make(new[] { "K" });
        var r = await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.Equal(new[] { "K:gemini-3.8-flash-preview-11-2026" }, api.Calls);
        Assert.Equal("gemini-3.8-flash-preview-11-2026", r.ModelUsed);
        await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.Equal(1, api.ListCalls); // ro'yxat keshlanadi
    }

    [Fact]
    public async Task PrimaryDailyLimit_OnAllKeys_FallsBackToNextModel_AndSkipsItAfterwards()
    {
        var (gen, api, log) = Make(new[] { "A", "B" });
        api.Fail["*|gemini-3.8-flash-preview-11-2026"] = "day";
        var r = await gen.GenerateAsync(Req(Chain3), log, CancellationToken.None);
        Assert.Equal(new[] { "A:gemini-3.8-flash-preview-11-2026", "B:gemini-3.8-flash-preview-11-2026", "A:gemini-3-flash" }, api.Calls);
        Assert.Equal("gemini-3-flash", r.ModelUsed);
        Assert.Contains(log.Lines, l => l.Contains("Gemini 3.8 Flash ishlatib bo'lmadi, so'rov Gemini 3 Flash bilan bajarildi"));

        api.Calls.Clear();
        await gen.GenerateAsync(Req(Chain3), log, CancellationToken.None);
        Assert.Equal(new[] { "A:gemini-3-flash" }, api.Calls); // kunlik limitdagi model qayta urilmaydi

        var u = gen.Router.Snapshot().Models["gemini-3.8-flash-preview-11-2026"];
        Assert.Equal(2, u.RateLimited);
        Assert.NotNull(u.DayLimitedUntil);
        Assert.Equal(2, gen.Router.Snapshot().Models["gemini-3-flash"].Requests);
    }

    [Fact]
    public async Task NotFound_Unsupported_Overloaded_AllMoveToNextModel()
    {
        var (gen, api, _) = Make(new[] { "K" });
        api.Fail["*|gemini-3.8-flash-preview-11-2026"] = "404";
        api.Fail["*|gemini-3-flash"] = "503";
        var r = await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.Equal("gemini-3.5-flash-lite", r.ModelUsed);
        Assert.False(gen.Router.ModelUsable("gemini-3.8-flash-preview-11-2026", GeminiTask.Script, out _)); // 404 - bir necha soat chetda
        Assert.True(gen.Router.ModelUsable("gemini-3-flash", GeminiTask.Script, out _)); // 503 - vaqtinchalik, keyingi safar yana sinaladi

        api.Fail["*|gemini-3.5-flash-lite"] = "400";
        api.Fail.Remove("*|gemini-3-flash");
        api.Fail["*|gemini-3-flash"] = "400";
        var e = await Assert.ThrowsAsync<PipelineException>(() => gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None));
        Assert.Equal("gemini_request", e.Code);
        Assert.False(gen.Router.ModelUsable("gemini-3-flash", GeminiTask.Script, out var why));
        Assert.Contains("Script", why);
        Assert.True(gen.Router.ModelUsable("gemini-3-flash", GeminiTask.Light, out _)); // boshqa vazifa uchun ochiq
    }

    [Fact]
    public async Task LightTasks_StartFromLiteModels()
    {
        var (gen, api, _) = Make(new[] { "K" });
        await gen.DescribeAsync(new MetadataRequest { Title = "T", Text = "x", Models = Chain3 }, new NullLog(), CancellationToken.None);
        Assert.Equal(new[] { "K:gemini-3.5-flash-lite" }, api.Calls);
    }

    [Fact]
    public async Task EverythingLimited_StopsWithQuotaError_AndRetryTime()
    {
        var (gen, api, _) = Make(new[] { "A", "B" });
        foreach (var (id, _) in Catalog) api.Fail["*|" + id] = "minute";
        var e = await Assert.ThrowsAsync<PipelineException>(() => gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None));
        Assert.Equal(ErrorCategory.Configuration, e.Category);
        Assert.Equal("gemini_quota_exhausted", e.Code);
        Assert.Contains("Barcha Gemini modellari (3 ta)", e.Message);
        Assert.Contains("Eng yaqin tiklanish", e.Message);
        Assert.Equal(6, api.Calls.Count);
    }

    [Fact]
    public async Task DailyLimit_PersistsAcrossRestarts_InStateFile()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "gemini-router.json");
        var (gen, api, _) = Make(new[] { "K" }, new GeminiModelRouter(path));
        api.Fail["*|gemini-3.8-flash-preview-11-2026"] = "day";
        await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.DoesNotContain("\"K\"", File.ReadAllText(path)); // kalit ochiq yozilmaydi

        var (gen2, api2, _) = Make(new[] { "K" }, new GeminiModelRouter(path));
        await gen2.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.Equal(new[] { "K:gemini-3-flash" }, api2.Calls);
    }

    [Fact]
    public async Task GuestCalls_KeepLimitState_ButAreNotCountedForOwner()
    {
        var published = new List<GeminiUsageSnapshot>();
        var guest = true;
        var router = new GeminiModelRouter { Counting = () => !guest, UsageChanged = published.Add };
        var (gen, _, _) = Make(new[] { "K" }, router);
        await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        Assert.Empty(published);
        Assert.Equal(0, router.Snapshot().Models.Values.Sum(m => m.Requests));
        guest = false;
        await gen.GenerateAsync(Req(Chain3), new NullLog(), CancellationToken.None);
        var snap = published.Last();
        Assert.Equal(1, snap.Models["gemini-3.8-flash-preview-11-2026"].Requests);
        Assert.Equal(120, snap.Models["gemini-3.8-flash-preview-11-2026"].Tokens);
        Assert.Equal("Gemini 3.8 Flash", snap.Models["gemini-3.8-flash-preview-11-2026"].Display);
        Assert.Equal(GeminiModelRouter.PacificDay(DateTimeOffset.UtcNow), snap.Day);
    }

    [Fact]
    public void NewPacificDay_ResetsUsage()
    {
        var now = new DateTimeOffset(2026, 11, 19, 7, 0, 0, TimeSpan.Zero); // Tinch okeanida 18-noyabr kechasi 23:00
        var r = new GeminiModelRouter { Now = () => now };
        r.Success("k", "m", "M", 10, 1);
        Assert.Equal("2026-11-18", r.Snapshot().Day);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero), GeminiModelRouter.NextPacificMidnight(now));
        now = now.AddHours(2);
        Assert.Equal("2026-11-19", r.Snapshot().Day);
        Assert.Empty(r.Snapshot().Models);
    }

    [Fact]
    public void RateLimitParse_ReadsDailyQuotaAndRetryDelay()
    {
        var day = GeminiRateLimitException.Parse("""{"error":{"details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},{"retryDelay":"41s"}]}}""", "m");
        Assert.True(day.PerDay); Assert.Equal(TimeSpan.FromSeconds(41), day.RetryAfter);
        var minute = GeminiRateLimitException.Parse("not json", "Quota exceeded. Please retry in 12.5s.");
        Assert.False(minute.PerDay); Assert.Equal(TimeSpan.FromSeconds(12.5), minute.RetryAfter);
        Assert.Equal(ErrorCategory.Transient, minute.Category);
        Assert.Equal("gemini_rate_limit", minute.Code);
    }

    [Theory]
    [InlineData("Gemini 3.8 Flash", "gemini-3.8-flash-preview-11-2026")]
    [InlineData("gemini-3.8-flash", "gemini-3.8-flash-preview-11-2026")] // -preview-sana qo'shimchasi topiladi
    [InlineData("Gemini 3 Flash", "gemini-3-flash")]
    [InlineData("models/gemini-2.5-flash", "gemini-2.5-flash")]
    [InlineData("Gemini 9 Ultra", null)]
    public void Match_ByIdDisplayNameOrVariant(string name, string? id) => Assert.Equal(id, GeminiModelRouter.Match(name, Catalog));

    [Fact]
    public void Match_WithoutCatalog_MakesIdFromName_AndLiteIsNotFlash()
    {
        Assert.Equal("gemini-3.8-flash", GeminiModelRouter.Match("Gemini 3.8 Flash", null));
        Assert.Equal("gemini-3-flash", GeminiModelRouter.Match("gemini-3-flash", new[] { ("gemini-3-flash-lite", "Gemini 3 Flash Lite"), ("gemini-3-flash", "Gemini 3 Flash") }));
        Assert.Null(GeminiModelRouter.Match("gemini-3-flash", new[] { ("gemini-3-flash-lite", "Gemini 3 Flash Lite") }));
    }

    [Fact]
    public void ModelChain_DefaultsToAllFreeTextModels_LegacyModelLast()
    {
        var g = new GeminiSettings { Model = "gemini-2.0-pro" };
        var chain = g.ModelChain(GeminiModelRouter.DefaultChain);
        Assert.Equal("Gemini 3.8 Flash", chain[0]);
        Assert.Equal(new[] { "Gemini 3 Flash", "Gemini 3.5 Flash Lite" }, chain.Skip(1).Take(2));
        Assert.Equal("gemini-2.0-pro", chain[^1]);
        Assert.Equal(GeminiModelRouter.DefaultChain.Length + 1, chain.Count);
        g.Models = new() { "Gemini 3 Flash", " gemini 3 flash ", "" };
        Assert.Equal(new[] { "Gemini 3 Flash", "gemini-2.0-pro" }, g.ModelChain(GeminiModelRouter.DefaultChain));
    }

    [Fact]
    public void BotConfig_ReadsModelChain()
    {
        using var t = new TempDir();
        var db = new YtAuto.Infrastructure.Data.SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey, """{"gemini":{"model":"gemini-2.5-flash","models":["Gemini 3.8 Flash"," Gemini 3 Flash ",""]}}""");
        var g = new YtAuto.Infrastructure.Data.SqliteSettingsStore(db, () => new AppSettings(), db).Load().Gemini;
        Assert.Equal(new[] { "Gemini 3.8 Flash", "Gemini 3 Flash" }, g.Models);
    }
}
