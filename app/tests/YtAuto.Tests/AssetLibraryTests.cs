using System.Net;
using Npgsql;
using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Media;
using Xunit;

namespace YtAuto.Tests;

/// <summary>Kutubxonani qidiruvni yozib oladigan soxta katalog.</summary>
internal sealed class FakeCatalog : IAssetCatalog
{
    public readonly List<AssetQuery> Queries = new();
    public readonly List<(long[] Ids, string Channel, long Job)> Usage = new();
    public Func<AssetQuery, IReadOnlyList<AssetHit>> Hits = _ => Array.Empty<AssetHit>();
    public bool Missing;

    public Task<IReadOnlyList<AssetHit>> SearchAsync(AssetQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        if (Missing) throw new AssetLibraryUnavailableException("yo'q");
        return Task.FromResult(Hits(query));
    }

    public Task RecordUsageAsync(IReadOnlyCollection<long> assetIds, string channelId, long jobId, CancellationToken ct)
    { Usage.Add((assetIds.ToArray(), channelId, jobId)); return Task.CompletedTask; }

    public static AssetHit Hit(long id, string kind = "photo", string host = "https://upload.wikimedia.org", bool alpha = false, bool credit = true) =>
        new(id, "wikimedia", kind, $"Asset {id}", $"{host}/a/{id}.{(alpha ? "png" : "jpg")}", "", 1920, 1280, alpha,
            credit ? "CC BY 4.0" : "CC0", "", "Author " + id, $"https://commons.wikimedia.org/wiki/File:{id}", credit, 1.0 / id);
}

/// <summary>HTTP: har so'rovga 4 KB "rasm" qaytaradi va so'ralgan manzillarni yozadi.</summary>
internal sealed class FakeImageHandler : HttpMessageHandler
{
    public readonly List<Uri> Requested = new();
    public HttpStatusCode Status = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requested.Add(request.RequestUri!);
        var r = new HttpResponseMessage(Status) { Content = new ByteArrayContent(new byte[4096]), RequestMessage = request };
        r.Content.Headers.ContentType = new("image/jpeg");
        return Task.FromResult(r);
    }
}

public class AssetLibraryTests
{
    [Fact]
    public void Pacing_DefaultsByFormat_TemplateWins_Clamped()
    {
        Assert.Equal(Pacing.VerticalDefault, Pacing.SecondsPerShot(VideoFormat.Shorts9x16, null));
        Assert.Equal(Pacing.LandscapeDefault, Pacing.SecondsPerShot(VideoFormat.Landscape16x9, null));
        Assert.Equal(1.3, Pacing.SecondsPerShot(VideoFormat.Landscape16x9, 1.3));
        Assert.Equal(Pacing.Min, Pacing.SecondsPerShot(VideoFormat.Shorts9x16, 0.2));
        Assert.Equal(Pacing.Max, Pacing.SecondsPerShot(VideoFormat.Shorts9x16, 30));
        Assert.Equal(Pacing.VerticalDefault, Pacing.SecondsPerShot(VideoFormat.Shorts9x16, 0));
    }

    [Fact]
    public void Pacing_FromCuts_MergesFlickerAndNeedsTwoCuts()
    {
        // 36 s video, kesimlar: 1.5, 1.6 (bitta), 3.0, 4.5 ... -> 3 ta haqiqiy kesim -> 36/4 = 9
        Assert.Equal(9, Pacing.FromCuts(new[] { 1.5, 1.6, 3.0, 4.5 }, 36));
        Assert.Null(Pacing.FromCuts(new[] { 5.0 }, 36));
        Assert.Null(Pacing.FromCuts(Array.Empty<double>(), 1));
        var cuts = Enumerable.Range(1, 23).Select(i => i * 1.5).ToList();   // har 1.5 s
        Assert.Equal(1.5, Pacing.FromCuts(cuts, 36));
    }

    [Fact]
    public void Pacing_ShotBudget_FollowsLengthAndPace()
    {
        Assert.Equal(34, Pacing.ShotBudget(55, 1.6));
        Assert.Equal(62, Pacing.ShotBudget(150, 2.4)); // 62.5 -> 62 (bank yaxlitlashi)
        Assert.Equal(4, Pacing.ShotBudget(3, 2.4));
        Assert.Equal(150, Pacing.ShotBudget(900, 1));
    }

