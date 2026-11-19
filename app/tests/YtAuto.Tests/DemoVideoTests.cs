using System.Text.Json;
using YtAuto.Core;
using YtAuto.Core.Demo;
using YtAuto.Core.Domain;
using YtAuto.Core.Media;
using YtAuto.Core.Pipeline;

namespace YtAuto.Tests;

/// <summary>Gemini o'rniga: qoralama - ko'rsatmalar va takror bilan (optimallashtirish tozalaydi), yakuniy matn TestScript bilan bir xil so'zlar.</summary>
internal sealed class FakeDemoWriter : IDemoScriptWriter
{
    public int Drafts, Optimizes; public DemoScriptRequest? LastDraft; public DemoOptimizeRequest? LastOptimize;
    public Task<DemoStepResult<DemoDraft>> DraftAsync(DemoScriptRequest r, IJobLog log, CancellationToken ct)
    {
        Drafts++; LastDraft = r;
        var ids = r.Media.Select(m => m.Id).ToList();
        var d = new DemoDraft
        {
            YouTubeTitle = "Kettle Pro in 30 seconds", YouTubeDescription = "Demo of Kettle Pro.", Tags = new() { "a", "b", "c", "d", "e" },
            Scenes = new()
            {
                new() { MediaId = ids[0], Narration = "Scene 1: [zoom on dashboard] Water boils when it gets hot enough.", Focus = "temperature gauge" },
                new() { MediaId = ids[^1], Narration = "Bubbles of steam form at the bottom of the pot. They rise and burst at the surface.", OnScreenText = "Live view" },
                new() { MediaId = ids[0], Narration = "That is why boiling water looks busy.", Focus = "" },
            },
        };
        return Task.FromResult(new DemoStepResult<DemoDraft> { Value = d, Raw = JsonSerializer.Serialize(d), ModelUsed = "fake" });
    }
    public Task<DemoStepResult<List<DemoScene>>> OptimizeAsync(DemoOptimizeRequest r, IJobLog log, CancellationToken ct)
    {
        Optimizes++; LastOptimize = r;
        // Model ko'rsatmani to'liq olib tashlamagan bo'lsa ham deterministik tozalash ishlaydi.
        var scenes = r.Draft.Scenes.Select(s => new DemoScene { MediaId = s.MediaId, Narration = s.Narration, Focus = s.Focus, OnScreenText = s.OnScreenText }).ToList();
        return Task.FromResult(new DemoStepResult<List<DemoScene>> { Value = scenes, Raw = JsonSerializer.Serialize(scenes) });
    }
}

/// <summary>Storage o'rniga: FakeMedia probe formatidagi fayl (boshida davomiylik).</summary>
internal sealed class FakeDemoFetcher : IDemoMediaFetcher
{
    public readonly List<string> Fetched = new();
    public Task<string> FetchAsync(DemoMedia m, string outStem, IJobLog log, CancellationToken ct)
    {
        Fetched.Add(m.Id);
        var path = outStem + (m.IsVideo ? ".mp4" : ".png");
        var bytes = new byte[256]; BitConverter.GetBytes(m.IsVideo ? 6.0 : 0.0).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return Task.FromResult(path);
    }
}

internal sealed class FakeResultSink : IDemoResultSink
{
    public int Calls; public string? Video; public string? Cover; public bool Fail;
    public Task<DemoResult> DeliverAsync(VideoJobRef job, string videoPath, string? coverPath, string? subtitlesPath, IJobLog log, CancellationToken ct)
    {
        Calls++; Video = videoPath; Cover = coverPath;
        if (Fail) throw PipelineException.Config("demo_result_rejected", "storage down");
        Assert.True(File.Exists(videoPath));
        return Task.FromResult(new DemoResult { VideoPath = $"acc/x/demo-result/job-{job.Id}/video.mp4", SizeBytes = new FileInfo(videoPath).Length, DeliveredAtUtc = DateTimeOffset.UtcNow });
    }
}

public class DemoVideoTests
{
    private static (Harness H, FakeDemoWriter W, FakeDemoFetcher F, FakeResultSink R) Make()
    {
        var h = new Harness();
        var w = new FakeDemoWriter(); var f = new FakeDemoFetcher(); var r = new FakeResultSink();
        h.DemoWriter = w; h.DemoMedia = f; h.DemoResults = r;
        return (h, w, f, r);
    }

