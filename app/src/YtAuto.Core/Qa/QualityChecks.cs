using System.Text;
using System.Text.Json.Serialization;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Qa;

public enum QaSeverity { Info, Warning, Error }

public sealed class QaIssue
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("severity")] public QaSeverity Severity { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("at")] public double? AtSec { get; set; }
    /// <summary>Claude'ga qaytarilganda tuzatish mumkinmi (aks holda worker/sozlama muammosi).</summary>
    [JsonPropertyName("fixable_by_editor")] public bool FixableByEditor { get; set; } = true;

    public override string ToString() => $"[{Severity}] {Code}{(AtSec is null ? "" : $" @{AtSec:0.00}s")}: {Message}";
}

public sealed class QaReport
{
    [JsonPropertyName("issues")] public List<QaIssue> Issues { get; set; } = new();
    [JsonPropertyName("metrics")] public Dictionary<string, double> Metrics { get; set; } = new();
    [JsonPropertyName("checked")] public List<string> Checked { get; set; } = new();

    [JsonIgnore] public bool Passed => Issues.All(i => i.Severity != QaSeverity.Error);
    [JsonIgnore] public IEnumerable<QaIssue> Errors => Issues.Where(i => i.Severity == QaSeverity.Error);

    public void Add(string code, QaSeverity sev, string msg, double? at = null, bool fixable = true)
        => Issues.Add(new QaIssue { Code = code, Severity = sev, Message = msg, AtSec = at, FixableByEditor = fixable });

    public string ToFeedbackMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Quality check failed - fix these problems");
        sb.AppendLine();
        sb.AppendLine("The worker rendered your film and checked it. Fix every ERROR below in the OpenFilm project (film/),");
        sb.AppendLine("keep everything else as it is. Do not touch assets/voice.wav or assets/voice.vtt. Do not render.");
        sb.AppendLine();
        foreach (var i in Issues.Where(x => x.Severity != QaSeverity.Info))
            sb.AppendLine($"- {i.Severity.ToString().ToUpperInvariant()} `{i.Code}`{(i.AtSec is null ? "" : $" at {i.AtSec:0.00}s")}: {i.Message}");
        sb.AppendLine();
        sb.AppendLine("After fixing, run `openfilm look` again, open the frames around the times above, update BUILD_NOTES.md and reply DONE.");
        return sb.ToString();
    }
}

public sealed class BlackSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Duration => End - Start;
}

/// <summary>Timeline (film.html), subtitr va audio o'lchovlariga asoslangan deterministik sifat qoidalari.</summary>
public static class QualityRules
{
    public const double Tolerance = 0.06;

