using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;
using YtAuto.Infrastructure.Media;
using Xunit;

namespace YtAuto.Tests;

internal sealed class FakeVisuals : IVisualDirector
{
    public int GuideCalls, PlanCalls, ReviewCalls, MotionCalls;
    public double Score = 9;
    public IReadOnlyList<MotionClip> LastClips = Array.Empty<MotionClip>();
    public double LastFps;
    /// <summary>Chaqirilgan paytda klip fayllari mavjud edimi (keyin o'chiriladi).</summary>
    public bool ClipsExisted;
    public Task<string> TemplateMotionGuideAsync(IReadOnlyList<MotionClip> clips, double fps, string templateName, string notes, IJobLog log, CancellationToken ct)
    {
        MotionCalls++; LastClips = clips; LastFps = fps; ClipsExisted = clips.All(c => File.Exists(c.Path));
        return Task.FromResult("# Motion\n## 2. Scene transitions\n- push left, 0.4 s, cubic-bezier(0.65,0,0.35,1)\n" + new string('m', 300));
    }
    public MediaPlan Plan = new() { Items = { new() { Kind = "logo", Query = "Visa", Purpose = "card brand", At = 1 }, new() { Kind = "photo", Query = "card terminal", Purpose = "payment", At = 3 } } };
    public Task<string> TemplateStyleGuideAsync(IReadOnlyList<string> framePaths, string templateName, string notes, IJobLog log, CancellationToken ct)
    { GuideCalls++; return Task.FromResult("# Style\n## 1. Palette\n- background #0b1020\n- accent #facc15\n" + new string('x', 300)); }
    public Task<MediaPlan> PlanMediaAsync(string subtitleText, IReadOnlyList<SubtitleCue> cues, string channelContext, int maxItems, IJobLog log, CancellationToken ct)
    { PlanCalls++; return Task.FromResult(Plan.Clean(maxItems)); }
    public Task<StyleReview> ReviewAgainstTemplateAsync(IReadOnlyList<string> templateFrames, IReadOnlyList<string> videoFrames, string styleGuide, IJobLog log, CancellationToken ct)
    { ReviewCalls++; return Task.FromResult(new StyleReview { Score = Score, Summary = "s", Problems = Score < 6 ? new() { "2.0 s: headline colour is blue, template uses #facc15" } : new() }); }
}

internal sealed class FakeMediaLib : IMediaLibrary
{
    public int Calls;
    public Task<MediaManifest> FetchAsync(MediaPlan plan, string mediaDir, IJobLog log, CancellationToken ct)
    {
        Calls++;
        var m = new MediaManifest();
        foreach (var (i, n) in plan.Items.Select((i, n) => (i, n)))
        {
            var file = $"{i.Kind}_{n:00}.{(i.Kind == "photo" ? "jpg" : "svg")}";
            File.WriteAllText(Path.Combine(mediaDir, file), "x");
            m.Assets.Add(new MediaAsset
            {
                File = file, Kind = i.Kind, Title = i.Query, Author = i.Kind == "photo" ? "Jane Doe" : "", SourceUrl = "https://example.org/" + n,
                License = i.Kind == "photo" ? "CC BY 4.0" : "CC0", NeedsCredit = i.Kind == "photo",
            });
        }
        return Task.FromResult(m);
    }
}

public class VisualsTests
{
    private static Harness WithTemplate(Harness h)
    {
        var mp4 = Path.Combine(h.Tmp.Path, "tpl.mp4");
        File.WriteAllBytes(mp4, BitConverter.GetBytes(30.0));
        h.Settings.S.Paths.TemplatesDir = Path.Combine(h.Tmp.Path, "templates");
        h.Settings.S.Templates.Add(new TemplateDefinition { Name = "Brand", Kind = TemplateKind.Mp4Reference, Path = mp4 });
        h.Visuals = new FakeVisuals(); h.MediaLib = new FakeMediaLib();
        var j = h.AddJob(); j.TemplateName = "Brand"; h.Store.Update(j);
        return h;
    }

    [Fact]
    public async Task Template_OutranksChannelStyleFromOlderVideos()
    {
        using var h = WithTemplate(new Harness());
        h.Settings.S.ChannelContext = new ChannelContextSettings { Enabled = true, Montage = "MONTAGE-STYLE: dark navy 2D" };
        await h.Run();
        var ins = h.Editor.LastInstructions!;
        Assert.Contains(EditorInstructions.MontageHeadingBelowTemplate, ins);
        Assert.Contains(EditorInstructions.TemplatePriorityRule, ins);
        Assert.DoesNotContain(EditorInstructions.MontageHeading, ins);
        Assert.Contains("MONTAGE-STYLE: dark navy 2D", ins); // kanal odatlari yo'qolmaydi, faqat ikkinchi darajali
        Assert.Contains("Never let the channel's older videos override the template.", ins);
    }

