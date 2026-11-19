using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Tests;

/// <summary>Har videoda muqova (cover): Claude chizgan cover.html → YouTube thumbnail va MP4 ichidagi rasm; bo'lmasa zaxira kadr.</summary>
public class CoverTests
{
    [Fact]
    public async Task CoverHtml_BecomesThumbnail_AndIsEmbeddedInVideo()
    {
        using var h = new Harness();
        var job = h.AddJob();
        await h.Run();
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Scheduled, j.Status);
        Assert.Contains("cover.html 0", h.Film.LookArgs);                      // muqova sahifasi rasmga aylantirildi
        Assert.Contains(h.Media.FrameSources, s => s.EndsWith(".png"));         // look PNG → cover.jpg
        Assert.Equal(1, h.Media.Embeds);                                        // arxivdagi MP4 ichida muqova
        Assert.Single(h.Uploader.Thumbnails);                                   // YouTube thumbnail
        Assert.Contains("## Cover (required)", h.Editor.LastInstructions);
        Assert.Contains("film/cover.html", h.Editor.LastInstructions);
        Assert.Contains("Why water boils", h.Editor.LastInstructions);          // sarlavha muqova uchun beriladi
    }

    [Fact]
    public async Task NoCoverHtml_FallsBackToProjectFrame_StillHasCover()
    {
        using var h = new Harness();
        h.Editor.WriteCover = false;
        h.AddJob();
        await h.Run();
        Assert.DoesNotContain("cover.html 0", h.Film.LookArgs);
        Assert.Contains(h.Film.LookArgs, a => double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)); // loyiha kadri
        Assert.Equal(1, h.Media.Embeds);
        Assert.Single(h.Uploader.Thumbnails);
        Assert.Contains(h.Log.Lines, l => l.Contains("cover.html yaratmagan"));
    }

    [Fact]
    public void LookPng_ParsesPathsWithSpaces_AndRelativePaths()
    {
        using var tmp = new TempDir();
        var film = Path.Combine(tmp.Path, "my jobs", "film");
        var png = Path.Combine(film, ".film", "cache", "look", "cover 0.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, new byte[] { 1 });
        Assert.Equal(png, JobRunner.LookPng(film, new ToolRunResult { StdoutTail = $"Opening Studio…\nframe 0s: {png}\n" }));
        Assert.Equal(png, JobRunner.LookPng(film, new ToolRunResult { StdoutTail = ".film/cache/look/cover 0.png" }));
        // Chiqishda yo'l bo'lmasa - keshdagi eng yangi PNG.
        Assert.Equal(png, JobRunner.LookPng(film, new ToolRunResult { StdoutTail = "done" }));
    }

    [Fact]
    public void WithFilmHook_AddsFrameToStaticCoverOnly()
    {
        // Skriptsiz statik muqova: openfilm look "window.film ... did not appear" deb rad etardi.
        var fixedHtml = JobRunner.WithFilmHook("<html><body><div>Cover</div></body></html>");
        Assert.NotNull(fixedHtml);
        Assert.Contains("<script>window.film={duration:1,frame(){}}</script>\n</body>", fixedHtml);
        Assert.Null(JobRunner.WithFilmHook(fixedHtml!));
        Assert.Null(JobRunner.WithFilmHook("<body><script>window.film={duration:2,frame(t){}}</script></body>"));
        Assert.EndsWith("</script>\n", JobRunner.WithFilmHook("<div>no body tag</div>"));
    }
}
