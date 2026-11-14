using YtAuto.Core.Domain;
using YtAuto.Core.Qa;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public interface IJobLog
{
    void Info(string message, string? stage = null);
    void Warn(string message, string? stage = null);
    void Error(string message, string? stage = null);
    /// <summary>Faqat faylga yoziladigan batafsil yozuv (UI hodisalariga tushmaydi).</summary>
    void Debug(string message, string? stage = null);
}

public interface IJobStore
{
    long Insert(VideoJob job);
    VideoJob? Get(long id);
    void Update(VideoJob job);
    /// <summary>Navbatdan eng eski yaroqli vazifani atomar band qiladi.</summary>
    VideoJob? ClaimNext(string runId, DateTimeOffset nowUtc);
    /// <summary>Navbatda kutayotgan (band qilinishi mumkin bo'lgan) mavzular soni - ishga tushishda bazadan sinxronlash uchun.</summary>
    int CountPending(DateTimeOffset nowUtc);
    /// <summary>"Hozir chiqarish" so'ralgan va hali bajarilmagan vazifalar soni.</summary>
    int CountPublishNow(DateTimeOffset nowUtc);
    /// <summary>"Hozir chiqarish" so'ralgan eng eski vazifani atomar band qiladi (Published/Cancelled bo'lmagan).</summary>
    VideoJob? ClaimPublishNow(string runId, DateTimeOffset nowUtc);
    /// <summary>"Hozir chiqarish" bayrog'ini qo'yadi yoki olib tashlaydi (Update()'dan alohida, poyga bo'lmasligi uchun).</summary>
    void SetPublishNow(long jobId, bool value);
    void ReleaseClaim(long jobId, string runId);
    /// <summary>OS mutex ostida chaqiriladi: tirik bo'lmagan worker'larning band qilishlarini bo'shatadi.</summary>
    int ReleaseStaleClaims(string currentRunId);
    bool IsCancelRequested(long jobId);
    StageRecord GetStage(long jobId, StageKind stage);
    IReadOnlyList<StageRecord> GetStages(long jobId);
    void SaveStage(StageRecord stage);
    void AddEvent(long jobId, string level, string? stage, string message);
    IReadOnlyList<VideoJob> ListByStatus(params JobStatus[] statuses);
    IReadOnlyList<(JobStatus Status, DateTimeOffset PublishAtUtc)> PublishSlots();
    void RecordProcess(OwnedProcessRecord rec);
    /// <summary>Band qilish hali tirikligini bildiradi (umumiy online bazada boshqa kompyuterlar uni eskirgan deb olmasligi uchun).</summary>
    void Heartbeat(long jobId, string runId) { }
    /// <summary>Aniq bitta vazifani atomar band qiladi (hech kim band qilmagan bo'lsa). "Tayyor videoni yuklash" uchun.</summary>
    bool TryClaim(long jobId, string runId, DateTimeOffset nowUtc);
    /// <summary>"Tayyor videoni YouTube'ga yuklash" so'ralgan (masalan Telegram botdan), arxivlangan, yuklanmagan va band qilinmagan vazifalar.</summary>
    IReadOnlyList<long> UploadRequestIds();
    /// <summary>"Tayyor videoni yuklash" so'rovi bayrog'i (Update()'dan alohida, poyga bo'lmasligi uchun).</summary>
    void SetUploadRequested(long jobId, bool value);
}

