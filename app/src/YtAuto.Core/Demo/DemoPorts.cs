using YtAuto.Core.Pipeline;

namespace YtAuto.Core.Demo;

/// <summary>1-bosqich (qoralama) so'rovi: loyiha konteksti, materiallar va ularning Gemini ko'radigan kadrlari.</summary>
public sealed class DemoScriptRequest
{
    public required DemoSpec Spec { get; init; }
    public required IReadOnlyList<DemoLocalMedia> Media { get; init; }
    /// <summary>Kadrlar fayllari (media id → mutlaq yo'llar).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> PreviewFiles { get; init; }
    public string Format { get; init; } = "horizontal 16:9";
    public string Model { get; init; } = "";
    public string ChannelContext { get; init; } = "";
    public bool WriteDescription { get; init; } = true;
    public bool WriteTags { get; init; } = true;
    public IReadOnlyList<string> PreviousProblems { get; init; } = Array.Empty<string>();
}

/// <summary>2-bosqich (optimallashtirish) so'rovi: qoralamadan ortiqcha gaplar, ko'rsatmalar va takrorlar olib tashlanadi.</summary>
public sealed class DemoOptimizeRequest
{
    public required DemoSpec Spec { get; init; }
    public required DemoDraft Draft { get; init; }
    public required IReadOnlyList<DemoLocalMedia> Media { get; init; }
    public string Model { get; init; } = "";
    public IReadOnlyList<string> PreviousProblems { get; init; } = Array.Empty<string>();
}

public sealed class DemoStepResult<T>
{
    public required T Value { get; init; }
    public string Raw { get; init; } = "";
    public string ModelUsed { get; init; } = "";
    public int PromptTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>Demo ssenariy yozuvchisi (Gemini): ikki bosqich - yaratish va optimallashtirish.</summary>
public interface IDemoScriptWriter
{
    Task<DemoStepResult<DemoDraft>> DraftAsync(DemoScriptRequest request, IJobLog log, CancellationToken ct);
    /// <summary>Faqat aytiladigan matn qoladi; har sahna o'z materiali bilan (media_id), focus/on_screen_text alohida.</summary>
    Task<DemoStepResult<List<DemoScene>>> OptimizeAsync(DemoOptimizeRequest request, IJobLog log, CancellationToken ct);
}

/// <summary>MCP orqali yuborilgan materialni kompyuterga yuklab oladi va pipeline uchun tayyorlaydi.</summary>
public interface IDemoMediaFetcher
{
    /// <summary>
    /// <paramref name="m"/> ni yuklab oladi: rasm - formati tekshirilib saqlanadi, video - ovozsiz H.264 MP4 ga o'tkaziladi.
    /// Natija: yaratilgan fayl (kengaytmasi bilan) mutlaq yo'li. <paramref name="outStem"/> - kengaytmasiz yo'l.
    /// </summary>
    Task<string> FetchAsync(DemoMedia m, string outStem, IJobLog log, CancellationToken ct);
}

/// <summary>Tayyor demo videoni serverga (Storage) yuboradi: MCP get_video_result u yerdan havola beradi.</summary>
public interface IDemoResultSink
{
    Task<DemoResult> DeliverAsync(VideoJobRef job, string videoPath, string? coverPath, string? subtitlesPath, IJobLog log, CancellationToken ct);
}

/// <summary>Natija yuborish uchun vazifa haqida kerakli minimum.</summary>
public sealed record VideoJobRef(long Id, double DurationSec);
