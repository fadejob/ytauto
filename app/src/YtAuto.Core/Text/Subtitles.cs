using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YtAuto.Core;

public sealed class WordTiming
{
    [JsonPropertyName("word")] public string Word { get; set; } = "";
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
}

public sealed class AlignedWord
{
    [JsonPropertyName("word")] public string Word { get; set; } = "";
    /// <summary>Asl matndagi boshlanish indeksi.</summary>
    [JsonPropertyName("char_start")] public int CharStart { get; set; }
    [JsonPropertyName("char_end")] public int CharEnd { get; set; }
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    /// <summary>true - transkripsiyadagi so'zga mos keldi; false - vaqti interpolyatsiya qilingan.</summary>
    [JsonPropertyName("matched")] public bool Matched { get; set; }
}

public sealed class AlignmentResult
{
    [JsonPropertyName("words")] public List<AlignedWord> Words { get; set; } = new();
    [JsonPropertyName("matched_ratio")] public double MatchedRatio { get; set; }
    [JsonPropertyName("similarity")] public double Similarity { get; set; }
    [JsonPropertyName("asr_text")] public string AsrText { get; set; } = "";
    [JsonPropertyName("mismatches")] public List<string> Mismatches { get; set; } = new();
}

public sealed class SubtitleCue
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<AlignedWord> Words { get; set; } = new();
}

/// <summary>
/// ASR so'z vaqtlarini asl ssenariy so'zlariga moslashtiradi. Subtitr matni har doim ASL ssenariydan olinadi;
/// farqlar yashirilmaydi - <see cref="AlignmentResult.Mismatches"/> ga yoziladi.
/// </summary>
public static class TranscriptAligner
{
    public static AlignmentResult Align(string originalText, IReadOnlyList<WordTiming> asr, double audioDuration)
    {
        var orig = TextNormalizer.Tokenize(originalText);
        var o = orig.Select(t => TextNormalizer.NormalizeWord(t.Word)).ToArray();
        var a = asr.Select(w => TextNormalizer.NormalizeWord(w.Word)).ToArray();
        int n = o.Length, m = a.Length;
        var result = new AlignmentResult { AsrText = string.Join(" ", asr.Select(w => w.Word.Trim())) };
        if (n == 0) return result;

        // Needleman-Wunsch: match=0 (yoki fuzzy ~0.4), sub=1, gap=1.
        var cost = new double[n + 1, m + 1];
        var back = new byte[n + 1, m + 1]; // 1=diag, 2=up(orig gap in asr), 3=left(asr extra)
        for (int i = 1; i <= n; i++) { cost[i, 0] = i; back[i, 0] = 2; }
        for (int j = 1; j <= m; j++) { cost[0, j] = j; back[0, j] = 3; }
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                var sub = cost[i - 1, j - 1] + MatchCost(o[i - 1], a[j - 1]);
                var up = cost[i - 1, j] + 1;
                var left = cost[i, j - 1] + 1;
                if (sub <= up && sub <= left) { cost[i, j] = sub; back[i, j] = 1; }
                else if (up <= left) { cost[i, j] = up; back[i, j] = 2; }
                else { cost[i, j] = left; back[i, j] = 3; }
            }

        var map = new int[n]; Array.Fill(map, -1);
        int x = n, y = m;
        while (x > 0 || y > 0)
        {
            var b = back[x, y];
            if (b == 1)
            {
                if (MatchCost(o[x - 1], a[y - 1]) < 0.75) map[x - 1] = y - 1;
                else result.Mismatches.Add($"\"{orig[x - 1].Word}\" ≠ \"{asr[y - 1].Word.Trim()}\"");
                x--; y--;
            }
            else if (b == 2) { result.Mismatches.Add($"ovozda topilmadi: \"{orig[x - 1].Word}\""); x--; }
            else { result.Mismatches.Add($"ortiqcha (ASR): \"{asr[y - 1].Word.Trim()}\""); y--; }
        }
        result.Mismatches.Reverse();

