using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Media;
using YtAuto.Infrastructure.OpenFilm;
using Xunit;

namespace YtAuto.Tests;

/// <summary>OpenFilm o'rnida: har so'rovga berilgan sondagi rasm va video "yuklaydi" (FakeMedia o'qiy oladigan fayllar).</summary>
internal sealed class FakeStock : IStockMediaSource
{
    public bool Configured = true;
    public int Calls;
    public readonly List<StockRequest> Requests = new();
    public StockOptions? LastOptions;
    /// <summary>Shu so'rovlar uchun hech narsa topilmaydi.</summary>
    public readonly HashSet<string> NotFound = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Har sahnada bitta buzilgan (o'qib bo'lmaydigan) rasm qo'shiladi.</summary>
    public bool AddBroken;
    public bool IsConfigured => Configured;

    public Task<StockCandidates> FetchAsync(IReadOnlyList<StockRequest> scenes, string workDir, StockOptions o, IJobLog log, CancellationToken ct)
    {
        Calls++; LastOptions = o; Requests.AddRange(scenes);
        var r = new StockCandidates();
        var dir = Path.Combine(workDir, "assets"); Directory.CreateDirectory(dir);
        foreach (var q in scenes)
        {
            var s = new StockScene { Id = q.Id, Query = q.Query, Purpose = q.Purpose, At = q.At };
            r.Scenes.Add(s);
            if (NotFound.Contains(q.Query)) { s.Notes.Add("pexels: found no pictures"); continue; }
            for (int i = 1; i <= o.Clips; i++) s.Candidates.Add(Make(dir, $"{q.Id}_clip_{i}.mp4", StockTypes.Video, 6.0));
            for (int i = 1; i <= o.Images; i++) s.Candidates.Add(Make(dir, $"{q.Id}_img_{i}.jpg", StockTypes.Image, 0.04));
            if (AddBroken) { var b = Path.Combine(dir, $"{q.Id}_broken.jpg"); File.WriteAllBytes(b, new byte[] { 1 }); s.Candidates.Add(new StockCandidate { Type = StockTypes.Image, File = b, Provider = "pexels" }); }
        }
        return Task.FromResult(r);
    }

    private static StockCandidate Make(string dir, string name, string type, double dur)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, BitConverter.GetBytes(dur).Concat(new byte[64]).ToArray());
        return new StockCandidate { Type = type, File = p, Width = 1920, Height = 1080, Duration = dur, Provider = "pexels", Credit = "Photo by A on Pexels", Page = "https://www.pexels.com/x" };
    }
}

/// <summary>Rasm rejasini yozib oladigan MediaLibrary (qaysi bandlar ochiq manbalarga qolganini tekshirish uchun).</summary>
internal sealed class RecordingMediaLib : IMediaLibrary
{
    public readonly List<MediaPlan> Plans = new();
    public Task<MediaManifest> FetchAsync(MediaPlan plan, string mediaDir, IJobLog log, CancellationToken ct)
    {
        Plans.Add(plan);
        var m = new MediaManifest();
        foreach (var (i, n) in plan.Items.Select((i, n) => (i, n)))
        {
            var file = $"{i.Kind}_{n:00}.svg"; File.WriteAllText(Path.Combine(mediaDir, file), "x");
            m.Assets.Add(new MediaAsset { File = file, Kind = i.Kind, Query = i.Query, License = "CC0" });
        }
        return Task.FromResult(m);
    }
}

public class StockMediaTests
{
    private static (Harness H, FakeStock Stock, RecordingMediaLib Lib, FakeVisuals Vis) Setup()
    {
        var h = new Harness();
        var vis = new FakeVisuals
        {
            Plan = new MediaPlan { Items =
            {
                new() { Kind = "logo", Query = "Visa", Purpose = "card brand", At = 1 },
                new() { Kind = "photo", Query = "card terminal", Purpose = "paying at a shop", At = 3 },
                new() { Kind = "photo", Query = "server room", Purpose = "where data lives", At = 6 },
            } },
        };
        var stock = new FakeStock(); var lib = new RecordingMediaLib();
        h.Visuals = vis; h.MediaLib = lib; h.Stock = stock;
        h.AddJob();
        return (h, stock, lib, vis);
    }

