namespace YtAuto.Core.Domain;

/// <summary>
/// Vazifa holatlari orasidagi ruxsat etilgan o'tishlar. Barcha holat o'zgarishlari shu yerda tekshiriladi,
/// shunda UI va worker bir xil qoidaga bo'ysunadi.
/// </summary>
public static class JobStateMachine
{
    public static readonly StageKind[] StageOrder =
    {
        StageKind.Script, StageKind.Voice, StageKind.Align, StageKind.Edit,
        StageKind.Render, StageKind.Validate, StageKind.Archive, StageKind.Upload,
    };

    /// <summary>Worker ishlayotgan (oraliq) holatlar.</summary>
    public static bool IsInProgress(JobStatus s) => s is JobStatus.Scripting or JobStatus.GeneratingVoice
        or JobStatus.Aligning or JobStatus.Editing or JobStatus.Rendering or JobStatus.Validating
        or JobStatus.Archived or JobStatus.Uploading;

    /// <summary>Worker endi tegmaydigan holatlar.</summary>
    public static bool IsTerminal(JobStatus s) => s is JobStatus.Published or JobStatus.Cancelled;

    /// <summary>YouTube'ga yuklangan (yoki yuklanmoqda emas, tugagan) holatlar.</summary>
    public static bool IsUploaded(JobStatus s) => s is JobStatus.Processing or JobStatus.Scheduled or JobStatus.Published;

    /// <summary>Dashboard'da "chiqarilgan" guruhi: faqat haqiqatan Published.</summary>
    public static bool IsReleased(JobStatus s) => s == JobStatus.Published;

    /// <summary>Navbatdan olinishi mumkin bo'lgan holatlar (retry va resume shu yerda).</summary>
    public static readonly JobStatus[] ClaimableStatuses =
    {
        JobStatus.Queued, JobStatus.Failed,
        JobStatus.Scripting, JobStatus.GeneratingVoice, JobStatus.Aligning, JobStatus.Editing,
        JobStatus.Rendering, JobStatus.Validating, JobStatus.Archived, JobStatus.Uploading,
    };

    public static JobStatus StatusFor(StageKind stage) => stage switch
    {
        StageKind.Script => JobStatus.Scripting,
        StageKind.Voice => JobStatus.GeneratingVoice,
        StageKind.Align => JobStatus.Aligning,
        StageKind.Edit => JobStatus.Editing,
        StageKind.Render => JobStatus.Rendering,
        StageKind.Validate => JobStatus.Validating,
        StageKind.Archive => JobStatus.Archived,
        StageKind.Upload => JobStatus.Uploading,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public static bool CanTransition(JobStatus from, JobStatus to)
    {
        if (from == to) return true;
        if (from == JobStatus.Cancelled) return false;
        if (from == JobStatus.Published) return false;
        if (to == JobStatus.Cancelled) return !IsUploaded(from) || from == JobStatus.Processing || from == JobStatus.Scheduled;
        return to switch
        {
            // UI: retry / qayta navbatga qo'yish
            JobStatus.Queued => from is JobStatus.Failed or JobStatus.NeedsAttention or JobStatus.Ready || IsInProgress(from),
            // Demo video tayyor: arxiv va natija yuborilgandan keyin (yoki qayta urinishda) - chiqarish buyrug'i kutiladi.
            JobStatus.Ready => from is JobStatus.Archived or JobStatus.Uploading or JobStatus.NeedsAttention or JobStatus.Failed,
            JobStatus.Failed => IsInProgress(from) || from == JobStatus.Queued,
            JobStatus.NeedsAttention => true,
            JobStatus.Processing => from is JobStatus.Uploading or JobStatus.Archived or JobStatus.NeedsAttention or JobStatus.Failed or JobStatus.Ready,
            JobStatus.Scheduled => from is JobStatus.Processing or JobStatus.Uploading or JobStatus.Scheduled or JobStatus.NeedsAttention,
            JobStatus.Published => from is JobStatus.Processing or JobStatus.Scheduled or JobStatus.Uploading or JobStatus.NeedsAttention,
            _ when IsInProgress(to) => from == JobStatus.Queued || from == JobStatus.Failed || IsInProgress(from),
            _ => false,
        };
    }

    public static void EnsureTransition(JobStatus from, JobStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Holat o'tishi ruxsat etilmagan: {from} -> {to}");
    }

    /// <summary>Bosqichdan keyingi barcha bosqichlar (qayta bajarilganda bekor qilinadi).</summary>
    public static IEnumerable<StageKind> Downstream(StageKind stage) => StageOrder.Where(s => s > stage);

    public static string Describe(JobStatus s) => s switch
    {
        JobStatus.Queued => "Navbatda",
        JobStatus.Scripting => "Ssenariy yozilmoqda",
        JobStatus.GeneratingVoice => "Ovoz yaratilmoqda",
        JobStatus.Aligning => "Subtitr vaqtlari",
        JobStatus.Editing => "Montaj (Claude)",
        JobStatus.Rendering => "Render (OpenFilm)",
        JobStatus.Validating => "Sifat tekshiruvi",
        JobStatus.Archived => "Arxivlandi",
        JobStatus.Uploading => "YouTube'ga yuklanmoqda",
        JobStatus.Processing => "YouTube qayta ishlamoqda",
        JobStatus.Scheduled => "Rejalashtirildi",
        JobStatus.Published => "Chiqarildi",
        JobStatus.Failed => "Xato (qayta urinadi)",
        JobStatus.NeedsAttention => "E'tibor kerak",
        JobStatus.Cancelled => "Bekor qilindi",
        JobStatus.Ready => "Tayyor (chiqarish kutilmoqda)",
        _ => s.ToString(),
    };
}
