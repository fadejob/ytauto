using System.Net;
using System.Text.Json.Nodes;
using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Text;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Media;
using Xunit;

namespace YtAuto.Tests;

internal sealed class FakeProvider(string name, Func<StockRequest, IReadOnlyList<LiveHit>> hits) : ILiveProvider
{
    public string Name => name;
    public readonly List<string> Asked = new();
    public Task<IReadOnlyList<LiveHit>> SearchAsync(StockRequest req, int want, string orientation, CancellationToken ct)
    { Asked.Add(req.Id); return Task.FromResult<IReadOnlyList<LiveHit>>(hits(req).Take(want).ToList()); }
}

/// <summary>Har so'rovga URL bo'yicha javob: .glb/.gltf/.bin/.jpg - kichik fayl; "missing" bo'lsa 404.</summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    public readonly List<string> Urls = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var u = request.RequestUri!.ToString();
        Urls.Add(u);
        var code = u.Contains("missing") ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(new byte[2048]), RequestMessage = request });
    }
}

public class LiveAndCaptionsTests
{
    // ------------------------------------------------------------------ jonli manbalar
    private static LiveHit Photo(string id) => new("wikimedia", "photo", "Bee " + id, $"https://upload.wikimedia.org/{id}.jpg", "", 1920, 2560, false, "CC BY 4.0", "Jane", $"https://commons.wikimedia.org/wiki/File:{id}", true);
    private static LiveHit Icon(string id) => new("3dicons", "icon3d", "flower " + id, $"https://x.supabase.co/sizes/{id}/dynamic/400/color.webp", "", 400, 400, true, "CC0", "Vijay", "https://3dicons.co", false);
    private static LiveHit Model(string id) => new("polyhaven", "model", "Plant " + id, $"https://dl.polyhaven.org/{id}/model.gltf", $"https://cdn.polyhaven.com/{id}.png", 0, 0, true, "CC0", "PH", "https://polyhaven.com/a/" + id, false,
        "", new[] { ("model.bin", $"https://dl.polyhaven.org/{id}/model.bin"), ("textures/diff.jpg", $"https://dl.polyhaven.org/{id}/diff.jpg") });

    [Fact]
    public void Mix_SceneTypeDecidesPhotosIconsModels()
    {
        Assert.Equal((2, 7, 1), LiveAssetSource.Mix("collage", 3));
        Assert.Equal((3, 1, 1), LiveAssetSource.Mix("hero", 1));
        Assert.Equal((5, 1, 0), LiveAssetSource.Mix("explainer", 2));
        Assert.Equal((1, 1, 0), LiveAssetSource.Mix("typography", 0));
    }