    [Fact]
    public async Task Pipeline_OpenFilmCandidates_GoToClaudeForReview_PhotosNotFetchedTwice()
    {
        var (h, stock, lib, _) = Setup();
        using var _h = h;
        bool filesThere = false, sheetThere = false;
        h.Editor.DuringRun = () =>
        {
            var edit = Path.GetDirectoryName(Directory.GetFiles(h.Settings.S.Paths.WorkDir, "instructions.md", SearchOption.AllDirectories).Single())!;
            var film = Path.Combine(edit, "film");
            filesThere = File.Exists(Path.Combine(film, "assets", "stock", "s01", "clip1.mp4")) && File.Exists(Path.Combine(film, "assets", "stock", "s02", "img5.jpg"));
            sheetThere = File.Exists(Path.Combine(edit, "stock_review", "s01.jpg"));
            // Claude kabi: tanlov va bitta sahifa tanlangan klip bilan.
            File.WriteAllText(Path.Combine(film, "media_selection.json"),
                "{\"scenes\":[{\"scene\":\"s01\",\"chosen\":[{\"file\":\"assets/stock/s01/clip2.mp4\",\"reason\":\"sharp, exact subject\"}]," +
                "\"rejected\":[{\"file\":\"film/assets/stock/s01/img1.jpg\",\"reason\":\"watermark\"},{\"file\":\"./assets/stock/s01/img2.jpg\",\"reason\":\"blurry\"}]}]}");
            File.WriteAllText(Path.Combine(film, "a.html"), "<video src=\"assets/stock/s01/clip2.mp4#t=1,4\"></video>");
            return Task.CompletedTask;
        };
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);

        Assert.Equal(1, stock.Calls);
        Assert.Equal(new[] { "s01", "s02" }, stock.Requests.Select(q => q.Id));
        Assert.Equal(new StockOptions(5, 3, "landscape"), stock.LastOptions);
        // Fotolar OpenFilm'dan: ochiq manbalarga faqat logo qoldi.
        Assert.Equal(new[] { "logo" }, lib.Plans.Single().Items.Select(i => i.Kind));

        Assert.True(filesThere);
        Assert.True(sheetThere);
        var ins = h.Editor.LastInstructions!;
        Assert.Contains("## Stock footage and photos", ins);
        Assert.Contains("- V1 `film/assets/stock/s01/clip1.mp4` (video, 1920x1080, 6.1 s, 0.0 MB, pexels)", ins); // FakeMedia davomiylikka +0.05 qo'shadi
        Assert.Contains("- I5 `film/assets/stock/s02/img5.jpg` (image, 1920x1080", ins);
        Assert.Contains("Contact sheet: `stock_review/s01.jpg`", ins);
        Assert.Contains("film/media_selection.json", ins);
        Assert.Contains("watermark", ins);
        Assert.Contains("muted", ins);
        Assert.Contains("film/assets/media/logo_00.svg", ins); // logo bo'limi ham bor

        // Kontakt varaq: 5 rasm (1-qator) + 3 video x 3 kadr.
        var sheet = h.Media.Sheets.First();
        Assert.Equal(14, sheet.Count);
        Assert.Equal(5, sheet.Count(t => t.Row == 0));
        Assert.Contains(sheet, t => t.Label.StartsWith("V3 6s 1920x1080") && t.Row == 3 && t.Col == 0);