    public static void CheckTimeline(FilmTimeline tl, double audioDuration, QaReport r, VideoFormat format)
    {
        r.Checked.Add("timeline");
        var visual = tl.Clips.Where(c => c.IsVisual && !tl.HiddenTracks.Contains(c.Track)).ToList();
        foreach (var w in tl.ParseWarnings) r.Add("timeline_parse", QaSeverity.Warning, w);
        if (visual.Count == 0) { r.Add("no_visuals", QaSeverity.Error, "film.html da birorta ham vizual klip yo'q."); return; }

        var expected = format switch { VideoFormat.Shorts9x16 => (1080, 1920), VideoFormat.Square1x1 => (1080, 1080), _ => (1920, 1080) };
        var ratio = tl.Width / (double)tl.Height; var want = expected.Item1 / (double)expected.Item2;
        if (Math.Abs(ratio - want) > 0.02)
            r.Add("aspect_ratio", QaSeverity.Error, $"Stage {tl.Width}x{tl.Height}, lekin format {format} ({expected.Item1}x{expected.Item2}) kerak. viewport meta'ni tuzating.");

        var unknown = visual.Where(c => c.Length is null).ToList();
        foreach (var c in unknown)
            r.Add("clip_length_unknown", QaSeverity.Error, $"'{c.Src}' klipining uzunligi aniq emas: src ga #t=in,out qo'ying.", c.At);

        var intervals = visual.Where(c => c.Length is > 0).Select(c => (c.At, End: c.At + c.Length!.Value)).OrderBy(x => x.At).ToList();
        if (intervals.Count == 0) return;
        var first = intervals[0].At;
        r.Metrics["first_visual_at"] = first;
        if (first > Tolerance) r.Add("starts_late", QaSeverity.Error, $"Birinchi vizual klip {first:0.00}s da boshlanadi; 0.0s dan boshlanishi kerak.", 0);

        // Birlashma ichidagi bo'shliqlar
        double coveredTo = intervals[0].End;
        foreach (var (at, end) in intervals.Skip(1))
        {
            if (at > coveredTo + Tolerance && coveredTo < audioDuration)
                r.Add("gap", QaSeverity.Error, $"{coveredTo:0.00}s dan {Math.Min(at, audioDuration):0.00}s gacha hech qanday vizual klip yo'q (qora ekran). Oldingi sahnani cho'zing yoki keyingisini oldinroq boshlang.", coveredTo);
            coveredTo = Math.Max(coveredTo, end);
        }
        r.Metrics["visual_end"] = coveredTo;
        if (coveredTo < audioDuration - Tolerance)
            r.Add("ends_early", QaSeverity.Error, $"Vizual klipler {coveredTo:0.00}s da tugaydi, audio esa {audioDuration:0.00}s. Oxirgi sahna audio yakunini qoplashi kerak.", coveredTo);

        // Audio treklar
        var audio = tl.Clips.Where(c => c.Kind == ClipKind.Audio && !tl.MutedTracks.Contains(c.Track) && !c.Muted).ToList();
        var voice = audio.FirstOrDefault(c => c.Src.EndsWith("voice.wav", StringComparison.OrdinalIgnoreCase));
        if (voice is null) r.Add("voice_missing", QaSeverity.Error, "Ovoz (assets/voice.wav) film.html da audio klip sifatida yo'q yoki o'chirilgan.");
        else
        {
            if (voice.At > Tolerance) r.Add("voice_offset", QaSeverity.Error, $"Ovoz {voice.At:0.00}s da boshlanadi; subtitrlar 0 dan hisoblangan. at=\"0\" qiling.");
            if (Math.Abs(voice.Volume - 1) > 0.01) r.Add("voice_volume", QaSeverity.Warning, $"Ovoz volume={voice.Volume}; 1 bo'lishi kerak.");
            if (voice.Speed != 1) r.Add("voice_speed", QaSeverity.Error, "Ovoz tezligi o'zgartirilgan; speed=1 bo'lishi kerak.");
        }
        foreach (var m in audio.Where(a => a != voice))
        {
            if (m.Volume > 0.3) r.Add("music_too_loud", QaSeverity.Error, $"Fon audio '{m.Src}' volume={m.Volume}: nutqni bosib ketmasligi uchun 0.15-0.25 qiling.", m.At);
            if (m.End is double e && e > audioDuration + 1.5) r.Add("audio_overrun", QaSeverity.Warning, $"'{m.Src}' ovozdan ancha keyin tugaydi ({e:0.0}s).");
        }
        if (visual.Where(c => c.End is not null).Max(c => c.End!.Value) > audioDuration + 3)
            r.Add("video_tail", QaSeverity.Warning, "Vizual klipler audiodan 3 soniyadan ko'proq keyin tugaydi (bo'sh dum).");
    }

    /// <summary>Sahna kesimlari subtitr cue chegaralariga majburan bog'langanini aniqlaydi (oldingi sinovdagi kamchilik).</summary>
    public static void CheckCutsVsCues(FilmTimeline tl, IReadOnlyList<SubtitleCue> cues, QaReport r)
    {
        r.Checked.Add("cuts_vs_cues");
        var cuts = tl.Clips.Where(c => c.IsVisual && c.At > 0.05).Select(c => c.At).Distinct().OrderBy(x => x).ToList();
        if (cuts.Count < 4 || cues.Count < 4) return;
        var cueStarts = cues.Select(c => c.Start).ToList();
        int aligned = cuts.Count(cut => cueStarts.Any(s => Math.Abs(s - cut) < 0.03));
        r.Metrics["cuts_on_cue_ratio"] = aligned / (double)cuts.Count;
        if (aligned >= cuts.Count * 0.9 && Math.Abs(cuts.Count + 1 - cues.Count) <= 1)
            r.Add("cuts_follow_cues", QaSeverity.Warning, "Har subtitr cue'si alohida sahna bo'lib kesilgan. Sahnalar ma'no bo'yicha (scene plan) bo'linsin, pauzalarda tasvir davom etsin.");
    }

