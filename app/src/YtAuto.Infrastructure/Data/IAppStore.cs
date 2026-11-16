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
    /// <summary>Worker ishga tushishining resurs/hajm statistikasi (JSON) - bazaga yoziladi, UI va bot ko'rsatadi.</summary>
    void SaveRunStats(string runId, string json);
    IReadOnlyList<(string RunId, DateTimeOffset Started, DateTimeOffset? Finished, string? Outcome, string? Machine, string? StatsJson)> RunStats(int n = 30);
    /// <summary>
    /// Kompyuter "yuragi urishi" (faqat online bazada): kompyuter yoniqligi, ilova ochiqligi va worker nima qilayotgani.
    /// null parametr - o'zgartirilmaydi. appOpen: ilova ochiq/yopiq; workerState: "busy" | "idle".
    /// </summary>
    void Heartbeat(string machine, bool? appOpen, string? workerState, long? jobId, string? detail, string? version);
    /// <summary>Bot "integratsiyalarni tekshirish" so'rovi yuborgan bo'lsa true qaytaradi va bayroqni atomar olib tashlaydi.</summary>
    bool TakeCheckRequest(string machine);
    void SaveCheckResult(string machine, string json);
    /// <summary>Bot "Hozir ishga tushirish" so'rovi yuborgan bo'lsa true qaytaradi va bayroqni atomar olib tashlaydi.</summary>
    bool TakeRunRequest(string machine);
    /// <summary>Botdan kelgan buyruqlar (shablonni o'chirish, upload sessiyasini tozalash): shu kompyuter uchun kutayotganlarini olib, bajarilgan deb belgilaydi.</summary>
    IReadOnlyList<(long Id, string Kind, string Arg)> TakeCommands(string machine);
    /// <summary>Shu kompyuterdagi shablonlar ro'yxatini online bazaga yozadi (eskisi almashtiriladi). Fayllar yuklanmaydi.</summary>
    void ReplaceTemplates(string machine, IReadOnlyList<(string Name, string Kind, long Size, DateTimeOffset? Added, string Source, string Notes)> items);
    /// <summary>Botdan yuklangan shablonlardan shu kompyuterga tegishli kutayotganini band qiladi (pending → downloading).</summary>
    (long Id, string Name, string Kind, string Url, long Size)? ClaimTemplateUpload(string machine);
    void UpdateTemplateUpload(long id, string status, int progress, string? message);
    /// <summary>Shu worker ishga tushishi (runId) hozir band qilib turgan vazifa.</summary>
    (long JobId, string Topic, int Status)? CurrentWork(string runId);
    /// <summary>Global kalit-qiymat (online bazada - barcha kompyuterlar uchun umumiy).</summary>
    string? GetSetting(string key);
    void SetSetting(string key, string value);
}