    [Fact]
    public void SceneTypes_NormalizeRangesKindsAndCandidates()
    {
        Assert.Equal(SceneTypes.Hero, SceneTypes.Normalize("whatever"));
        Assert.Equal(SceneTypes.Collage, SceneTypes.Normalize(" Collage "));
        Assert.Equal((3, 8), SceneTypes.ElementRange(SceneTypes.Collage));
        Assert.Equal(AssetKinds.Cutout, SceneTypes.AssetKindsFor(SceneTypes.Collage)[0]);
        Assert.Equal(AssetKinds.Photo, SceneTypes.AssetKindsFor(SceneTypes.Hero)[0]);
        Assert.Equal(12, SceneTypes.CandidatesFor(SceneTypes.Collage, 8));
        Assert.Equal(7, SceneTypes.CandidatesFor(SceneTypes.Collage, 1)); // kollaj kamida 3 element
        Assert.Equal(3, SceneTypes.CandidatesFor(SceneTypes.Hero, 1));
    }

    [Fact]
    public void MediaPlan_Clean_NormalizesSceneFields()
    {
        var plan = new MediaPlan { Items =
        {
            new() { Kind = "photo", Query = "honey bee", SceneType = "COLLAGE", Elements = 40, Keywords = { "Apis mellifera", "honey  bee", "Honey bee", "x", "pollinator insect" } },
            new() { Kind = "photo", Query = "volcano", SceneType = "nonsense", Elements = 0 },
            new() { Kind = "icon", Query = "lock", SceneType = "collage", Elements = 5 },
        } }.Clean(10);
        Assert.Equal("collage", plan.Items[0].SceneType);
        Assert.Equal(8, plan.Items[0].Elements);
        Assert.Equal(new[] { "Apis mellifera", "honey bee", "pollinator insect" }, plan.Items[0].Keywords);
        Assert.Equal("hero", plan.Items[1].SceneType);
        Assert.Equal(1, plan.Items[1].Elements);
        Assert.Equal("", plan.Items[2].SceneType); // ikon - sahna emas
    }

    [Fact]
    public void StockRequest_SearchTerms_QueryFirstThenKeywords()
    {
        Assert.Equal(new[] { "bee" }, new StockRequest("s01", "bee", "", 0).SearchTerms);
        Assert.Equal(new[] { "bee", "Apis mellifera" }, new StockRequest("s01", "bee", "", 0, "hero", 1, new[] { "Apis mellifera", "BEE" }).SearchTerms);
    }

    [Fact]
    public void StockRoom_UsesPaceAndPlannedElements()
    {
        var c = new StockCandidates();
        c.Scenes.Add(new StockScene { Id = "s01", At = 0, Elements = 6, Candidates = { new StockCandidate() } });
        c.Scenes.Add(new StockScene { Id = "s02", At = 3, Elements = 1, Candidates = { new StockCandidate() } });
        var room = EditorInstructions.StockRoom(c, 9, 1.5);
        Assert.Equal((3.0, 6), room["s01"]); // 3 s / 1.5 = 2, lekin kollajga 6 element rejalangan
        Assert.Equal((6.0, 4), room["s02"]);
    }

    [Fact]
    public void TsQuery_OrJoinedPhrasesAndSanitized()
    {
        Assert.Equal("\"honey bee\" or Apis or \"x ray\"", PostgresJobStore.TsQuery(new[] { "honey bee", "Apis", "apis", "x-ray", "\"", "a" }));
        Assert.Equal("", PostgresJobStore.TsQuery(new[] { "!", "" }));
        Assert.DoesNotContain("'", PostgresJobStore.TsQuery(new[] { "it's (bad) & | <x>" }));
    }

