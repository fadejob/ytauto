using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;

namespace YtAuto.Tests;

public class BatchPlannerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 3)]   // 7 dan ko'p bo'lsa ham 4-5 video qilinmaydi
    [InlineData(40, 3)]
    public void VideosPerRun_FollowsTopicCount(int pending, int expected) => Assert.Equal(expected, BatchPlanner.VideosFor(pending));
}

public class PublishNowTests
{
    [Fact]
    public async Task PublishNow_TakesPriority_AndUploadsPublicImmediately()
    {
        using var h = new Harness();
        var older = h.AddJob("older topic", minutesAgo: 60);
        var urgent = h.AddJob("urgent topic", minutesAgo: 5, publishInDays: 10);
        h.Store.SetPublishNow(urgent.Id, true);
        Assert.Equal(1, h.Store.CountPublishNow(h.Clock.UtcNow));

        var r = await h.Run();

        Assert.Equal(urgent.Id, r.JobId);
        Assert.True(r.PublishNow);
        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.True(h.Uploader.LastPublishNow);
        var j = h.Store.Get(urgent.Id)!;
        Assert.Equal(JobStatus.Published, j.Status);
        Assert.False(j.PublishNow);                                   // bayroq olib tashlandi
        Assert.True(j.PublishAtUtc <= h.Clock.UtcNow.AddMinutes(1));  // 10 kun kutilmadi
        Assert.Equal(JobStatus.Queued, h.Store.Get(older.Id)!.Status);
        Assert.Equal(0, h.Store.CountPublishNow(h.Clock.UtcNow));
    }

    [Fact]
    public async Task PublishNowOnly_DoesNotTouchNormalQueue()
    {
        using var h = new Harness();
        h.AddJob();
        var r = await h.Runner().RunOnceAsync("poll-1", publishNowOnly: true, CancellationToken.None);
        Assert.Equal(RunOutcome.QueueEmpty, r.Outcome);
        Assert.Equal(0, h.Script.Calls);
        Assert.Equal(1, h.Store.CountPending(h.Clock.UtcNow));
    }

    [Fact]
    public async Task UploadedButNotReleased_IsMadePublic_WithoutReupload()
    {
        using var h = new Harness();
        var job = h.AddJob();
        await h.Run();                                               // oddiy: private + publishAt
        Assert.Equal(JobStatus.Scheduled, h.Store.Get(job.Id)!.Status);
        var scriptCalls = h.Script.Calls;

        h.Store.SetPublishNow(job.Id, true);
        var r = await h.Run();

        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(1, h.Uploader.MakePublicCalls);
        Assert.Equal(1, h.Uploader.Initiations);                     // qayta yuklanmadi
        Assert.Equal(scriptCalls, h.Script.Calls);                   // qayta yaratilmadi
        var j = h.Store.Get(job.Id)!;
        Assert.Equal(JobStatus.Published, j.Status);
        Assert.False(j.PublishNow);
    }

    [Fact]
    public async Task ArchivedVideoThatMissedUpload_IsUploadedPublic_WithoutRegenerating()
    {
        using var h = new Harness();
        var job = h.AddJob();
        h.Uploader.Connected = false;
        var first = await h.Run();                                   // video tayyor, lekin YouTube ulanmagan
        Assert.Equal(RunOutcome.NeedsAttention, first.Outcome);
        Assert.Equal(JobStatus.NeedsAttention, h.Store.Get(job.Id)!.Status);
        var tts = h.Tts.Synth; var scripts = h.Script.Calls;

        h.Uploader.Connected = true;
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(5);                  // chiqish vaqti o'tib ketdi
        h.Store.SetPublishNow(job.Id, true);
        var r = await h.Run();

        Assert.Equal(RunOutcome.Completed, r.Outcome);
        Assert.Equal(tts, h.Tts.Synth);
        Assert.Equal(scripts, h.Script.Calls);
        Assert.True(h.Uploader.LastPublishNow);
        Assert.Equal(JobStatus.Published, h.Store.Get(job.Id)!.Status);
    }

    [Fact]
    public void CountPending_IgnoresFinishedCancelledAndBackoff()
    {
        using var h = new Harness();
        h.AddJob("a"); h.AddJob("b");
        var done = h.AddJob("c"); done.Status = JobStatus.Published; h.Store.Update(done);
        var later = h.AddJob("d"); later.Status = JobStatus.Failed; later.NextAttemptAtUtc = h.Clock.UtcNow.AddHours(1); h.Store.Update(later);
        Assert.Equal(2, h.Store.CountPending(h.Clock.UtcNow));
        h.Store.SetPublishNow(later.Id, true);                        // so'rov backoff'ni bekor qiladi
        Assert.Equal(3, h.Store.CountPending(h.Clock.UtcNow));
    }
}
