using System.Text.RegularExpressions;
using YtAuto.Core.Domain;
using YtAuto.Core;

namespace YtAuto.Core.Script;

/// <summary>
/// Format bo'yicha video uzunligi (bot: lib/video-rules.ts - raqamlar bir xil bo'lishi kerak).
/// 16:9 (keng ekran) - 2-3 daqiqa; Shorts 9:16 va kvadrat 1:1 - 55 soniyadan oshmaydi.
/// MaxAudioSec - ovozning eng ko'p uzunligi: video oxirida bir-ikki soniyalik yakun uchun joy qoladi (video MaxSec dan oshmaydi).
/// </summary>
public sealed record VideoLength(int MinSec, int MaxSec, int TargetSec, int MaxAudioSec)
{
    public static readonly VideoLength Landscape = new(120, 180, 150, 176);
    public static readonly VideoLength Short = new(30, 55, 46, 52);

    public static VideoLength For(VideoFormat format) => format == VideoFormat.Landscape16x9 ? Landscape : Short;

    /// <summary>Ovoz chegaradan oshsa shu koeffitsientgacha tezlashtiriladi (balandligi o'zgarmaydi); undan ko'pi - xato.</summary>
    public const double MaxTempo = 1.2;

    /// <summary>
    /// TTS ovozining haqiqiy tezligi (so'z/soniya, bo'laklar orasidagi pauzalar bilan): MOSS-TTS-Nano inglizchada ~1.9-2.0.
    /// <see cref="LanguageNames.WordsPerSecond"/> (oddiy nutq) dan sekinroq - uzunlik shu bilan hisoblanadi.
    /// </summary>
    public static double TtsWordsPerSecond(string language) => LanguageNames.WordsPerSecond(language) * 0.8;

    public int TargetWords(string language) => (int)Math.Round(TargetSec * TtsWordsPerSecond(language));
    public int MinWords(string language) => (int)Math.Round(MinSec * TtsWordsPerSecond(language));
    public int MaxWords(string language) => (int)Math.Floor(MaxAudioSec * TtsWordsPerSecond(language));

    public static string FormatName(VideoFormat f) => f switch
    {
        VideoFormat.Shorts9x16 => "Shorts (9:16)",
        VideoFormat.Square1x1 => "kvadrat (1:1) video",
        _ => "16:9 video",
    };

    public string Label => this == Landscape ? "2-3 daqiqa" : $"{MaxSec} soniyagacha";
}

/// <summary>
/// Video boshlanishi (hook): foydalanuvchi botda kiritgan boshlang'ich savol. Ssenariy aynan shu so'zlar bilan boshlanadi
/// (katta-kichik harf va tinish belgilari farqi hisobga olinmaydi), montaj shu savoldan quriladi.
/// </summary>
public static class HookRule
{
    public static string Normalize(string? hook) => Regex.Replace(hook ?? "", @"\s+", " ").Trim();

    private static List<string> Tokens(string? s) =>
        Regex.Matches((s ?? "").ToLowerInvariant().Replace('’', '\''), @"[\p{L}\p{N}][\p{L}\p{N}']*").Select(m => m.Value).ToList();

    public static int WordCount(string? hook) => Tokens(hook).Count;

    /// <summary>Matn hook so'zlari bilan boshlanadimi.</summary>
    public static bool StartsWithHook(string? text, string? hook)
    {
        var h = Tokens(hook);
        if (h.Count == 0) return true;
        var t = Tokens(text);
        return t.Count >= h.Count && t.Take(h.Count).SequenceEqual(h);
    }

    /// <summary>
    /// Ssenariy hook bilan boshlanmasa, hook aynan foydalanuvchi yozganidek boshiga qo'yiladi. true - matn o'zgardi.
    /// (Gemini'ga ikki marta imkon beriladi; oxirgi urinishda ham boshlamasa - shu.)
    /// </summary>
    public static bool Prepend(VideoScript s, string? hook)
    {
        var h = Normalize(hook);
        if (h.Length == 0) return false;
        ScriptValidator.Canonicalize(s);
        if (StartsWithHook(s.SubtitleText, h)) return false;
        s.SubtitleText = h + " " + s.SubtitleText.TrimStart();
        s.NarrationText = s.SubtitleText;
        return true;
    }

    /// <summary>Hook ovozda qachon tugaydi (subtitr vaqtlaridan): montaj ochilish sahnasini shu vaqtgacha quradi. Topilmasa null.</summary>
    public static double? EndSec(IReadOnlyList<SubtitleCue> cues, string? hook)
    {
        var need = Tokens(hook).Count;
        if (need == 0 || cues.Count == 0) return null;
        var seen = 0;
        foreach (var cue in cues)
        {
            if (cue.Words.Count > 0)
            {
                foreach (var w in cue.Words)
                {
                    seen += Tokens(w.Word).Count;
                    if (seen >= need) return w.End;
                }
            }
            else
            {
                seen += Tokens(cue.Text).Count;
                if (seen >= need) return cue.End;
            }
        }
        return null;
    }
}
