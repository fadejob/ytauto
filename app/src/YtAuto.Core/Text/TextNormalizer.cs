using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YtAuto.Core;

public static class TextNormalizer
{
    private static readonly Regex WordRx = new(@"[\p{L}\p{N}][\p{L}\p{N}'’ʻʼ`\-]*", RegexOptions.Compiled);

    /// <summary>So'zlarni asl ko'rinishida (tinish belgilarisiz) ajratadi, pozitsiyasi bilan.</summary>
    public static List<(string Word, int Start, int Length)> Tokenize(string text)
    {
        var list = new List<(string, int, int)>();
        foreach (Match m in WordRx.Matches(text ?? "")) list.Add((m.Value, m.Index, m.Length));
        return list;
    }

    /// <summary>Solishtirish uchun: kichik harf, diakritikasiz, apostroflarsiz.</summary>
    public static string NormalizeWord(string w)
    {
        if (string.IsNullOrEmpty(w)) return "";
        var lower = w.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static List<string> NormalizedWords(string text) =>
        Tokenize(text).Select(t => NormalizeWord(t.Word)).Where(w => w.Length > 0).ToList();

    /// <summary>So'z darajasidagi o'xshashlik (1 - WER, LCS asosida), 0..1.</summary>
    public static double WordSimilarity(string a, string b)
    {
        var x = NormalizedWords(a); var y = NormalizedWords(b);
        if (x.Count == 0 && y.Count == 0) return 1;
        if (x.Count == 0 || y.Count == 0) return 0;
        var lcs = Lcs(x, y);
        return 2.0 * lcs / (x.Count + y.Count);
    }

    private static int Lcs(List<string> a, List<string> b)
    {
        var prev = new int[b.Count + 1]; var cur = new int[b.Count + 1];
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
            Array.Clear(cur);
        }
        return prev[b.Count];
    }

    public static string Slug(string s, int max = 40)
    {
        var n = NormalizeWordKeepSpaces(s);
        var slug = Regex.Replace(n, @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > max) slug = slug[..max].Trim('-');
        return slug.Length == 0 ? "video" : slug;
    }

    private static string NormalizeWordKeepSpaces(string s)
    {
        var lower = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in lower)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}