public sealed class OwnedProcessRecord
{
    public long? JobId { get; set; }
    public string RunId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Tool { get; set; } = "";
    public int Pid { get; set; }
    public DateTimeOffset ProcessStartUtc { get; set; }
    public string ExePath { get; set; } = "";
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public long? PeakMemoryBytes { get; set; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

public static class SecretNames
{
    public const string GeminiApiKey = "gemini_api_key";
    public const string YouTubeClientSecret = "youtube_client_secret";
    public const string YouTubeTokens = "youtube_tokens";
    public const string UploadSessionPrefix = "upload_session_";
    /// <summary>Online baza (Supabase PostgreSQL) ulanish satri, parol bilan - faqat DPAPI'da.</summary>
    public const string PostgresConnection = "postgres_connection";
}

public interface IScriptGenerator
{
    Task<ScriptResult> GenerateAsync(ScriptRequest request, IJobLog log, CancellationToken ct);
}

public interface ITtsService
{
    /// <summary>VoiceStudio'ni tayyor holatga keltiradi (kerak bo'lsa o'zi ishga tushiradi).</summary>
    Task<ITtsSession> OpenAsync(VoiceStudioSettings settings, IJobLog log, CancellationToken ct);
}

public interface ITtsSession : IAsyncDisposable
{
    bool StartedByUs { get; }
    string Version { get; }
    Task EnsureModelAvailableAsync(string model, CancellationToken ct);
    /// <summary>Matnni WAV faylga sintez qiladi. Server yiqilsa, qayta ishga tushirishga harakat qiladi.</summary>
    Task SynthesizeAsync(string text, VoiceProfile voice, string outWav, CancellationToken ct);
    Dictionary<string, double> Metrics { get; }
}

public interface IAligner
{
    Task<IReadOnlyList<WordTiming>> TranscribeWordsAsync(string wavPath, string language, string outJson, IJobLog log, CancellationToken ct);
    Dictionary<string, double> LastMetrics { get; }
    string Describe();
}

public sealed class ToolRunResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string StdoutPath { get; init; } = "";
    public string StderrPath { get; init; } = "";
    public long PeakMemoryBytes { get; init; }
    public double CpuSeconds { get; init; }
    public int LeftoverProcessesKilled { get; init; }
    public string StdoutTail { get; init; } = "";
    public string StderrTail { get; init; } = "";
}

public interface IEditorAgent
{
    /// <summary>Yangi, mustaqil non-interactive Claude Code sessiyasi. prompt - qat'iy, foydalanuvchi matni fayllarda.</summary>
    Task<EditorRunResult> RunAsync(string workingDir, string prompt, TimeSpan timeout, string logPrefix,
        IReadOnlyList<string> extraPathDirs, IJobLog log, CancellationToken ct);
    string Version { get; }
}

public sealed class EditorRunResult
{
    public ToolRunResult Run { get; init; } = new();
    public bool IsError { get; init; }
    public string ResultText { get; init; } = "";
    public string SessionId { get; init; } = "";
    public double? CostUsd { get; init; }
    public int PermissionDenials { get; init; }
}

public sealed class RenderOptions
{
    public int Fps { get; init; } = 30;
    public int Workers { get; init; } = 1;
    public double LoudnessLufs { get; init; } = -14;
    public bool BurnSubtitles { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(90);
}

public interface IFilmTool
{
    string Version { get; }
    Task<string> GetSkillTextAsync(string name, CancellationToken ct);
    /// <summary>Worker'ga tegishli OpenFilm sessiyasi: dispose paytida Studio server va child process'lar yopiladi.</summary>
    Task<IFilmSession> OpenSessionAsync(IJobLog log, CancellationToken ct);
    /// <summary>OpenFilm shim'ini (bash va cmd) papkaga yozadi: Claude PATH orqali `openfilm` chaqira oladi.</summary>
    string WriteShims(string dir);
    string FfmpegDir { get; }
}

public interface IFilmSession : IAsyncDisposable
{
    Task<ToolRunResult> LookAsync(string filmDir, IReadOnlyList<string> args, string logPrefix, TimeSpan timeout, CancellationToken ct);
    Task<ToolRunResult> RenderAsync(string filmDir, string outMp4, RenderOptions options, CancellationToken ct);
}

public sealed class MediaInfo
{
    public double Duration { get; init; }
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public double Fps { get; init; }
}

public interface IMediaProbe
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct);
    Task<(bool Ok, string Errors)> DecodeCheckAsync(string path, CancellationToken ct);
    Task<IReadOnlyList<BlackSegment>> BlackDetectAsync(string path, CancellationToken ct);
    Task<(double MaxVolumeDb, double? IntegratedLufs)> AudioLevelsAsync(string path, CancellationToken ct);
    Task ExtractFrameAsync(string video, double atSec, string outImage, int width, CancellationToken ct);
    Task<(double Mean, double Std)> FrameLumaAsync(string video, double atSec, CancellationToken ct);
    string Version { get; }
}

public sealed class UploadRequest
{
    public string VideoPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string CategoryId { get; init; } = "27";
    public string Language { get; init; } = "en";
    public DateTimeOffset PublishAtUtc { get; init; }
    /// <summary>true - video darhol public yuklanadi (publishAt yuborilmaydi).</summary>
    public bool PublishNow { get; init; }
    public bool MadeForKids { get; init; }
    public bool ContainsSyntheticMedia { get; init; }
    public string? ThumbnailPath { get; init; }
    public string License { get; init; } = "youtube";
    public bool Embeddable { get; init; } = true;
    public bool PublicStatsViewable { get; init; } = true;
    public bool NotifySubscribers { get; init; } = true;
    public DateTimeOffset? RecordingDateUtc { get; init; }
    public string LocationDescription { get; init; } = "";
}

/// <summary>Upload holatini bazaga o'z vaqtida yozish uchun callbacklar.</summary>
public interface IUploadStateSink
{
    string? LoadSessionUri();
    void SaveSessionUri(string? uri, DateTimeOffset startedAtUtc);
    DateTimeOffset? SessionStartedAtUtc();
    void SaveVideoId(string videoId);
    void Progress(long sent, long total);
}

public sealed class YouTubeVideoStatus
{
    public string VideoId { get; init; } = "";
    public bool Exists { get; init; }
    public string UploadStatus { get; init; } = "";
    public string PrivacyStatus { get; init; } = "";
    public string ProcessingStatus { get; init; } = "";
    public DateTimeOffset? PublishAtUtc { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public string? FailureReason { get; init; }
    public string? RejectionReason { get; init; }
    public string Title { get; init; } = "";
}

public interface IVideoUploader
{
    bool IsConnected { get; }
    Task<string> UploadAsync(UploadRequest request, IUploadStateSink state, IJobLog log, CancellationToken ct);
    Task<YouTubeVideoStatus> GetStatusAsync(string videoId, CancellationToken ct);
    /// <summary>Noaniq holatda: kanal yuklamalari orasidan shu sarlavhali, shu vaqtdan keyin yuklangan videoni qidiradi.</summary>
    Task<string?> FindRecentUploadAsync(string title, DateTimeOffset sinceUtc, CancellationToken ct);
    Task<bool> SetThumbnailAsync(string videoId, string imagePath, CancellationToken ct);
    /// <summary>Yuklangan (private/rejalashtirilgan) videoni darhol public qiladi. Allaqachon public bo'lsa hech narsa qilmaydi.</summary>
    Task MakePublicAsync(string videoId, CancellationToken ct);
}

public interface IResourceMonitor
{
    double AvailableRamGb();
    double TotalRamGb();
    double FreeDiskGb(string path);
}

public interface IAppPaths
{
    string DataRoot { get; }
    string DatabasePath { get; }
    string LogsDir { get; }
    string RecordsDir { get; }
    string ToolsDir { get; }
    string RecordDirFor(long jobId);
}
