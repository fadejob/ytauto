using YtAuto.Core.Domain;

namespace YtAuto.Core.Pipeline;

/// <summary>Bitta vazifaning ish papkasi tuzilishi va snapshot sozlamalari.</summary>
public sealed class JobContext
{
    public required VideoJob Job { get; init; }
    public required SettingsSnapshot Snapshot { get; init; }
    public required string WorkRoot { get; init; }
    public required string Dir { get; init; }
    public required string RecordDir { get; init; }
    public required IJobLog Log { get; init; }
    public required string RunId { get; init; }
    public CancellationToken Ct { get; set; }

    public AppSettings S => Snapshot.Settings;

    public string ScriptDir => Path.Combine(Dir, "script");
    public string ScriptJson => Path.Combine(ScriptDir, "script.json");
    public string VoiceDir => Path.Combine(Dir, "voice");
    public string VoiceChunksDir => Path.Combine(VoiceDir, "chunks");
    public string VoiceWav => Path.Combine(VoiceDir, "voice.wav");
    public string AlignDir => Path.Combine(Dir, "align");
    public string AsrJson => Path.Combine(AlignDir, "asr.json");
    public string AlignmentJson => Path.Combine(AlignDir, "alignment.json");
    public string TimingJson => Path.Combine(AlignDir, "timing.json");
    public string VoiceVtt => Path.Combine(AlignDir, "voice.vtt");
    public string VoiceSrt => Path.Combine(AlignDir, "voice.srt");
    public string EditDir => Path.Combine(Dir, "edit");
    public string FilmDir => Path.Combine(EditDir, "film");
    public string FilmHtml => Path.Combine(FilmDir, "film.html");
    public string QaFeedback => Path.Combine(EditDir, "qa_feedback.md");
    public string RenderDir => Path.Combine(Dir, "render");
    public string FinalMp4 => Path.Combine(RenderDir, "final.mp4");
    public string QaDir => Path.Combine(Dir, "qa");
    public string QaReportJson => Path.Combine(QaDir, "qa_report.json");
    public string LogsDir => Path.Combine(Dir, "logs");

    public Dictionary<string, double> StageMetrics { get; } = new();

    public void EnsureDirs()
    {
        foreach (var d in new[] { Dir, ScriptDir, VoiceDir, VoiceChunksDir, AlignDir, EditDir, RenderDir, QaDir, LogsDir, RecordDir })
            Directory.CreateDirectory(d);
    }

    public (int W, int H) StageSize => Job.Format switch
    {
        VideoFormat.Shorts9x16 => (1080, 1920),
        VideoFormat.Square1x1 => (1080, 1080),
        _ => (1920, 1080),
    };

    public int SubtitleMaxChars => Job.Format == VideoFormat.Shorts9x16 ? 30 : 42;
}