        var words = new List<AlignedWord>(n);
        for (int i = 0; i < n; i++)
        {
            var w = new AlignedWord { Word = orig[i].Word, CharStart = orig[i].Start, CharEnd = orig[i].Start + orig[i].Length };
            if (map[i] >= 0) { w.Start = asr[map[i]].Start; w.End = asr[map[i]].End; w.Matched = true; }
            words.Add(w);
        }
        Interpolate(words, audioDuration);
        result.Words = words;
        result.MatchedRatio = words.Count(w => w.Matched) / (double)n;
        result.Similarity = TextNormalizer.WordSimilarity(originalText, result.AsrText);
        return result;
    }

    /// <summary>
    /// TTS tashlab ketgan joylar: ketma-ket kamida <paramref name="minWords"/> ta asl so'z ASR'da topilmagan VA ularga deyarli vaqt
    /// qolmagan (qo'shni topilgan so'zlar orasidagi oraliq so'z boshiga <paramref name="maxSecPerWord"/> dan kam). ASR shunchaki
    /// noto'g'ri eshitgan so'zlarning vaqti bor - ular bu yerga tushmaydi. Natija: (birinchi so'z indeksi, so'zlar soni).
    /// </summary>
    public static List<(int First, int Count)> DroppedRuns(AlignmentResult res, double audioDuration, int minWords = 5, double maxSecPerWord = 0.12)
    {
        var runs = new List<(int, int)>();
        var w = res.Words;
        for (int i = 0; i < w.Count;)
        {
            if (w[i].Matched) { i++; continue; }
            var j = i;
            while (j < w.Count && !w[j].Matched) j++;
            var count = j - i;
            var from = i > 0 ? w[i - 1].End : 0;
            var to = j < w.Count ? w[j].Start : audioDuration;
            if (count >= minWords && to - from < count * maxSecPerWord) runs.Add((i, count));
            i = j;
        }
        return runs;
    }

    private static double MatchCost(string x, string y)
    {
        if (x == y) return 0;
        if (x.Length == 0 || y.Length == 0) return 1;
        var d = Levenshtein(x, y);
        var ratio = 1.0 - d / (double)Math.Max(x.Length, y.Length);
        // Raqamlar so'z bilan aytiladi ("2" vs "two"): fuzzy emas, almashtirish sifatida qoladi.
        return ratio >= 0.7 ? 0.4 : 1;
    }

    private static int Levenshtein(string s, string t)
    {
        var d = new int[t.Length + 1];
        for (int j = 0; j <= t.Length; j++) d[j] = j;
        for (int i = 1; i <= s.Length; i++)
        {
            int prev = d[0]; d[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (s[i - 1] == t[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[t.Length];
    }

    /// <summary>Mos kelmagan so'zlar vaqtini qo'shni mos so'zlar orasida harf uzunligiga proporsional taqsimlaydi.</summary>
    private static void Interpolate(List<AlignedWord> words, double audioDuration)
    {
        int i = 0;
        while (i < words.Count)
        {
            if (words[i].Matched) { i++; continue; }
            int j = i;
            while (j < words.Count && !words[j].Matched) j++;
            double from = i > 0 ? words[i - 1].End : 0;
            double to = j < words.Count ? words[j].Start : Math.Max(from, audioDuration);
            if (to < from) to = from;
            double totalChars = 0;
            for (int k = i; k < j; k++) totalChars += Math.Max(1, words[k].Word.Length);
            double t = from;
            for (int k = i; k < j; k++)
            {
                var dur = (to - from) * Math.Max(1, words[k].Word.Length) / totalChars;
                words[k].Start = t; words[k].End = t + dur; t += dur;
            }
            i = j;
        }
        // Monotonlikni ta'minlash
        for (int k = 1; k < words.Count; k++)
        {
            if (words[k].Start < words[k - 1].Start) words[k].Start = words[k - 1].Start;
            if (words[k].End < words[k].Start) words[k].End = words[k].Start;
        }
    }
}

public static class SubtitleBuilder
{
    /// <param name="maxChars">Bir qatordagi maksimal belgi (16:9 uchun ~42, 9:16 uchun ~30).</param>
    public static List<SubtitleCue> BuildCues(string originalText, List<AlignedWord> words, int maxChars = 42,
        double maxDuration = 4.5, double minDuration = 0.8)
    {
        var cues = new List<SubtitleCue>();
        var current = new List<AlignedWord>();

        string TextOf(List<AlignedWord> ws)
        {
            if (ws.Count == 0) return "";
            var start = ws[0].CharStart;
            var end = ws[^1].CharEnd;
            // So'zdan keyingi tinish belgilarini qo'shamiz (.,!?;:…»")
            while (end < originalText.Length && ".,!?;:…»\"'”)".Contains(originalText[end])) end++;
            var s = originalText.Substring(start, end - start);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        void Flush()
        {
            if (current.Count == 0) return;
            cues.Add(new SubtitleCue { Start = current[0].Start, End = current[^1].End, Text = TextOf(current), Words = new(current) });
            current = new List<AlignedWord>();
        }

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (current.Count > 0)
            {
                var candidate = TextOf(new List<AlignedWord>(current) { w });
                var dur = w.End - current[0].Start;
                if (candidate.Length > maxChars || dur > maxDuration) Flush();
            }
            current.Add(w);
            // Gap yoki gap oxirida bo'lish
            var after = w.CharEnd < originalText.Length ? originalText[w.CharEnd] : ' ';
            var sentenceEnd = ".!?…".Contains(after);
            var clauseEnd = ",;:".Contains(after);
            var len = TextOf(current).Length;
            var nextGap = i + 1 < words.Count ? words[i + 1].Start - w.End : 0;
            if (sentenceEnd || nextGap > 0.6 || (clauseEnd && len > maxChars * 0.55)) Flush();
        }
        Flush();

        // Minimal davomiylik: keyingi cue boshlanishigacha cho'zamiz, ustma-ust tushirmaymiz.
        for (int i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            var limit = i + 1 < cues.Count ? cues[i + 1].Start : double.MaxValue;
            if (c.End - c.Start < minDuration) c.End = Math.Min(c.Start + minDuration, limit);
            if (c.End <= c.Start) c.End = c.Start + 0.05;
        }
        return cues;
    }

    public static string ToVtt(IEnumerable<SubtitleCue> cues, bool wordTags = true)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var c in cues)
        {
            sb.Append(Ts(c.Start, '.')).Append(" --> ").Append(Ts(c.End, '.')).Append('\n');
            if (wordTags && c.Words.Count > 1)
            {
                // OpenFilm: cue boshida birinchi so'z, keyingi har so'z oldida <hh:mm:ss.fff> tegi.
                var text = c.Text;
                var baseIdx = c.Words[0].CharStart;
                var line = new StringBuilder();
                int pos = 0;
                for (int k = 0; k < c.Words.Count; k++)
                {
                    var rel = c.Words[k].CharStart - baseIdx;
                    if (rel < pos || rel > text.Length) { line.Clear(); line.Append(text); pos = text.Length; break; }
                    line.Append(text, pos, rel - pos);
                    if (k > 0) line.Append('<').Append(Ts(c.Words[k].Start, '.')).Append('>');
                    pos = rel;
                }
                if (pos < text.Length) line.Append(text, pos, text.Length - pos);
                sb.Append(line.ToString().Replace("\n", " ")).Append("\n\n");
            }
            else sb.Append(c.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder(); int n = 1;
        foreach (var c in cues)
        {
            sb.Append(n++).Append('\n').Append(Ts(c.Start, ',')).Append(" --> ").Append(Ts(c.End, ',')).Append('\n')
              .Append(c.Text).Append("\n\n");
        }
        return sb.ToString();
    }

    public static string Ts(double seconds, char msSep)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}{3}{4:000}", (int)t.TotalHours, t.Minutes, t.Seconds, msSep, t.Milliseconds);
    }
}

public static class VttParser
{
    private static readonly Regex TimeLine = new(@"^\s*(?<a>[\d:.,]+)\s*-->\s*(?<b>[\d:.,]+)", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.Compiled);

    public static List<SubtitleCue> Parse(string vtt)
    {
        var cues = new List<SubtitleCue>();
        var lines = vtt.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = TimeLine.Match(lines[i]);
            if (!m.Success) continue;
            var text = new StringBuilder();
            int j = i + 1;
            while (j < lines.Length && lines[j].Trim().Length > 0) { if (text.Length > 0) text.Append('\n'); text.Append(lines[j]); j++; }
            cues.Add(new SubtitleCue { Start = ParseTs(m.Groups["a"].Value), End = ParseTs(m.Groups["b"].Value), Text = Tag.Replace(text.ToString(), "").Trim() });
            i = j;
        }
        return cues;
    }

    public static double ParseTs(string s)
    {
        s = s.Replace(',', '.');
        var parts = s.Split(':');
        double sec = 0;
        foreach (var p in parts) sec = sec * 60 + double.Parse(p, CultureInfo.InvariantCulture);
        return sec;
    }
}
