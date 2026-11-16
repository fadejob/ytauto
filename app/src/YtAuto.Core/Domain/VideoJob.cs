namespace YtAuto.Core.Domain;

public sealed class VideoJob
{
    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Language { get; set; } = "en";
    /// <summary>0 - dinamik (standart): uzunlik ovoz bilan belgilanadi.</summary>
    public int TargetDurationSec { get; set; }
    public VideoFormat Format { get; set; } = VideoFormat.Landscape16x9;
    public string TemplateName { get; set; } = "";
    public string VoiceProfileName { get; set; } = "";
    /// <summary>YouTube'da ommaga chiqish vaqti (UTC).</summary>
    public DateTimeOffset PublishAtUtc { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Muvaffaqiyatsiz tugagan worker ishga tushishlari soni (crash ham hisoblanadi).</summary>
    public int RunAttempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public bool CancelRequested { get; set; }
    /// <summary>
    /// "Hozir chiqarish" so'rovi (online bazadan ham qo'yilishi mumkin): worker shu vazifani navbatdan tashqari darhol tayyorlaydi
    /// va YouTube'ga darhol public qiladi. Update() bu maydonni yozmaydi - faqat alohida atomar amallar (SetPublishNow).
    /// </summary>
    public bool PublishNow { get; set; }
    /// <summary>Sifat tekshiruvidan keyin Claude'ga qaytarilgan tuzatish sikllari soni.</summary>
    public int QaFixCycles { get; set; }

    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public string? AttentionReason { get; set; }

    /// <summary>Vazifa birinchi boshlanganda olingan sozlamalar/prompt/shablon snapshot'i (JSON).</summary>
    public string? SettingsSnapshotJson { get; set; }

    /// <summary>
    /// Botda foydalanuvchi kiritgan tayyor subtitr matni va title (ikkalasi birga). Berilgan bo'lsa Gemini chaqirilmaydi -
    /// ssenariy shu matndan tuziladi. Faqat bot yozadi: Update() bu maydonlarga tegmaydi.
    /// </summary>
    public string? ManualScript { get; set; }
    public string? ManualTitle { get; set; }
    /// <summary>Botdan yuklangan tayyor ovoz (Supabase Storage imzolangan havolasi). Berilgan bo'lsa VoiceStudio TTS o'tkazib yuboriladi.</summary>
    public string? ManualAudioUrl { get; set; }

    public bool HasManualScript => !string.IsNullOrWhiteSpace(ManualScript) && !string.IsNullOrWhiteSpace(ManualTitle);
    public bool HasManualAudio => !string.IsNullOrWhiteSpace(ManualAudioUrl);

    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? VideoArchivePath { get; set; }
    public string? AudioArchivePath { get; set; }
    public string? SubtitleArchivePath { get; set; }
    public double? AudioDurationSec { get; set; }

    public string? YouTubeVideoId { get; set; }
    public string? YouTubePrivacy { get; set; }
    public string? YouTubeUploadStatus { get; set; }
    public string? YouTubeProcessingStatus { get; set; }
    public DateTimeOffset? YouTubeCheckedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }

    public string? WorkDir { get; set; }
    public bool WorkDirCleaned { get; set; }

    public string YouTubeUrl => string.IsNullOrEmpty(YouTubeVideoId) ? "" : $"https://youtu.be/{YouTubeVideoId}";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Topic : Title!;
}

public sealed class StageRecord
{
    public long JobId { get; set; }
    public StageKind Stage { get; set; }
    public StageState State { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? InputHash { get; set; }
    /// <summary>Natija fayllari va qo'shimcha ma'lumot (JSON, <see cref="StageOutputs"/>).</summary>
    public string? OutputsJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long? DurationMs { get; set; }
    /// <summary>O'lchangan resurslar (peak RAM, CPU vaqti) JSON.</summary>
    public string? MetricsJson { get; set; }
}

public sealed class JobEvent
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Level { get; set; } = "INFO";
    public string? Stage { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>Bosqich natija fayllari: resume paytida mavjudligi va o'lchami tekshiriladi.</summary>
public sealed class StageOutputs
{
    public List<OutputFile> Files { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

public sealed class OutputFile
{
    public string Role { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
}