    [Fact]
    public async Task Pipeline_GivesClaudeStyleGuideFramesAndLicensedMedia_AndCreditsGoToDescription()
    {
        using var h = WithTemplate(new Harness());
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);

        var ins = h.Editor.LastInstructions!;
        Assert.Contains("reference/STYLE_GUIDE.md", ins);
        Assert.Contains("## Template fidelity", ins);
        Assert.Contains("reference/style_frame_01.jpg", ins);
        Assert.Contains("## Real images, logos and icons", ins);
        Assert.Contains("film/assets/media/logo_00.svg", ins);
        Assert.Contains("film/assets/media/photo_01.jpg", ins);

        Assert.Contains("Image credits:", h.Uploader.Last!.Description);
        Assert.Contains("card terminal by Jane Doe, CC BY 4.0 (https://example.org/1)", h.Uploader.Last.Description);
        Assert.DoesNotContain("Visa by", h.Uploader.Last.Description); // CC0 logo - muallif talab qilinmaydi
        Assert.Equal(1, ((FakeVisuals)h.Visuals!).ReviewCalls);
    }

    [Fact]
    public async Task StyleGuide_IsCachedPerTemplate_NotReanalysedForNextVideo()
    {
        using var h = WithTemplate(new Harness());
        await h.Run();
        var j2 = h.AddJob("Second topic"); j2.TemplateName = "Brand"; h.Store.Update(j2);
        await h.Run();
        Assert.Equal(1, ((FakeVisuals)h.Visuals!).GuideCalls);
    }

    [Fact]
    public async Task LowStyleScore_SendsVideoBackToClaudeWithProblems()
    {
        using var h = WithTemplate(new Harness());
        ((FakeVisuals)h.Visuals!).Score = 4;
        await h.Run();
        Assert.True(h.Editor.Calls >= 2); // tuzatish sikli
        Assert.Contains(h.Editor.Feedbacks, f => f.Contains("headline colour is blue") && f.Contains("STYLE_GUIDE.md"));
        Assert.Equal(1, ((FakeMediaLib)h.MediaLib!).Calls); // tuzatishda rasmlar qayta yuklanmaydi (kesh)
    }

    [Fact]
    public async Task RealMediaOff_NoMediaSection()
    {
        using var h = WithTemplate(new Harness());
        h.Settings.S.Visuals.RealMedia = false;
        await h.Run();
        Assert.DoesNotContain("## Real images", h.Editor.LastInstructions);
        Assert.Equal(0, ((FakeMediaLib)h.MediaLib!).Calls);
    }

    [Fact]
    public async Task VisualFailures_DoNotStopTheVideo()
    {
        using var h = WithTemplate(new Harness());
        h.Visuals = new ThrowingVisuals();
        var r = await h.Run();
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Contains("reference/style_frame_01.jpg", h.Editor.LastInstructions); // kadrlar baribir beriladi
    }

    [Fact]
    public async Task MotionGuide_FromVideoClip_GoesToClaude_AndIsCachedPerTemplate()
    {
        using var h = WithTemplate(new Harness());
        var v = (FakeVisuals)h.Visuals!;
        Assert.Equal(RunOutcome.Completed, (await h.Run()).Outcome);

        Assert.Equal(1, v.MotionCalls);
        var clip = Assert.Single(v.LastClips);               // 30 s shablon - butun video bitta klip
        Assert.Equal(0, clip.StartSec);
        Assert.True(v.ClipsExisted);
        Assert.False(File.Exists(clip.Path));                // vaqtinchalik klip o'chirildi
        Assert.Equal(5, v.LastFps);
        Assert.Contains(h.Media.Clips, c => c.Width == 640);

        var ins = h.Editor.LastInstructions!;
        Assert.Contains("reference/MOTION_GUIDE.md", ins);
        Assert.Contains("Move like the template", ins);
        Assert.Contains("the timings and styles in `reference/MOTION_GUIDE.md` replace the defaults", ins);

        var j2 = h.AddJob("Second topic"); j2.TemplateName = "Brand"; h.Store.Update(j2);
        await h.Run();
        Assert.Equal(1, v.MotionCalls);                      // ikkinchi videoda qayta tahlil qilinmaydi
        Assert.Contains("reference/MOTION_GUIDE.md", h.Editor.LastInstructions);
    }

    [Fact]
    public async Task MotionGuideOff_NoVideoAnalysis()
    {
        using var h = WithTemplate(new Harness());
        h.Settings.S.Visuals.TemplateMotionGuide = false;
        await h.Run();
        Assert.Equal(0, ((FakeVisuals)h.Visuals!).MotionCalls);
        Assert.Empty(h.Media.Clips);
        Assert.DoesNotContain("reference/MOTION_GUIDE.md", h.Editor.LastInstructions);
        Assert.Contains("reference/STYLE_GUIDE.md", h.Editor.LastInstructions); // kadr tahlili ishlayveradi
    }

    [Theory]
    [InlineData(60, 1, 5)]     // butun video, 5 kadr/s = 300 kadr
    [InlineData(150, 1, 3)]    // 450 / 150
    [InlineData(600, 2, 3)]    // boshidan 75 s + o'rtasidan 75 s
    [InlineData(1, 0, 0)]      // juda qisqa - tahlil yo'q
    public void MotionPlan_BoundsFramesAndTokens(double duration, int segments, double fps)
    {
        var (segs, f) = JobRunner.MotionPlan(duration);
        Assert.Equal(segments, segs.Count);
        Assert.Equal(fps, f);
        Assert.True(segs.Sum(s => s.Duration) * f <= JobRunner.MotionMaxFrames);
        if (segments == 2) Assert.Equal((262.5, 75.0), segs[1]);
    }

    private sealed class ThrowingVisuals : IVisualDirector
    {
        public Task<string> TemplateStyleGuideAsync(IReadOnlyList<string> f, string n, string notes, IJobLog log, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<string> TemplateMotionGuideAsync(IReadOnlyList<MotionClip> c, double fps, string n, string notes, IJobLog log, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<MediaPlan> PlanMediaAsync(string s, IReadOnlyList<SubtitleCue> c, string ch, int m, IJobLog log, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<StyleReview> ReviewAgainstTemplateAsync(IReadOnlyList<string> a, IReadOnlyList<string> b, string g, IJobLog log, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    // ---------------------------------------------------------------- birlik testlari

    [Fact]
    public void MediaPlan_Clean_DropsBadKindsDuplicatesAndCaps()
    {
        var p = new MediaPlan
        {
            Items =
            {
                new() { Kind = "Logo", Query = " Visa " }, new() { Kind = "logo", Query = "visa" }, new() { Kind = "video", Query = "x y" },
                new() { Kind = "icon", Query = "" }, new() { Kind = "icon", Query = "lock" }, new() { Kind = "photo", Query = "server room" },
            },
        }.Clean(2);
        Assert.Equal(new[] { "logo:Visa", "icon:lock" }, p.Items.Select(i => $"{i.Kind}:{i.Query}"));
    }

    [Theory]
    [InlineData("CC0", "CC0", false)]
    [InlineData("Public domain", "Public domain", false)]
    [InlineData("PD-US", "Public domain", false)]
    [InlineData("CC BY 4.0", "CC BY 4.0", true)]
    [InlineData("CC BY 2.0", "CC BY 2.0", true)]
    public void Wikimedia_AllowedLicenses(string shortName, string expected, bool credit)
    {
        var l = OpenMediaLibrary.WikimediaLicense(shortName);
        Assert.NotNull(l);
        Assert.Equal((expected, credit), l!.Value);
    }

    [Theory]
    [InlineData("CC BY-SA 4.0")]
    [InlineData("CC BY-NC 2.0")]
    [InlineData("CC BY-ND 3.0")]
    [InlineData("GFDL")]
    [InlineData("Fair use")]
    public void Wikimedia_RejectsRestrictiveLicenses(string shortName) => Assert.Null(OpenMediaLibrary.WikimediaLicense(shortName));

    [Fact]
    public void IconSets_OnlyPermissive()
    {
        Assert.True(OpenMediaLibrary.IconSetAllowed("MIT"));
        Assert.True(OpenMediaLibrary.IconSetAllowed("CC0-1.0"));
        Assert.False(OpenMediaLibrary.IconSetAllowed("GPL-3.0"));
        Assert.False(OpenMediaLibrary.IconSetAllowed("CC-BY-SA-4.0"));
    }

    [Fact]
    public void Svg_IsSanitized()
    {
        var s = OpenMediaLibrary.SanitizeSvg("<svg onload=\"alert(1)\"><script>x()</script><a href=\"https://evil\"><path d=\"M0\"/></a><foreignObject><b/></foreignObject></svg>");
        Assert.DoesNotContain("script", s);
        Assert.DoesNotContain("onload", s);
        Assert.DoesNotContain("evil", s);
        Assert.DoesNotContain("foreignObject", s);
        Assert.Contains("<path d=\"M0\"/>", s);
    }

    [Fact]
    public void ImageType_FromBytesNotHeaders()
    {
        Assert.Equal(".jpg", OpenMediaLibrary.ImageExtension(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0 }));
        Assert.Equal(".png", OpenMediaLibrary.ImageExtension(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }));
        Assert.Null(OpenMediaLibrary.ImageExtension(System.Text.Encoding.ASCII.GetBytes("<html>not an image</html>")));
    }

    [Fact]
    public void Description_KeepsCreditsWhenTrimming()
    {
        var credits = "Image credits:\n- a by b, CC BY 4.0 (https://x)";
        var d = JobRunner.ComposeDescription(new string('d', 6000), credits, "Subscribe!");
        Assert.True(d.Length <= 5000);
        Assert.EndsWith(credits + "\n\nSubscribe!", d);
        Assert.Equal("only", JobRunner.ComposeDescription("only", "", null));
    }
}