    private static VideoJob AddDemo(Harness h, DemoPublish? publish = null)
    {
        var spec = new DemoSpec
        {
            Project = new DemoProject { Name = "Kettle Pro", Summary = "A smart kettle dashboard that shows water temperature live.", Url = "https://kettle.example",
                Features = { new DemoFeature { Name = "Live gauge", Description = "Shows temperature" } } },
            Media =
            {
                new DemoMedia { Id = "aaaaaaaaaaaaaaaa/01.png", Kind = DemoMediaKinds.Screenshot, Title = "Dashboard", Description = "Main dashboard with gauge", Highlight = "gauge", Url = "https://x.supabase.co/a?token=1" },
                new DemoMedia { Id = "aaaaaaaaaaaaaaaa/02.mp4", Kind = DemoMediaKinds.Video, Title = "Boil", Description = "User presses Boil and the chart rises", Url = "https://x.supabase.co/b?token=1" },
            },
            Requirements = new DemoRequirements { Language = "en", DurationSec = 12, Audience = "home cooks" },
            Publish = publish ?? new DemoPublish(),
        };
        var j = new VideoJob
        {
            Topic = "Kettle Pro", Language = "en", TargetDurationSec = 12, VoiceProfileName = "v", Format = VideoFormat.Landscape16x9,
            PublishAtUtc = h.Clock.UtcNow, Status = JobStatus.Queued, CreatedAtUtc = h.Clock.UtcNow.AddMinutes(-1),
            Kind = VideoJob.DemoKind, DemoSpecJson = spec.ToJson(),
        };
        h.Store.Insert(j);
        return j;
    }