        Assert.Contains(h.Log.Lines, l => l.Contains("Stok tanlovi (Claude): 16 nomzoddan 1 tasi tanlandi, 2 tasi rad etildi, videoda 1 tasi ishlatilgan") && l.Contains("watermark"));
    }

    [Fact]
    public async Task SceneWithNoCandidates_FallsBackToOpenSourcePhoto()
    {
        var (h, stock, lib, _) = Setup();
        using var _h = h;
        stock.NotFound.Add("server room");
        await h.Run();
        var items = lib.Plans.Single().Items;
        Assert.Contains(items, i => i.Kind == "photo" && i.Query == "server room");
        Assert.DoesNotContain(items, i => i.Query == "card terminal");
        Assert.DoesNotContain("### s02", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task NoKeys_OldFlow_AndHintInLog()
    {
        var (h, stock, lib, _) = Setup();
        using var _h = h;
        stock.Configured = false;
        await h.Run();
        Assert.Equal(0, stock.Calls);
        Assert.Equal(2, lib.Plans.Single().Items.Count(i => i.Kind == "photo"));
        Assert.DoesNotContain("## Stock footage", h.Editor.LastInstructions);
        Assert.Contains(h.Log.Lines, l => l.Contains("Pexels yoki Pixabay kalitini kiriting"));
    }

    [Fact]
    public async Task SettingOff_NoStockSearch()
    {
        var (h, stock, _, _) = Setup();
        using var _h = h;
        h.Settings.S.Visuals.OpenFilmStock = false;
        await h.Run();
        Assert.Equal(0, stock.Calls);
    }

    [Fact]
    public async Task FixCycle_ReusesCandidates_NoSecondSearch()
    {
        var (h, stock, _, vis) = Setup();
        using var _h = h;
        h.Editor.GappyCalls = 1; // birinchi montajda bo'shliq - tuzatish sikli
        await h.Run();
        Assert.True(h.Editor.Calls >= 2);
        Assert.Equal(1, stock.Calls);
        Assert.Equal(1, vis.PlanCalls);
        Assert.Contains("## Stock footage", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task BrokenCandidate_IsDropped_CountsAndVerticalOrientation()
    {
        var (h, stock, _, _) = Setup();
        using var _h = h;
        stock.AddBroken = true;
        h.Settings.S.Visuals.StockImagesPerScene = 2; h.Settings.S.Visuals.StockClipsPerScene = 1; h.Settings.S.Visuals.MaxStockScenes = 1;
        var j = h.Store.ListByStatus(JobStatus.Queued).Single(); j.Format = VideoFormat.Shorts9x16; h.Store.Update(j);
        await h.Run();
        Assert.Equal(new StockOptions(2, 1, "portrait"), stock.LastOptions);
        Assert.Single(stock.Requests);
        var ins = h.Editor.LastInstructions!;
        Assert.Contains("film/assets/stock/s01/img2.jpg", ins);
        Assert.DoesNotContain("img3", ins); // buzilgan fayl ro'yxatga kirmadi
        Assert.Contains(h.Log.Lines, l => l.Contains("1/1 sahna, 2 rasm, 1 video"));
    }

    [Fact]
    public async Task MissingSelectionFile_OnlyWarns()
    {
        var (h, _, _, _) = Setup();
        using var _h = h;
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Contains(h.Log.Lines, l => l.StartsWith("W ") && l.Contains("media_selection.json yozmadi"));
    }

    [Fact]
    public void BotConfig_ReadsStockSettings_ClampedToOpenFilmLimits()
    {
        using var t = new TempDir();
        var db = new YtAuto.Infrastructure.Data.SqliteJobStore(Path.Combine(t.Path, "db.sqlite"));
        db.SetSetting(YtAuto.Infrastructure.Data.SqliteSettingsStore.ConfigKey,
            """{"visuals":{"openFilmStock":false,"stockImages":12,"stockClips":2,"stockScenes":6,"pexelsApiKey":" pk "}}""");
        var v = new YtAuto.Infrastructure.Data.SqliteSettingsStore(db, () => new AppSettings(), db).Load().Visuals;
        Assert.Equal((false, 8, 2, 6, "pk"), (v.OpenFilmStock, v.StockImagesPerScene, v.StockClipsPerScene, v.MaxStockScenes, v.PexelsApiKey));
        var d = new AppSettings().Visuals;
        Assert.Equal((true, 5, 3, 8), (d.OpenFilmStock, d.StockImagesPerScene, d.StockClipsPerScene, d.MaxStockScenes));
    }

    // ------------------------------------------------------------ OpenFilm javobi va yordamchilar

    [Fact]
    public void ParseOutput_ReadsOpenFilmImageAndVideoJson()
    {
        var img = "Starting OpenFilm Studio…\n{\"provider\":\"pexels\",\"images\":[{\"src\":\"assets/image/s01_pexels_img.jpg\",\"width\":1880,\"height\":1253,\"source\":\"web\",\"credit\":\"Photo by A on Pexels\",\"page\":\"https://www.pexels.com/photo/1\"}],\"asked\":5,\"landed\":1}";
        var a = OpenFilmStockSearch.ParseOutput(img, StockTypes.Image, "pexels").Single();
        Assert.Equal(("assets/image/s01_pexels_img.jpg", 1880, 1253, "Photo by A on Pexels"), (a.File, a.Width, a.Height, a.Credit));

        var vid = "{\"provider\":\"pixabay\",\"clips\":[{\"src\":\"assets/video/x.mp4\",\"dur\":12.5,\"w\":1920,\"h\":1080,\"credit\":\"Video by B on Pixabay\",\"page\":\"p\"},{\"src\":\"\"}]}";
        var v = OpenFilmStockSearch.ParseOutput(vid, StockTypes.Video, "pexels").Single();
        Assert.Equal(("pixabay", 12.5, 1920, StockTypes.Video), (v.Provider, v.Duration, v.Width, v.Type));

        Assert.Empty(OpenFilmStockSearch.ParseOutput("not json", StockTypes.Image, "pexels"));
        Assert.Empty(OpenFilmStockSearch.ParseOutput("{\"images\":5}", StockTypes.Image, "pexels"));
    }

    [Theory]
    [InlineData("Pexels said 401 Unauthorized", true)]
    [InlineData("Pixabay did not accept your key. Check it in Studio", true)]
    [InlineData("There is no Pexels key: add one in Studio", true)]
    [InlineData("429 Too Many Requests", true)]
    [InlineData("Pexels found no pictures for \"x\". Try other words.", false)]
    public void ProviderRefusal_DisablesOnlyOnKeyOrLimitErrors(string msg, bool refused) =>
        Assert.Equal(refused, OpenFilmStockSearch.IsProviderRefusal(msg));

    [Fact]
    public void Providers_PexelsFirst_OnlyWithKeys()
    {
        Assert.Equal(new[] { "pexels", "pixabay" }, OpenFilmStockSearch.Providers(new StockPhotoKeys("a", "b")));
        Assert.Equal(new[] { "pixabay" }, OpenFilmStockSearch.Providers(new StockPhotoKeys(" ", "b")));
        Assert.Empty(OpenFilmStockSearch.Providers(StockPhotoKeys.None));
    }

    [Fact]
    public void Selection_ParsesLooselyAndNormalisesPaths()
    {
        var s = StockSelection.TryParse("{ // izoh\n\"Scenes\":[{\"scene\":\"s01\",\"chosen\":[{\"file\":\"film\\\\assets\\\\stock\\\\s01\\\\img1.jpg\"}],\"rejected\":[],},]}")!;
        Assert.Equal(new[] { "assets/stock/s01/img1.jpg" }, s.ChosenFiles);
        Assert.Null(StockSelection.TryParse("{broken"));
    }

    [Fact]
    public void StockReferences_FindsFilesUsedInPages_IgnoresAssetsAndCache()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "film.html"), "<video src=\"assets/stock/s01/clip1.mp4#t=0,3\"></video><img src='assets/stock/s02/img4.jpg'>");
        Directory.CreateDirectory(Path.Combine(t.Path, "pages"));
        File.WriteAllText(Path.Combine(t.Path, "pages", "a.css"), ".bg{background:url(../assets/stock/s03/img1.png)}");
        Directory.CreateDirectory(Path.Combine(t.Path, ".film", "cache"));
        File.WriteAllText(Path.Combine(t.Path, ".film", "cache", "x.html"), "assets/stock/s09/img1.jpg");
        var refs = JobRunner.StockReferences(t.Path);
        Assert.Equal(new[] { "assets/stock/s01/clip1.mp4", "assets/stock/s02/img4.jpg", "assets/stock/s03/img1.png" }, refs.OrderBy(x => x));
    }

    [Fact]
    public void ContactSheetFilter_PlacesTilesAndSanitisesLabels()
    {
        var tiles = new[] { new SheetTile("a.jpg", "I1 1920x1080", 0, 0), new SheetTile("b.jpg", "V1 'end':;", 1, 2) };
        var f = FfmpegMediaProbe.ContactSheetFilter(tiles, 384, 216, @"C:\Windows\Fonts\arial.ttf");
        Assert.Contains("color=c=0x101010:s=1152x432", f);
        Assert.Contains("overlay=768:216", f);
        Assert.Contains("fontfile='C\\:/Windows/Fonts/arial.ttf'", f);
        Assert.Contains("text='V1  end'", f);
        Assert.DoesNotContain("drawtext", FfmpegMediaProbe.ContactSheetFilter(tiles, 384, 216, null));
    }
}
