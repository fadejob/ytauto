using System.Text.RegularExpressions;
using YtAuto.Core.Script;

namespace YtAuto.Core.Demo;

/// <summary>
/// Demo ssenariy uchun deterministik qoidalar: narration'dan sahna ko'rsatmalarini tozalash, takrorlarni olib tashlash,
/// sahnalarni materiallar bilan tekshirish va sahna vaqtlarini ovozdagi so'z vaqtlaridan hisoblash.
/// </summary>
public static class DemoRules
{
    public const int MaxMedia = 30;

    // Ovozga o'qilmasligi kerak: [..], (cut to ...), "Scene 2:", "Narrator:", "On screen:", markdown, havolalar.
    private static readonly Regex Brackets = new(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex Directions = new(
        @"\((?:[^)]*\b(?:cut|scene|shot|zoom|pan|show|screen|music|sfx|sound|pause|beat|transition|fade|b-roll|visual|on[- ]screen|clip|screenshot|video)\b[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Labels = new(
        @"^\s*(?:scene|shot|sahna|narrator|narration|voice ?over|vo|host|speaker|diktor|on[- ]screen(?: text)?|visual|caption)\s*\d*\s*[:\-–]\s*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Markdown = new(@"\*\*|__|^#{1,6}\s+|`", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Urls = new(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Sentence = new(@"[^.!?…]+[.!?…]*", RegexOptions.Compiled);

    /// <summary>Bitta sahna matnini ovoz uchun tozalaydi (faqat aytiladigan so'zlar qoladi).</summary>
    public static string CleanNarration(string text)
    {
        var t = text ?? "";
        t = Brackets.Replace(t, " ");
        t = Directions.Replace(t, " ");
        t = Labels.Replace(t, "");
        t = Markdown.Replace(t, "");
        t = Urls.Replace(t, "");
        t = t.Replace('“', '"').Replace('”', '"');
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\s+([,.!?;:])", "$1");
        t = Regex.Replace(t, @"\s*\n\s*", " ");
        return t.Trim();
    }

    /// <summary>
    /// Sahnalarni tozalaydi: narration tozalanadi, avvalgi sahnalarda aynan aytilgan gaplar olib tashlanadi, bo'sh sahnalar
    /// tashlanadi, noma'lum media_id lar eng yaqin ma'lum material bilan almashtiriladi (yoki tashlanadi).
    /// </summary>
    public static List<DemoScene> CleanScenes(IEnumerable<DemoScene> scenes, IReadOnlyCollection<string> mediaIds, List<string>? notes = null)
    {
        var known = new HashSet<string>(mediaIds, StringComparer.Ordinal);
        var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DemoScene>();
        foreach (var s in scenes ?? Array.Empty<DemoScene>())
        {
            var id = (s.MediaId ?? "").Trim();
            if (!known.Contains(id))
            {
                var fallback = list.LastOrDefault()?.MediaId ?? mediaIds.FirstOrDefault();
                if (fallback is null) continue;
                notes?.Add($"Noma'lum media_id \"{id}\" - \"{fallback}\" bilan almashtirildi.");
                id = fallback;
            }
            var kept = new List<string>();
            foreach (Match m in Sentence.Matches(CleanNarration(s.Narration)))
            {
                var sentence = m.Value.Trim();
                if (sentence.Length == 0) continue;
                var key = Regex.Replace(sentence.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
                if (key.Length == 0) continue;
                if (key.Split(' ').Length >= 4 && !said.Add(key)) { notes?.Add($"Takroriy gap olib tashlandi: \"{Short(sentence)}\""); continue; }
                kept.Add(sentence);
            }
            var narration = string.Join(" ", kept).Trim();
            if (narration.Length == 0) continue;
            list.Add(new DemoScene
            {
                MediaId = id, Narration = narration,
                Focus = Single(s.Focus, 200), OnScreenText = Single(CleanNarration(s.OnScreenText), 60),
            });
        }
        return list;
    }

    /// <summary>Yakuniy narration: sahna matnlari ketma-ket (paragraf bilan) - ovoz va subtitr aynan shu matndan.</summary>
    public static string JoinNarration(IEnumerable<DemoScene> scenes) => string.Join("\n\n", scenes.Select(s => s.Narration.Trim()).Where(s => s.Length > 0));

    /// <summary>Sahnalar tekshiruvi: bo'sh emas, har sahnada material, kamida yarim materiallar ishlatilgan.</summary>
    public static List<string> ValidateScenes(IReadOnlyList<DemoScene> scenes, IReadOnlyCollection<string> mediaIds)
    {
        var problems = new List<string>();
        if (scenes.Count == 0) { problems.Add("scenes bo'sh: kamida bitta sahna kerak."); return problems; }
        var used = scenes.Select(s => s.MediaId).Distinct().Count();
        var need = Math.Min(mediaIds.Count, Math.Max(1, (int)Math.Ceiling(mediaIds.Count * 0.5)));
        if (used < need) problems.Add($"Materiallarning faqat {used} tasi ishlatilgan ({mediaIds.Count} ta berilgan): har muhim screenshot/videoga sahna bering.");
        foreach (var s in scenes.Where(s => ScriptValidator.CountWords(s.Narration) > 140))
            problems.Add($"\"{Short(s.Narration)}\" sahnasi juda uzun: bitta material ustida 140 so'zdan ko'p gapirilmasin, sahnani bo'ling.");
        return problems;
    }

    /// <summary>
    /// Sahna vaqtlari: yakuniy matndagi so'zlar ketma-ketligi ovozdagi so'z vaqtlariga (alignment) teng (bir so'z - bitta yozuv).
    /// Har sahna o'z so'zlari soni bo'yicha kesiladi; sahnalar orasida bo'shliq qolmaydi, birinchisi 0 dan, oxirgisi ovoz oxirigacha.
    /// </summary>
    public static List<(double Start, double End)> SceneTimes(IReadOnlyList<DemoScene> scenes, IReadOnlyList<AlignedWord> words, double audioSec)
    {
        var result = new List<(double, double)>();
        if (scenes.Count == 0) return result;
        var counts = scenes.Select(s => TextNormalizer.Tokenize(s.Narration).Count).ToList();
        var total = counts.Sum();
        int idx = 0;
        double prevEnd = 0;
        for (int i = 0; i < scenes.Count; i++)
        {
            double start = i == 0 ? 0 : prevEnd;
            idx += counts[i];
            double end;
            if (i == scenes.Count - 1 || words.Count == 0) end = i == scenes.Count - 1 ? audioSec : audioSec * idx / Math.Max(1, total);
            else
            {
                // Sahnaning oxirgi so'zi tugashi va keyingisining birinchi so'zi boshlanishi o'rtasi (pauza o'rtasida kesiladi).
                var last = words[Math.Clamp(idx - 1, 0, words.Count - 1)];
                var next = words[Math.Clamp(idx, 0, words.Count - 1)];
                end = next.Start > last.End ? (last.End + next.Start) / 2 : last.End;
            }
            end = Math.Clamp(end, start + 0.5, Math.Max(start + 0.5, audioSec));
            result.Add((Math.Round(start, 2), Math.Round(end, 2)));
            prevEnd = end;
        }
        // Oxirgisi aynan ovoz oxirida tugaydi.
        if (result.Count > 0) result[^1] = (result[^1].Item1, Math.Round(Math.Max(result[^1].Item1 + 0.5, audioSec), 2));
        return result;
    }

    private static string Single(string? s, int max)
    {
        var t = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