    [Fact]
    public async Task DemoJob_RunsWholePipeline_StopsAtReady_WithoutYouTube()
    {
        var (h, w, f, r) = Make();
        using var _ = h;
        var job = AddDemo(h);
        var normal = h.AddJob("Unrelated topic"); // oddiy mavzu demo navbatidan olinmaydi

        var run = await h.Runner().RunDemoOnceAsync("run-demo", CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, run.Outcome);
        Assert.Equal(job.Id, run.JobId);

        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Ready, j.Status);
        Assert.Null(j.YouTubeVideoId);
        Assert.Equal(0, h.Uploader.Initiations);
        Assert.Equal("Kettle Pro in 30 seconds", j.Title);
        Assert.True(File.Exists(j.VideoArchivePath));
        // Ikki bosqich, ikkalasi ham chaqirildi; Gemini materiallarning kadrlarini oldi.
        Assert.Equal(1, w.Drafts); Assert.Equal(1, w.Optimizes);
        Assert.Equal(2, w.LastDraft!.PreviewFiles.Count);
        Assert.Equal(3, w.LastDraft.PreviewFiles["aaaaaaaaaaaaaaaa/02.mp4"].Count); // video: 3 kadr
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaa/01.png", "aaaaaaaaaaaaaaaa/02.mp4" }, f.Fetched);
        // Ovozga faqat aytiladigan matn ketdi.
        var rec = Path.Combine(h.Paths.RecordsDir, $"job-{job.Id}");
        var script = YtAuto.Core.Script.VideoScript.FromJson(File.ReadAllText(Path.Combine(rec, "script.json")));
        Assert.DoesNotContain("Scene", script.SubtitleText);
        Assert.DoesNotContain("[", script.SubtitleText);
        Assert.Equal(TextNormalizer.Tokenize(TestScript.Narration).Count, TextNormalizer.Tokenize(script.SubtitleText).Count);
        // Montaj: materiallar, SFX va sahna rejasi (Claude ishlagan paytda film/ ichida edi).
        Assert.Equal(new[] { "assets/demo/01.png", "assets/demo/02.mp4", "assets/sfx/click.wav", "assets/sfx/pop.wav", "assets/sfx/whoosh.wav" }, h.Editor.SeenDemoAssets);
        var plan = JsonSerializer.Deserialize<List<DemoPlanScene>>(File.ReadAllText(Path.Combine(rec, "demo_plan.json")))!;
        Assert.Equal(3, plan.Count);
        Assert.Equal(0, plan[0].Start);
        Assert.Equal(plan[0].End, plan[1].Start);
        // Tayyor: vaqtinchalik ish papkasi tozalandi, arxiv qoldi.
        Assert.True(j.WorkDirCleaned);
        Assert.False(Directory.Exists(j.WorkDir));
        Assert.Contains("## Product demo", h.Editor.LastInstructions);
        Assert.Contains("film/assets/demo/02.mp4", h.Editor.LastInstructions);
        Assert.Contains("temperature gauge", h.Editor.LastInstructions);
        Assert.Contains("THIRD audio track", h.Editor.LastInstructions);
        Assert.DoesNotContain("Group these lines into scenes by meaning (one idea per scene). Make one page", h.Editor.LastInstructions!.Split("## Product demo")[1]);
        // Natija serverga yuborildi va saqlandi.
        Assert.Equal(1, r.Calls);
        var res = JsonSerializer.Deserialize<DemoResult>(j.DemoResultJson!, DemoSpec.Json)!;
        Assert.Equal($"acc/x/demo-result/job-{job.Id}/video.mp4", res.VideoPath);
        Assert.Equal(j.VideoArchivePath, res.LocalPath);
        Assert.Null(res.Error);
        // "Tayyor" holati navbatdan olinmaydi; oddiy mavzu tegilmagan.
        Assert.Equal(RunOutcome.QueueEmpty, (await h.Runner().RunDemoOnceAsync("again", CancellationToken.None)).Outcome);
        Assert.Equal(JobStatus.Queued, h.Store.Get(normal.Id)!.Status);
    }

    [Fact]
    public async Task ReadyDemo_PublishedLater_ByUploadRequest_WithoutRebuilding()
    {
        var (h, w, _, _) = Make();
        using var _h = h;
        var job = AddDemo(h);
        await h.Runner().RunDemoOnceAsync("r1", CancellationToken.None);
        Assert.Equal(JobStatus.Ready, h.Store.Get(job.Id)!.Status);
        var renders = h.Film.Renders;

        // publish_video (rejalashtirilgan) = upload_requested; worker tayyor faylni yuklaydi.
        h.Store.SetUploadRequested(job.Id, true);
        var results = await h.Runner().RunUploadRequestsAsync("r2", CancellationToken.None);
        Assert.Single(results);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal("vid-1", j.YouTubeVideoId);
        Assert.Equal(1, h.Uploader.Initiations);
        Assert.Equal(renders, h.Film.Renders); // qayta render qilinmadi
        Assert.Equal(1, w.Drafts);
    }

    [Fact]
    public async Task DemoWithPublishRequested_UploadsPublicNow()
    {
        var (h, _, _, _) = Make();
        using var _h = h;
        h.PublishAllowed = () => true;
        var job = AddDemo(h, new DemoPublish { Requested = true, When = "now" });
        var run = await h.Runner().RunDemoOnceAsync("r", CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, run.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal("vid-1", j.YouTubeVideoId);
        Assert.True(h.Uploader.LastPublishNow);
        Assert.Equal(JobStatus.Published, j.Status);
    }

    [Fact]
    public async Task DemoWithPublishRequested_GuestScope_IsNotPublished()
    {
        var (h, _, _, _) = Make();
        using var _h = h;
        h.PublishAllowed = () => false; // mehmon (qo'shimcha token) hisobi
        var job = AddDemo(h, new DemoPublish { Requested = true, When = "now" });
        await h.Runner().RunDemoOnceAsync("r", CancellationToken.None);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Ready, j.Status);
        Assert.Equal(0, h.Uploader.Initiations);
        Assert.Contains(h.Log.Lines, l => l.Contains("ruxsat etilmagan"));
    }

    [Fact]
    public async Task ResultDeliveryFailure_DoesNotLoseVideo()
    {
        var (h, _, _, r) = Make();
        using var _h = h;
        r.Fail = true;
        var job = AddDemo(h);
        var run = await h.Runner().RunDemoOnceAsync("r", CancellationToken.None);
        Assert.Equal(RunOutcome.Completed, run.Outcome);
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Ready, j.Status);
        var res = JsonSerializer.Deserialize<DemoResult>(j.DemoResultJson!, DemoSpec.Json)!;
        Assert.Contains("storage down", res.Error);
        Assert.True(File.Exists(res.LocalPath));
    }

    [Fact]
    public async Task DemoJob_WithoutDemoServices_NeedsAttention()
    {
        using var h = new Harness();
        var job = AddDemo(h);
        var run = await h.Runner().RunDemoOnceAsync("r", CancellationToken.None);
        Assert.Equal(RunOutcome.NeedsAttention, run.Outcome);
        Assert.Contains("Demo video servislari", h.Store.Get(job.Id)!.AttentionReason);
    }

    // ------------------------------------------------------------------ qoidalar
    [Fact]
    public void CleanNarration_RemovesDirectionsLabelsAndUrls()
    {
        var t = DemoRules.CleanNarration("Narrator: **Meet** Kettle Pro. [Cut to dashboard] (zoom on the gauge) Visit https://k.example now.\nIt boils fast.");
        Assert.Equal("Meet Kettle Pro. Visit now. It boils fast.", t);
    }

    [Fact]
    public void CleanScenes_DropsRepeatsAndFixesUnknownMedia()
    {
        var notes = new List<string>();
        var scenes = DemoRules.CleanScenes(new[]
        {
            new DemoScene { MediaId = "m1", Narration = "Kettle Pro shows the temperature live. It is fast." },
            new DemoScene { MediaId = "ghost", Narration = "Kettle Pro shows the temperature live. You can start boiling with one tap." },
            new DemoScene { MediaId = "m2", Narration = "(show screen)" },
        }, new[] { "m1", "m2" }, notes);
        Assert.Equal(2, scenes.Count);
        Assert.Equal("m1", scenes[1].MediaId); // noma'lum id oldingi sahna materiali bilan
        Assert.Equal("You can start boiling with one tap.", scenes[1].Narration);
        Assert.Contains(notes, n => n.Contains("Takroriy"));
    }

    [Fact]
    public void SceneTimes_CoverWholeAudio_FromWordTimings()
    {
        var scenes = new List<DemoScene> { new() { Narration = "One two three." }, new() { Narration = "Four five." }, new() { Narration = "Six." } };
        var words = Enumerable.Range(0, 6).Select(i => new AlignedWord { Start = i, End = i + 0.6 }).ToList();
        var t = DemoRules.SceneTimes(scenes, words, 6.5);
        Assert.Equal(3, t.Count);
        Assert.Equal(0, t[0].Start);
        Assert.Equal(2.8, t[0].End, 2);   // 3-so'z oxiri 2.6, 4-so'z boshi 3.0 → o'rtasi
        Assert.Equal(t[0].End, t[1].Start);
        Assert.Equal(t[1].End, t[2].Start);
        Assert.Equal(6.5, t[2].End);
    }

    [Fact]
    public void ValidateScenes_RequiresMostMaterials()
    {
        var p = DemoRules.ValidateScenes(new[] { new DemoScene { MediaId = "a", Narration = "x" } }, new[] { "a", "b", "c", "d" });
        Assert.Contains(p, x => x.Contains("faqat 1 tasi"));
    }

    [Fact]
    public void Sfx_WritesValidShortWavs()
    {
        using var tmp = new TempDir();
        var files = Sfx.WriteAll(tmp.Path);
        Assert.Equal(3, files.Count);
        foreach (var f in files)
        {
            var info = Wav.ReadInfo(f);
            Assert.Equal(48000, info.SampleRate);
            Assert.InRange(info.Duration, 0.05, 0.5);
            Assert.InRange(Wav.Analyze(f, 0.01).PeakDb, -20, -3); // eshitiladi, lekin clipping yo'q
        }
    }

    [Fact]
    public void ReadyStatus_IsNotClaimable_AndCanBePublishedOrRequeued()
    {
        Assert.DoesNotContain(JobStatus.Ready, JobStateMachine.ClaimableStatuses);
        Assert.True(JobStateMachine.CanTransition(JobStatus.Archived, JobStatus.Ready));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Ready, JobStatus.Queued));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Ready, JobStatus.Cancelled));
        Assert.False(JobStateMachine.IsInProgress(JobStatus.Ready));
    }
}
