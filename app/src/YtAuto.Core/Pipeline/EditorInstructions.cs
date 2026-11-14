using System.Globalization;
using System.Text;
using YtAuto.Core.Domain;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Claude uchun instructions.md: saqlangan Claude (montaj) prompti + subtitr + ovoz + shablon (bo'lsa) + worker tekshiradigan
/// texnik talablar. Sahnalarni Claude subtitr ma'nosiga qarab o'zi belgilaydi.
/// </summary>
internal static class EditorInstructions
{
    public static string Build(JobContext c, VideoScript script, IReadOnlyList<SubtitleCue> cues, double audioSec,
        TemplateDefinition? tpl, IReadOnlyList<string> refFrames)
    {
        var (w, h) = c.StageSize;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Task: build the OpenFilm project for one video");
        sb.AppendLine();
        sb.AppendLine("You are running unattended in a fresh session. Never ask questions. Work only inside this folder.");
        sb.AppendLine("Do not render the final video and do not delete anything outside `film/`.");
        sb.AppendLine();
        sb.AppendLine("## Brief (Claude prompt from the channel settings)");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(c.S.Claude.MontagePrompt) ? DefaultPrompts.Montage : c.S.Claude.MontagePrompt.Trim());
        sb.AppendLine();
        sb.AppendLine("## Read first");
        sb.AppendLine();
        sb.AppendLine("- `openfilm-edit-skill.md`: OpenFilm's own rules for film.html and pages. Read it whole before writing anything.");
        sb.AppendLine();
        sb.AppendLine("## Materials");
        sb.AppendLine();
        sb.AppendLine("- `subtitles.txt`: the full subtitle text. It is exactly what the voice says.");
        sb.AppendLine("- `subtitles.srt` and `film/assets/voice.vtt`: the same subtitles with real timings taken from the voice (voice.vtt has a time tag at each word). Do not edit, move or rename voice.vtt.");
        sb.AppendLine($"- `film/assets/voice.wav`: the voice, **{audioSec.ToString("0.000", inv)} seconds**. You cannot hear it; use the subtitle timings.");
        if (tpl is { Kind: TemplateKind.OpenFilmProject })
        {
            sb.AppendLine($"- Template: the project in `film/` is a COPY of the channel template \"{tpl.Name}\". Keep its style, fonts, colours, shared files and layout;");
            sb.AppendLine("  replace its placeholder content with this video's content. Remove template clips that do not belong in this video.");
        }
        if (refFrames.Count > 0)
        {
            sb.AppendLine($"- Template: style reference frames from the channel's sample video{(tpl is null ? "" : $" \"{tpl.Name}\"")} (look at them with the Read tool; match colours, typography and layout, not the content):");
            foreach (var f in refFrames) sb.AppendLine($"  - `{f}`");
        }
        if (tpl is null) sb.AppendLine("- Template: none for this video. Use the default design from the brief.");
        if (!string.IsNullOrWhiteSpace(tpl?.Notes)) sb.AppendLine($"- Template notes: {tpl!.Notes}");
        sb.AppendLine();
        sb.AppendLine("## Subtitles with timings (seconds)");
        sb.AppendLine();
        foreach (var q in cues)
            sb.AppendLine($"- {q.Start.ToString("0.00", inv)}–{q.End.ToString("0.00", inv)}: {q.Text.Replace("\n", " ")}");
        sb.AppendLine();
        sb.AppendLine("Group these lines into scenes by meaning (one idea per scene). Make one page per scene and cut them end to end on one track.");
        sb.AppendLine();
        sb.AppendLine("## Hard requirements (the worker checks them automatically)");
        sb.AppendLine();
        sb.AppendLine($"1. `film/film.html` with `<meta name=\"viewport\" content=\"width={w}, height={h}\">`.");
        sb.AppendLine($"2. Visual clips cover 0.00 → {audioSec.ToString("0.00", inv)} s with no gap (every page clip has `#t=0,<length>` and an `at`). A moment with no clip is black, which is an error.");
        sb.AppendLine("3. `assets/voice.wav` on its own audio track, `at=\"0\"`, volume 1, speed 1. Optional music on another track at volume ≤ 0.2.");
        sb.AppendLine("4. No subtitles drawn in pages (they are burned in from voice.vtt). Keep the lower 22% of the frame free of key text.");
        sb.AppendLine("5. Every page follows \"the picture depends only on t\". No external network resources (no CDN fonts or images); everything local.");
        sb.AppendLine("6. All text inside the safe area and readable on a phone. Nothing cut off at the edges.");
        sb.AppendLine("7. Leave `film/assets/voice.wav` and `film/assets/voice.vtt` untouched.");
        sb.AppendLine();
        sb.AppendLine("## Check, then finish");
        sb.AppendLine();
        sb.AppendLine("- Run `openfilm look` in `film/` and open the contact sheet PNGs it prints. Use `openfilm look <t>` for single frames around each scene start.");
        sb.AppendLine("- Fix anything empty, cut off, overlapping or unreadable, then look again.");
        sb.AppendLine("- Write `film/BUILD_NOTES.md`: the files you made, each scene's start/end, and for any scene that is black on purpose a line `intentionally black: <page>.html`.");
        sb.AppendLine("- Reply DONE.");
        return sb.ToString();
    }
}