    [Fact]
    public void Library_AllowedHosts_HttpsOnly_TestOriginLoopbackOnly()
    {
        Assert.True(LibraryStockSource.Allowed(new Uri("https://upload.wikimedia.org/x.jpg")));
        Assert.True(LibraryStockSource.Allowed(new Uri("https://images-assets.nasa.gov/image/x~large.jpg")));
        Assert.True(LibraryStockSource.Allowed(new Uri("https://abc.supabase.co/storage/v1/object/public/library/x.png")));
        Assert.False(LibraryStockSource.Allowed(new Uri("http://upload.wikimedia.org/x.jpg")));
        Assert.False(LibraryStockSource.Allowed(new Uri("https://evilwikimedia.org/x.jpg")));
        Assert.False(LibraryStockSource.Allowed(new Uri("https://wikimedia.org.evil.com/x.jpg")));
        Assert.False(LibraryStockSource.Allowed(new Uri("http://127.0.0.1:8765/x.png")));
        Environment.SetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN", "http://127.0.0.1:8765");
        try
        {
            Assert.True(LibraryStockSource.Allowed(new Uri("http://127.0.0.1:8765/x.png")));
            Assert.False(LibraryStockSource.Allowed(new Uri("http://127.0.0.1:9999/x.png")));
        }
        finally { Environment.SetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN", null); }
        Environment.SetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN", "http://10.0.0.5:8765");
        try { Assert.False(LibraryStockSource.Allowed(new Uri("http://10.0.0.5:8765/x.png"))); } // faqat loopback
        finally { Environment.SetEnvironmentVariable("YTAUTO_LIBRARY_TEST_ORIGIN", null); }
    }

    [Fact]
    public async Task LibrarySource_DownloadsByScene_KindsAndCountsFromSceneType_NoReuse()
    {
        using var t = new TempDir();
        var cat = new FakeCatalog();
        cat.Hits = q => q.Kinds[0] == AssetKinds.Cutout
            ? Enumerable.Range(1, 20).Select(i => FakeCatalog.Hit(100 + i, "cutout", alpha: true)).Where(h => q.ExcludeIds?.Contains(h.Id) != true).ToList()
            : new[] { FakeCatalog.Hit(1), FakeCatalog.Hit(2, host: "https://evil.example.com"), FakeCatalog.Hit(3, credit: false) };
        var http = new FakeImageHandler();
        var src = new LibraryStockSource(cat, new HttpClient(http));
        var reqs = new[]
        {
            new StockRequest("s01", "volcano", "eruption", 0, "hero", 1, new[] { "volcano eruption" }),
            new StockRequest("s02", "bees", "many bees", 3, "collage", 5),
            new StockRequest("s03", "flowers", "more", 6, "collage", 3),
        };
        var r = await src.FetchAsync(reqs, t.Path, new StockOptions(5, 3, "portrait", "UC123"), new NullLog(), CancellationToken.None);

        Assert.Equal(new[] { AssetKinds.Photo, AssetKinds.Render, AssetKinds.Cutout }, cat.Queries[0].Kinds);
        Assert.Equal(new[] { "volcano", "volcano eruption" }, cat.Queries[0].Keywords);
        Assert.Equal("portrait", cat.Queries[0].Orientation);
        Assert.Equal("UC123", cat.Queries[0].ChannelId);
        var hero = r.Scenes[0];
        Assert.Equal(new long[] { 1, 3 }, hero.Candidates.Select(x => x.AssetId)); // evil host o'tkazildi, hero'ga 3 nomzod kerak edi - 2 qoldi
        Assert.Contains(hero.Notes, n => n.Contains("ruxsatsiz"));
        Assert.Equal("Asset 1 by Author 1, CC BY 4.0 (https://commons.wikimedia.org/wiki/File:1)", hero.Candidates[0].Credit);
        Assert.Equal("", hero.Candidates[1].Credit); // CC0 - muallif shart emas
        Assert.All(hero.Candidates, x => Assert.True(File.Exists(x.File)));
        Assert.Equal("library:wikimedia", hero.Candidates[0].Provider);

        var c1 = r.Scenes[1]; var c2 = r.Scenes[2];
        Assert.Equal(11, c1.Candidates.Count);                       // 5 element × 2 + 1
        Assert.Equal(7, c2.Candidates.Count);                        // 3 × 2 + 1
        Assert.All(c1.Candidates, x => { Assert.True(x.HasAlpha); Assert.Equal("cutout", x.Kind); Assert.EndsWith(".png", x.File); });
        Assert.Empty(c1.Candidates.Select(x => x.AssetId).Intersect(c2.Candidates.Select(x => x.AssetId))); // bir rasm ikki sahnada emas
        Assert.Equal("collage", c1.SceneType);
        Assert.Equal(5, c1.Elements);
    }

    [Fact]
    public async Task LibrarySource_MissingTable_Throws_FailedDownload_NotedAndRemoved()
    {
        using var t = new TempDir();
        var src = new LibraryStockSource(new FakeCatalog { Missing = true }, new HttpClient(new FakeImageHandler()));
        await Assert.ThrowsAsync<AssetLibraryUnavailableException>(() =>
            src.FetchAsync(new[] { new StockRequest("s01", "x", "", 0) }, t.Path, new StockOptions(5, 3, ""), new NullLog(), CancellationToken.None));

        var cat = new FakeCatalog { Hits = _ => new[] { FakeCatalog.Hit(1) } };
        var bad = new LibraryStockSource(cat, new HttpClient(new FakeImageHandler { Status = HttpStatusCode.NotFound }));
        var r = await bad.FetchAsync(new[] { new StockRequest("s01", "x", "", 0) }, t.Path, new StockOptions(5, 3, ""), new NullLog(), CancellationToken.None);
        Assert.Empty(r.Scenes[0].Candidates);
        Assert.Contains(r.Scenes[0].Notes, n => n.Contains("yuklanmadi"));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Path, "s01")));
    }

    [Fact]
    public void Kit_EmbeddedFilesWrittenAndDeterministic()
    {
        using var t = new TempDir();
        EditorKit.WriteTo(t.Path);
        foreach (var f in new[] { "kit.js", "kit.css", "README.md" }) Assert.True(new FileInfo(Path.Combine(t.Path, "kit", f)).Length > 500, f);
        var js = File.ReadAllText(Path.Combine(t.Path, "kit", "kit.js"));
        Assert.DoesNotContain("Math.random(", js);          // rasm faqat t ga bog'liq
        Assert.DoesNotContain("requestAnimationFrame", js);
        Assert.DoesNotContain("setTimeout", js);
        Assert.Contains("export function collage", js);
        Assert.Contains("export function page", js);
    }

    [Fact]
    public void Instructions_PacingKitAndLibraryCandidates()
    {
        var job = new VideoJob { Id = 7, Topic = "bees", Format = VideoFormat.Shorts9x16 };
        var tmp = Path.GetTempPath();
        var c = new JobContext
        {
            Job = job, Snapshot = new SettingsSnapshot { Settings = AppSettings.CreateDefault(tmp, tmp) }, WorkRoot = tmp, Dir = tmp, RecordDir = tmp,
            Log = new NullLog(), RunId = "t",
        };
        var stock = new StockCandidates();
        var s = new StockScene { Id = "s01", At = 0, Query = "bee", Purpose = "bees on a flower", SceneType = "collage", Elements = 4 };
        s.Candidates.Add(new StockCandidate { Label = "I1", Type = StockTypes.Image, File = "assets/stock/s01/img1.png", Width = 1600, Height = 1200, Provider = "library:wikimedia", AssetId = 5, Kind = "cutout", HasAlpha = true });
        stock.Scenes.Add(s);
        var cues = new List<SubtitleCue> { new() { Start = 0, End = 10, Text = "Bees see colours we cannot." } };
        var ins = EditorInstructions.Build(c, new YtAuto.Core.Script.VideoScript { SubtitleText = "Bees see colours we cannot.", YouTubeTitle = "Bees" }, cues, 10,
            null, Array.Empty<string>(), stock: stock, pace: 1.6, kit: true);
        Assert.Contains("## Rhythm", ins);
        Assert.Contains("about every **1.6 s**", ins);
        Assert.Contains("## Motion kit (`film/kit/`)", ins);
        Assert.Contains("film/kit/README.md", ins);
        Assert.Contains("curated image library", ins);
        Assert.Contains("scene type **collage**, 4 pictures planned", ins);
        Assert.Contains("(cutout, transparent PNG, 1600x1200", ins);
        Assert.Contains("Transparent PNGs", ins);
        Assert.Contains("room for about 6 pictures", ins); // 10 s / 1.6 = 6, rejadagi 4 dan ko'p
    }

    // ------------------------------------------------------------------ haqiqiy PostgreSQL (YTAUTO_PG_TEST)
    private static string? Cs => Environment.GetEnvironmentVariable("YTAUTO_PG_TEST");

    [Fact]
    public async Task OnlineCatalog_SearchRankingFiltersUsageAndRls()
    {
        if (string.IsNullOrEmpty(Cs)) return;
        var schema = "ytauto_test_" + Guid.NewGuid().ToString("N")[..8];
        var roles = new List<string>();
        try
        {
            _ = new PostgresJobStore(Cs, schema);
            await using (var c = new NpgsqlConnection(Cs))
            {
                await c.OpenAsync();
                var sql = $"""
                    INSERT INTO "{schema}".assets(source, source_id, kind, title, tags, description, file_url, width, height, has_alpha, quality, license, needs_credit)
                    VALUES ('wikimedia','1','photo','European honey bee on lavender','Apis mellifera, insects','A bee collecting nectar','https://upload.wikimedia.org/1.jpg',1920,1280,false,0.9,'CC BY 4.0',true),
                           ('wikimedia','2','photo','Bee hive frames','beekeeping','Honeycomb','https://upload.wikimedia.org/2.jpg',1280,1920,false,0.5,'CC0',false),
                           ('wikimedia','2','cutout','Honey bee','Apis mellifera, cutout','','https://x.supabase.co/c/2.png',1100,900,true,0.8,'CC0',false),
                           ('nasa','3','photo','Volcano from orbit','volcano, Earth','','https://images-assets.nasa.gov/3.jpg',1920,1080,false,0.7,'Public domain',false),
                           ('wikimedia','4','photo','Tiny bee','bee','','https://upload.wikimedia.org/4.jpg',800,600,false,0.9,'CC0',false)
                    """;
                await using var cmd = new NpgsqlCommand(sql, c);
                await cmd.ExecuteNonQueryAsync();
            }
            var (csA, roleA) = DeviceRole(schema, "A"); var (csB, roleB) = DeviceRole(schema, "B");
            roles.Add(roleA); roles.Add(roleB);
            var a = new PostgresJobStore(csA, schema);
            var b = new PostgresJobStore(csB, schema);

            var photos = await a.SearchAssetsAsync(new AssetQuery(new[] { "honey bee", "Apis mellifera", "bee" }, new[] { "photo" }, 10, "landscape", 900, "UC1"), default);
            Assert.Equal(new[] { "European honey bee on lavender", "Bee hive frames" }, photos.Select(h => h.Title)); // 800px'lisi chiqmaydi
            Assert.True(photos[0].NeedsCredit);
            var mixed = await a.SearchAssetsAsync(new AssetQuery(new[] { "honey bee" }, new[] { "cutout", "photo" }, 10), default);
            Assert.Contains(mixed, h => h.Kind == "cutout" && h.HasAlpha);
            Assert.Empty(await a.SearchAssetsAsync(new AssetQuery(new[] { "galaxy" }, new[] { "photo" }, 10), default));

            // Ishlatilgan rasm shu kanalda qayta chiqmaydi; boshqa kanalda va boshqa hisobda - chiqadi (asset_usage hisob bo'yicha, RLS).
            await a.RecordUsageAsync(new[] { photos[0].Id }, "UC1", 1, default);
            var again = await a.SearchAssetsAsync(new AssetQuery(new[] { "honey bee" }, new[] { "photo" }, 10, ChannelId: "UC1"), default);
            Assert.DoesNotContain(again, h => h.Id == photos[0].Id);
            Assert.Contains(await a.SearchAssetsAsync(new AssetQuery(new[] { "honey bee" }, new[] { "photo" }, 10, ChannelId: "UC2"), default), h => h.Id == photos[0].Id);
            Assert.Contains(await b.SearchAssetsAsync(new AssetQuery(new[] { "honey bee" }, new[] { "photo" }, 10, ChannelId: "UC1"), default), h => h.Id == photos[0].Id);
            var excluded = await a.SearchAssetsAsync(new AssetQuery(new[] { "bee" }, new[] { "photo" }, 10, ExcludeIds: new[] { photos[1].Id }), default);
            Assert.DoesNotContain(excluded, h => h.Id == photos[1].Id);

            // Qurilma roli kutubxonaga yoza olmaydi.
            await using (var dc = new NpgsqlConnection(csA))
            {
                await dc.OpenAsync();
                await using var ins = new NpgsqlCommand($"INSERT INTO \"{schema}\".assets(source, source_id, kind, file_url, license) VALUES ('x','x','photo','https://x','CC0')", dc);
                var e = await Assert.ThrowsAsync<PostgresException>(() => ins.ExecuteNonQueryAsync());
                Assert.Equal("42501", e.SqlState);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(Cs);
            await conn.OpenAsync();
            await using (var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", conn)) await drop.ExecuteNonQueryAsync();
            foreach (var role in roles) { await using var dr = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", conn); await dr.ExecuteNonQueryAsync(); }
        }
    }

    private static (string Cs, string Role) DeviceRole(string schema, string name)
    {
        var pw = "test-" + Guid.NewGuid().ToString("N");
        using var c = new NpgsqlConnection(Cs);
        c.Open();
        using var cmd = new NpgsqlCommand($"SELECT role_name FROM \"{schema}\".register_account(@d, @m, @h, @p)", c);
        cmd.Parameters.AddWithValue("d", name); cmd.Parameters.AddWithValue("m", name);
        cmd.Parameters.AddWithValue("h", YtAuto.Infrastructure.Accounts.DeviceAccount.Hash(Guid.NewGuid().ToString()));
        cmd.Parameters.AddWithValue("p", pw);
        var role = (string)cmd.ExecuteScalar()!;
        return (new NpgsqlConnectionStringBuilder(Cs) { Username = role, Password = pw }.ConnectionString, role);
    }
}