    [Fact]
    public async Task Live_DownloadsPhotosIcons3dModelsWithFiles_CapsModels_NoDuplicates()
    {
        using var t = new TempDir();
        var web = new FakeWeb();
        var photos = new FakeProvider("wikimedia", r => new[] { Photo(r.Id + "a"), Photo(r.Id + "b"), Photo("shared"), Photo(r.Id + "c"), Photo(r.Id + "d"), Photo(r.Id + "e") });
        var icons = new FakeProvider("3dicons", r => Enumerable.Range(1, 9).Select(i => Icon(r.Id + i)).ToList());
        var models = new FakeProvider("polyhaven", r => new[] { Model(r.Id) });
        var src = new LiveAssetSource(new HttpClient(web), new[] { photos }, new[] { icons }, new[] { models }) { MaxModels = 1 };
        var reqs = new[] { new StockRequest("s01", "bee", "", 0, "collage", 3), new StockRequest("s02", "flower", "", 3, "hero", 1) };
        var r = await src.FetchAsync(reqs, t.Path, new StockOptions(5, 3, "portrait"), new NullLog(), CancellationToken.None);

        var s1 = r.Scenes[0];
        Assert.Single(s1.Candidates, x => x.Type == StockTypes.Model);          // kollajga bitta model
        Assert.Equal(7, s1.Candidates.Count(x => x.Kind == "icon3d"));
        Assert.Equal(2, s1.Candidates.Count(x => x.Kind == "photo"));
        var m = s1.Candidates.Single(x => x.Type == StockTypes.Model);
        Assert.True(File.Exists(m.File));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(m.File)!, "model.bin")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(m.File)!, "textures", "diff.jpg")));
        Assert.True(File.Exists(m.Preview));
        Assert.Equal("live:polyhaven", m.Provider);
        Assert.DoesNotContain(r.Scenes[1].Candidates, x => x.Type == StockTypes.Model); // video bo'yicha chegara: 1
        Assert.Single(r.Scenes.SelectMany(s => s.Candidates), x => x.File.Contains("live") && x.Title == "Bee shared"); // bir rasm ikki sahnada emas
        Assert.Equal("Bee s01a by Jane, CC BY 4.0 (https://commons.wikimedia.org/wiki/File:s01a)", s1.Candidates.First(x => x.Kind == "photo").Credit);
    }

    [Fact]
    public async Task Live_ModelPathTraversalAndBadHostsRejected()
    {
        using var t = new TempDir();
        var evil = new LiveHit("polyhaven", "model", "Evil", "https://dl.polyhaven.org/x/model.gltf", "", 0, 0, true, "CC0", "", "", false, "",
            new[] { ("../../escape.bin", "https://dl.polyhaven.org/x/a.bin") });
        var offsite = Photo("o") with { Url = "https://evil.example.com/a.jpg" };
        var src = new LiveAssetSource(new HttpClient(new FakeWeb()), new[] { new FakeProvider("w", _ => new[] { offsite }) }, Array.Empty<ILiveProvider>(),
            new[] { new FakeProvider("m", _ => new[] { evil }) });
        var r = await src.FetchAsync(new[] { new StockRequest("s01", "x", "", 0, "hero", 1) }, t.Path, new StockOptions(5, 3, ""), new NullLog(), CancellationToken.None);
        Assert.Empty(r.Scenes[0].Candidates);
        Assert.False(File.Exists(Path.Combine(t.Path, "escape.bin")));
    }

    [Fact]
    public void Live_AllowedHosts()
    {
        foreach (var ok in new[] { "https://upload.wikimedia.org/a.jpg", "https://images-assets.nasa.gov/a~large.jpg", "https://img.icons8.com/3d-fluency/500/sunflower.png",
                     "https://huggingface.co/datasets/allenai/objaverse/resolve/main/glbs/000-001/x.glb", "https://cas-bridge.xethub.hf.co/x", "https://dl.polyhaven.org/a.gltf",
                     "https://bvconuycpdvgzbvbkijl.supabase.co/storage/v1/object/public/sizes/x/dynamic/400/color.webp", "https://media.sketchfab.com/models/x/thumb.jpeg" })
            Assert.True(LiveAssetSource.Allowed(new Uri(ok)), ok);
        foreach (var bad in new[] { "http://upload.wikimedia.org/a.jpg", "https://evil-hf.co/x", "https://huggingface.co.evil.com/x", "https://example.com/a.jpg" })
            Assert.False(LiveAssetSource.Allowed(new Uri(bad)), bad);
    }

    [Fact]
    public void Wikimedia_LicenseAndPeopleFilters()
    {
        Assert.Equal(("CC BY 4.0", true), WikimediaLive.LicenseOf("cc-by-4.0", "CC BY 4.0", ""));
        Assert.Equal(("CC0", false), WikimediaLive.LicenseOf("cc0", "", ""));
        Assert.Equal(("Public domain", false), WikimediaLive.LicenseOf("pd", "", ""));
        Assert.Null(WikimediaLive.LicenseOf("cc-by-sa-4.0", "CC BY-SA 4.0", ""));
        Assert.Null(WikimediaLive.LicenseOf("", "GFDL 1.2", ""));
        JsonObject Page(string title, string cats, string lic = "cc0", int w = 1920, int h = 1280) => new()
        {
            ["title"] = $"File:{title}.jpg",
            ["imageinfo"] = new JsonArray(new JsonObject
            {
                ["mime"] = "image/jpeg", ["thumburl"] = "https://upload.wikimedia.org/t.jpg", ["thumbwidth"] = w, ["thumbheight"] = h, ["descriptionurl"] = "https://commons.wikimedia.org/x",
                ["extmetadata"] = new JsonObject { ["License"] = new JsonObject { ["value"] = lic }, ["Categories"] = new JsonObject { ["value"] = cats },
                    ["ObjectName"] = new JsonObject { ["value"] = title }, ["Artist"] = new JsonObject { ["value"] = "<a href='x'>Jane &amp; Co</a>" } },
            }),
        };
        var ok = WikimediaLive.ToHit(Page("Honey bee on lavender", "Apis mellifera"), true)!;
        Assert.Equal(("photo", "Jane & Co", "CC0"), (ok.Kind, ok.Author, ok.License));
        Assert.Null(WikimediaLive.ToHit(Page("Children in lavender field", "Lavender"), true));
        Assert.Null(WikimediaLive.ToHit(Page("Bee", "Bees", "cc-by-sa-3.0"), true));
        Assert.Null(WikimediaLive.ToHit(Page("Tiny bee", "Bees", w: 800, h: 600), true));
    }

    [Fact]
    public void Relevance_AllQueryWordsOrSpecificKeyword()
    {
        var bee = new StockRequest("s01", "honey bee", "", 0, "hero", 1, new[] { "Apis mellifera", "honey bee", "bee" });
        Assert.True(LiveAssetSource.Relevant("European honey bees on lavender", bee));
        Assert.True(LiveAssetSource.Relevant("Apis mellifera worker 2", bee));            // birinchi (ilmiy) kalit so'z
        Assert.False(LiveAssetSource.Relevant("Little bee-eater (Merops pusillus)", bee)); // umumiy "bee" yetmaydi
        var earth = new StockRequest("s06", "Earth from space", "", 0, "hero", 1, new[] { "Earth from space", "planet Earth" });
        Assert.False(LiveAssetSource.Relevant("The Lion nebula", earth));
        Assert.True(LiveAssetSource.Relevant("Planet Earth seen by Apollo 17", earth));
    }

    [Fact]
    public void ThreeDIcons_IndexParsedFromExplorePage()
    {
        const string html = "<img src=\"https://abc.supabase.co/storage/v1/object/public/sizes/0ef25b-calender/dynamic/200/color.webp\">" +
                            "<img src=\"https://abc.supabase.co/storage/v1/object/public/sizes/12ab34-flower-pot/dynamic/200/color.webp\">" +
                            "<img src=\"https://abc.supabase.co/storage/v1/object/public/sizes/12ab34-flower-pot/iso/200/clay.webp\">";
        var idx = ThreeDIconsLive.ParseIndex(html);
        Assert.Equal(2, idx.Count);
        Assert.Contains(idx, x => x.Name == "flower-pot" && x.Words.Contains("flower") && x.Base.EndsWith("/sizes/12ab34-flower-pot"));
        Assert.Equal("honey-bee", Icons8ThreeDLive.Slug(" Honey Bee! "));
    }

    // ------------------------------------------------------------------ subtitr
    private const string Vtt = "WEBVTT\n\n00:00:00.000 --> 00:00:00.940\nEvery <00:00:00.500>night\n\n" +
                               "00:00:01.540 --> 00:00:05.300\nat <00:00:01.660>one <00:00:02.240>o'clock, <00:00:03.280>a <00:00:03.460>small <00:00:03.840>program <00:00:04.500>wakes <00:00:04.920>up\n\n" +
                               "00:00:05.300 --> 00:00:06.380\non <00:00:05.520>this <00:00:05.760>computer.\n";

    [Fact]
    public void Captions_ParseWordTimes()
    {
        var w = Captions.ParseWords(Vtt);
        Assert.Equal(new[] { "Every", "night", "at", "one", "o'clock,", "a", "small", "program", "wakes", "up", "on", "this", "computer." }, w.Select(x => x.Text));
        Assert.Equal((0.0, 0.5), (w[0].Start, w[0].End));
        Assert.Equal((0.5, 0.94), (w[1].Start, w[1].End));     // cue'ning oxirgi so'zi cue tugaguncha
        Assert.Equal((4.92, 5.3), (w[9].Start, w[9].End));
    }

    [Fact]
    public void Captions_ChunkByWordsPunctuationAndPauses()
    {
        var chunks = Captions.Chunk(Captions.ParseWords(Vtt), 3, 30);
        Assert.Equal(new[] { "Every night", "at one o'clock,", "a small program", "wakes up on", "this computer." },
            chunks.Select(c => string.Join(' ', c.Words.Select(x => x.Text))));
        Assert.Equal(1.54, chunks[1].Start);
        Assert.True(chunks[0].End <= chunks[1].Start);       // pauzada keyingisigacha ko'rinmaydi (0.94 + 0.5)
        Assert.Equal(1.44, chunks[0].End, 3);
        Assert.All(chunks.Zip(chunks.Skip(1)), p => Assert.True(p.First.End <= p.Second.Start + 1e-9));
    }

    [Fact]
    public void Captions_PageAndTrack()
    {
        var chunks = Captions.Chunk(Captions.ParseWords(Vtt), 3, 30);
        var s = new SubtitleSettings { Style = "box", HighlightColor = "#00FF88", Uppercase = true, FontScale = 1.2 };
        var html = Captions.BuildPage(chunks, s, 1080, 1920, 6.4, VideoFormat.Shorts9x16);
        Assert.Contains("\"t\":\"O\\u0027CLOCK,\"", html);
        Assert.Contains("#00FF88", html);
        Assert.Contains("const D=6.4;", html);
        Assert.Contains("top:80%", html);
        Assert.DoesNotContain("Math.random", html);
        Assert.Contains("font-size:106px", html); // 1080 × 0.082 × 1.2
        var bad = Captions.BuildPage(chunks, new SubtitleSettings { Style = "weird", TextColor = "red;}" }, 1920, 1080, 6.4, VideoFormat.Landscape16x9);
        Assert.Contains("const STYLE=\"bold\"", bad);
        Assert.DoesNotContain("red;}", bad);

        const string film = "<!doctype html>\n<html><head></head><body>\n<section>\n  <iframe src=\"s1.html#t=0,3\" at=\"0\"></iframe>\n</section>\n</body></html>";
        var once = Captions.InsertTrack(film, 6.4);
        var twice = Captions.InsertTrack(once, 7);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(twice, "data-ytauto=")); // qayta qo'yilsa ham bitta yo'lak
        Assert.Contains("captions.html#t=0,7\"", twice);
        Assert.DoesNotContain("#t=0,6.4\"", twice);
        Assert.True(twice.IndexOf("captions.html", StringComparison.Ordinal) < twice.IndexOf("s1.html", StringComparison.Ordinal)); // eng yuqori yo'lak
        Assert.Equal(3, Captions.WordsPerChunk(new SubtitleSettings(), VideoFormat.Shorts9x16));
        Assert.Equal(5, Captions.WordsPerChunk(new SubtitleSettings(), VideoFormat.Landscape16x9));
        Assert.Equal(0.86, Captions.PositionOf(new SubtitleSettings(), VideoFormat.Landscape16x9));
    }

    [Fact]
    public void Kit_Has3dFiles()
    {
        var files = EditorKit.Files;
        foreach (var f in new[] { "kit.js", "kit.css", "README.md", "model3d.js", "three/three.module.min.js", "three/addons/loaders/GLTFLoader.js",
                     "three/addons/utils/BufferGeometryUtils.js", "three/addons/environments/RoomEnvironment.js", "three/LICENSE" })
            Assert.Contains(f, files);
        using var t = new TempDir();
        EditorKit.WriteTo(t.Path);
        Assert.True(new FileInfo(Path.Combine(t.Path, "kit", "three", "three.module.min.js")).Length > 500_000);
        var m3d = File.ReadAllText(Path.Combine(t.Path, "kit", "model3d.js"));
        Assert.Contains("preserveDrawingBuffer: true", m3d);
        Assert.Contains("URL.createObjectURL", m3d);
    }
}
