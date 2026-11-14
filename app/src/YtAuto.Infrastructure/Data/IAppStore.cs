using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Data;

/// <summary>
/// UI va worker ishlatadigan to'liq ombor: online PostgreSQL (Supabase) yoki lokal SQLite.
/// Mavzular (CRUD), bosqichlar, hodisalar, worker ishga tushishlari va umumiy (global) sozlamalar - masalan promptlar.
/// </summary>
public interface IAppStore : IJobStore
{
    string Kind { get; }
    IReadOnlyList<VideoJob> Page(bool released, int offset, int limit, out int total);
    IReadOnlyList<JobEvent> GetEvents(long jobId, int limit = 300);
    void RequestCancel(long jobId);
    bool TryCancelUnclaimed(long jobId);
    bool IsClaimed(long jobId);
    void Requeue(long jobId, bool resetAttempts);
    /// <summary>Mavzuni butunlay o'chiradi (faqat hech kim band qilmagan bo'lsa). true - o'chirildi.</summary>
    bool Delete(long jobId);
    void StartRun(string runId, int pid, DateTimeOffset processStart, string trigger);
    void FinishRun(string runId, string outcome, long? jobId, string? message);
    IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, long? JobId, string? Message, string? Trigger)> RecentRuns(int n = 20);
    /// <summary>Global kalit-qiymat (online bazada - barcha kompyuterlar uchun umumiy).</summary>
    string? GetSetting(string key);
    void SetSetting(string key, string value);
}
