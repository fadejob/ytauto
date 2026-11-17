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
        AppendDesignBar(sb, w, h, tpl is not null || refFrames.Count > 0);
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

    /// <summary>
    /// Sahna sahifalari (iframe) uchun dizayn sifati talablari. Har doim qo'shiladi (foydalanuvchi o'z montaj promptini yozgan bo'lsa ham):
    /// oldingi videolarda kontent kichik, yuqoriga tiqilgan, kadrning yarmi bo'sh, sahna boshida qora/xira kadr bo'lardi.
    /// </summary>
    private static void AppendDesignBar(StringBuilder sb, int w, int h, bool hasTemplate)
    {
        var vertical = h > w;
        var px = (double v) => Math.Round(v * Math.Min(w, h) / 1080.0).ToString(CultureInfo.InvariantCulture);
        sb.AppendLine("## Design quality bar (every scene page; the result must look like a polished, professional explainer)");
        sb.AppendLine();
        sb.AppendLine(hasTemplate
            ? "The template/reference sets the colours, fonts and layout; apply the rules below inside that style."
            : "No template: build one consistent visual system for the whole video and reuse it on every page (shared `film/style.css`).");
        sb.AppendLine();
        sb.AppendLine("Composition");
        sb.AppendLine($"- Every page fills the {w}x{h} stage edge to edge (`html,body{{margin:0;width:100%;height:100%;overflow:hidden}}`). No white default background, no scrollbars, no browser margins.");
        sb.AppendLine("- One clear focal visual per scene that EXPLAINS the narration (diagram, chart, illustrated object, process steps, comparison, big number) —");
        sb.AppendLine($"  it occupies roughly 55–75% of the free area above the subtitle zone, not a small icon floating in empty space. {(vertical ? "Stack headline above the visual, centred." : "Use a balanced layout: headline + visual, or split left/right.")}");
        sb.AppendLine("- Respect the safe area: 6% margins on every side, and the lower 22% stays free of key text (burned-in subtitles go there). Centre the composition in the remaining area, not stuck to the top.");
        sb.AppendLine("- Vary layouts between scenes (full visual, split, list of 2–4 items, big stat, timeline) while keeping the same colours and type, so consecutive scenes do not look identical.");
        sb.AppendLine();
        sb.AppendLine("Look");
        sb.AppendLine("- Background with depth, never flat: a subtle gradient or vignette plus one soft decorative layer (blurred shapes, grid, glow). Keep it calm so text stays readable.");
        sb.AppendLine("- Palette: one background family, one main accent, one secondary accent, near-white text. Contrast of text vs background at least 7:1. Never low-opacity or dark-on-dark headlines.");
        sb.AppendLine($"- Typography: one font family from the system (e.g. \"Segoe UI\", Arial; no web fonts). Headline {px(64)}–{px(96)} px, bold, max 6 words; labels at least {px(30)} px; one or two key words in the accent colour.");
        sb.AppendLine($"- Shapes look finished: rounded corners, consistent stroke widths ({px(4)}–{px(6)} px), soft shadows or glow on the focal element, icons drawn in SVG with the same style everywhere. No emoji, no clip-art look, no placeholder boxes.");
        sb.AppendLine();
        sb.AppendLine("Motion (the picture depends only on t)");
        sb.AppendLine("- Frame 0 of every page is already a good-looking, readable frame: the headline and the focal visual are visible at t=0 (at most a quick 0–0.4 s ease-in on top of an already visible layout). Never start a scene on a black, empty or faded frame.");
        sb.AppendLine("- Build the explanation with staged reveals timed to the subtitle words (items appear, lines draw, numbers count up), each 0.3–0.8 s with ease-out; keep a gentle continuous motion (slow drift/scale 1.00→1.04) so no scene is a frozen slide.");
        sb.AppendLine("- Everything stays on screen until the scene ends; no element disappears early, no fade-out to black before the cut.");
        sb.AppendLine();
        sb.AppendLine("Self-review with `openfilm look`: for each scene check frame 0, the middle and the last frame. If any frame is mostly empty, has tiny text, text near the edges or in the subtitle zone, or looks unfinished, redesign that page before finishing.");
        sb.AppendLine();
    }
}
