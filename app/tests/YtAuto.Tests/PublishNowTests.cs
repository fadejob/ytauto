using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;

namespace YtAuto.Tests;

public class BatchPlannerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]   // navbatdagi barcha mavzular tayyorlanadi (oldin ko'pi 3 ta edi)
    [InlineData(40, 40)]
    [InlineData(500, BatchPlanner.MaxVideosPerRun)]
    public void VideosPerRun_IsWholeQueue(int pending, int expected) => Assert.Equal(expected, BatchPlanner.VideosFor(pending));

    [Fact]
    public void Pause_IsShorter_WhenQueueIsLong()
    {
        Assert.Equal(BatchPlanner.PauseBetweenVideos, BatchPlanner.PauseFor(1));
        Assert.Equal(BatchPlanner.ShortPauseBetweenVideos, BatchPlanner.PauseFor(10));
        Assert.True(BatchPlanner.ShortPauseBetweenVideos < BatchPlanner.PauseBetweenVideos);
    }

    [Fact]
    public void ClaimNext_TakesNearestPublishDateFirst_AndFarFutureTopicsToo()
    {
        using var h = new Harness();
        var far = h.AddJob("far future topic", minutesAgo: 120, publishInDays: 30);
        var soon = h.AddJob("soon topic", minutesAgo: 5, publishInDays: 1);
        Assert.Equal(2, h.Store.CountPending(h.Clock.UtcNow));

        var first = h.Store.ClaimNext("run-a", h.Clock.UtcNow);
        Assert.Equal(soon.Id, first!.Id); // chiqish vaqti yaqini birinchi, qo'shilgan vaqtidan qat'i nazar
        var second = h.Store.ClaimNext("run-a", h.Clock.UtcNow);
        Assert.Equal(far.Id, second!.Id); // 30 kun keyingi mavzu ham hozir tayyorlanadi (deadline faqat YouTube uchun)
    }
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