    public static void CheckSubtitles(IReadOnlyList<SubtitleCue> cues, double audioDuration, int maxChars, QaReport r)
    {
        r.Checked.Add("subtitles");
        if (cues.Count == 0) { r.Add("no_subtitles", QaSeverity.Error, "Subtitr fayli bo'sh.", fixable: false); return; }
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            foreach (var line in c.Text.Split('\n'))
                if (line.Length > maxChars + 8)
                    r.Add("subtitle_line_long", QaSeverity.Warning, $"Subtitr qatori uzun ({line.Length} belgi): \"{line}\"", c.Start, fixable: false);
            if (c.End - c.Start < 0.5) r.Add("subtitle_flash", QaSeverity.Warning, $"Subtitr juda qisqa ko'rinadi ({c.End - c.Start:0.00}s).", c.Start, fixable: false);
            if (i > 0 && c.Start < cues[i - 1].End - 0.01) r.Add("subtitle_overlap", QaSeverity.Warning, "Subtitrlar ustma-ust tushgan.", c.Start, fixable: false);
        }
        if (cues[^1].End > audioDuration + 0.5) r.Add("subtitle_after_audio", QaSeverity.Warning, "Oxirgi subtitr audio tugagandan keyin ham ko'rinadi.", cues[^1].End, fixable: false);
    }

    public static void CheckRenderedStreams(double videoDuration, double audioDuration, bool hasVideo, bool hasAudio,
        int width, int height, VideoFormat format, QaReport r)
    {
        r.Checked.Add("streams");
        r.Metrics["video_duration"] = videoDuration;
        if (!hasVideo) r.Add("no_video_stream", QaSeverity.Error, "MP4 ichida video oqimi yo'q.", fixable: false);
        if (!hasAudio) r.Add("no_audio_stream", QaSeverity.Error, "MP4 ichida audio oqimi yo'q.", fixable: false);
        if (videoDuration < audioDuration - 0.15)
            r.Add("video_shorter_than_audio", QaSeverity.Error, $"Video {videoDuration:0.00}s, audio {audioDuration:0.00}s: ovoz kesilib qolgan.");
        if (videoDuration > audioDuration + 3.5)
            r.Add("video_much_longer", QaSeverity.Error, $"Video {videoDuration:0.00}s, audio {audioDuration:0.00}s: oxirida ortiqcha bo'sh joy.");
        var portrait = height > width;
        if (format == VideoFormat.Shorts9x16 && !portrait) r.Add("orientation", QaSeverity.Error, $"Shorts formati uchun vertikal video kerak, render {width}x{height}.");
        if (format == VideoFormat.Landscape16x9 && portrait) r.Add("orientation", QaSeverity.Error, $"16:9 format uchun gorizontal video kerak, render {width}x{height}.");
    }

    public static void CheckAudioLevels(double maxVolumeDb, double? integratedLufs, double targetLufs, QaReport r)
    {
        r.Checked.Add("audio_levels");
        r.Metrics["max_volume_db"] = maxVolumeDb;
        if (integratedLufs is double l) r.Metrics["integrated_lufs"] = l;
        if (maxVolumeDb >= -0.05) r.Add("clipping", QaSeverity.Error, $"Audio clipping: maksimal daraja {maxVolumeDb:0.0} dB.", fixable: false);
        if (integratedLufs is double lufs && Math.Abs(lufs - targetLufs) > 3)
            r.Add("loudness", QaSeverity.Warning, $"Umumiy balandlik {lufs:0.0} LUFS (maqsad {targetLufs}).", fixable: false);
    }

    /// <summary>Qora segmentlarni timeline bilan solishtiradi: klip bo'lmagan joydagi qora - xato; klip ichidagi qora - ogohlantirish.</summary>
    public static void CheckBlack(IReadOnlyList<BlackSegment> black, FilmTimeline tl, double audioDuration, IReadOnlyCollection<string> intentionallyBlackPages, QaReport r)
    {
        r.Checked.Add("black_frames");
        var visual = tl.Clips.Where(c => c.IsVisual && c.Length is > 0 && !tl.HiddenTracks.Contains(c.Track)).ToList();
        foreach (var b in black)
        {
            var mid = (b.Start + b.End) / 2;
            if (mid > audioDuration) continue;
            var covering = visual.Where(c => c.At <= mid && c.End >= mid).ToList();
            if (covering.Count == 0)
                r.Add("black_gap", QaSeverity.Error, $"{b.Start:0.00}-{b.End:0.00}s qora ekran va bu vaqtda klip yo'q.", b.Start);
            else if (covering.Any(c => intentionallyBlackPages.Contains(c.Src)))
                r.Add("black_intentional", QaSeverity.Info, $"{b.Start:0.00}-{b.End:0.00}s ataylab qora sahna ({string.Join(", ", covering.Select(c => c.Src))}).", b.Start);
            else if (b.Duration >= 1.0)
                r.Add("black_in_clip", QaSeverity.Error, $"{b.Start:0.00}-{b.End:0.00}s ({b.Duration:0.0}s) to'liq qora tasvir, garchi '{string.Join(", ", covering.Select(c => c.Src))}' ko'rinishi kerak edi. Sahifa chizilmayapti yoki ataylab qora bo'lsa BUILD_NOTES.md ga 'intentionally black: <fayl>' deb yozing.", b.Start);
            else
                r.Add("black_short", QaSeverity.Warning, $"{b.Start:0.00}-{b.End:0.00}s qisqa qora kadr.", b.Start);
        }
    }

    public static void CheckFrameStats(IReadOnlyList<(double At, double MeanLuma, double StdLuma)> frames, QaReport r)
    {
        r.Checked.Add("sample_frames");
        foreach (var f in frames)
        {
            if (f.StdLuma < 2.0 && f.MeanLuma > 235)
                r.Add("white_frame", QaSeverity.Error, $"{f.At:0.00}s da kadr butunlay oq (sahifa yuklanmagan bo'lishi mumkin).", f.At);
            else if (f.StdLuma < 1.0 && f.MeanLuma > 8)
                r.Add("flat_frame", QaSeverity.Warning, $"{f.At:0.00}s da kadr bir xil rang (hech narsa chizilmagan bo'lishi mumkin).", f.At);
        }
    }
}
