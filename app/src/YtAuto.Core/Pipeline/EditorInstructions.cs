using System.Globalization;
using System.Text;
using YtAuto.Core.Domain;
using YtAuto.Core.Script;
using YtAuto.Core.Visuals;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Claude uchun instructions.md: saqlangan Claude (montaj) prompti + subtitr + ovoz + shablon (bo'lsa) + worker tekshiradigan
/// texnik talablar. Sahnalarni Claude subtitr ma'nosiga qarab o'zi belgilaydi.
/// </summary>
internal static class EditorInstructions
{
    /// <summary>Kanal tahlilidan (bot) montaj uslubi: shablon/brief bilan birga Claude'ga beriladi.</summary>
    public const string MontageHeading = "## Channel style (learned from this channel's latest videos; keep new videos consistent with it)";

    public static string Build(JobContext c, VideoScript script, IReadOnlyList<SubtitleCue> cues, double audioSec,
        TemplateDefinition? tpl, IReadOnlyList<string> refFrames, bool hasStyleGuide = false, MediaManifest? media = null, bool hasMotionGuide = false)
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
        if (c.S.ChannelContext?.MontageBlock is { Length: > 0 } channel)
        {
            sb.AppendLine(MontageHeading);
            sb.AppendLine();
            sb.AppendLine(channel);
            sb.AppendLine();
        }
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
        if (hasStyleGuide)
            sb.AppendLine($"- **`reference/STYLE_GUIDE.md`: the channel's design system, extracted from the template{(tpl is null ? "" : $" \"{tpl.Name}\"")}. It is binding: exact hex colours, fonts, layout, image treatment, brand elements, motion. Read it whole before designing.**");
        if (hasMotionGuide)
            sb.AppendLine($"- **`reference/MOTION_GUIDE.md`: how the template{(tpl is null ? "" : $" \"{tpl.Name}\"")} MOVES, analysed from the video itself (not stills): pacing, scene transitions, text/shape/image entrances, durations, staggers, easing, ambient motion, and a CSS recipe. It is binding for all animation; where it differs from the motion section of STYLE_GUIDE.md or the generic motion rules below, MOTION_GUIDE.md wins. Read it whole before animating.**");
        if (refFrames.Count > 0)
        {
            sb.AppendLine($"- Template: {refFrames.Count} frames from the channel's template video{(tpl is null ? "" : $" \"{tpl.Name}\"")}. Open ALL of them with the Read tool before designing; copy their look (colours, typography, layout, backgrounds, how images and icons are framed, recurring brand elements), not their content:");
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
        if (refFrames.Count > 0 || hasStyleGuide || hasMotionGuide) AppendTemplateFidelity(sb, hasMotionGuide);
        if (media is { Assets.Count: > 0 }) AppendMedia(sb, media, inv);
        AppendDesignBar(sb, w, h, tpl is not null || refFrames.Count > 0, hasMotionGuide);
        sb.AppendLine("## Hard requirements (the worker checks them automatically)");
        sb.AppendLine();
        sb.AppendLine($"1. `film/film.html` with `<meta name=\"viewport\" content=\"width={w}, height={h}\">`.");
        sb.AppendLine($"2. Visual clips cover 0.00 → {audioSec.ToString("0.00", inv)} s with no gap (every page clip has `#t=0,<length>` and an `at`). A moment with no clip is black, which is an error.");
        sb.AppendLine("3. `assets/voice.wav` on its own audio track, `at=\"0\"`, volume 1, speed 1. Optional music on another track at volume ≤ 0.2.");
        sb.AppendLine("4. No subtitles drawn in pages (they are burned in from voice.vtt). Keep the lower 22% of the frame free of key text.");
        sb.AppendLine("5. Every page follows \"the picture depends only on t\". No external network resources (no CDN fonts or images); everything local" +
            (media is { Assets.Count: > 0 } ? " (the real images are already in `film/assets/media/`; do not download others)." : "."));
        sb.AppendLine("6. All text inside the safe area and readable on a phone. Nothing cut off at the edges.");
        sb.AppendLine("7. Leave `film/assets/voice.wav` and `film/assets/voice.vtt` untouched.");
        sb.AppendLine("8. `film/cover.html`: the video cover (see below). It is NOT on the timeline.");
        sb.AppendLine();
        AppendCover(sb, script, w, h);
        sb.AppendLine("## Check, then finish");
        sb.AppendLine();
        sb.AppendLine("- Run `openfilm look` in `film/` and open the contact sheet PNGs it prints. Use `openfilm look <t>` for single frames around each scene start.");
        sb.AppendLine("- Fix anything empty, cut off, overlapping or unreadable, then look again.");
        sb.AppendLine("- Write `film/BUILD_NOTES.md`: the files you made, each scene's start/end, and for any scene that is black on purpose a line `intentionally black: <page>.html`.");
        sb.AppendLine("- Reply DONE.");
        return sb.ToString();
    }

    /// <summary>Shablonga sodiqlik: video shablon kanalidan chiqqandek ko'rinishi kerak (render'dan keyin Gemini solishtiradi).</summary>
    private static void AppendTemplateFidelity(StringBuilder sb, bool hasMotionGuide = false)
    {
        sb.AppendLine("## Template fidelity (the most important design goal)");
        sb.AppendLine();
        sb.AppendLine("A viewer of this channel must recognise the video as the channel's own at first glance. After rendering, frames are compared with the template frames and the style guide; a mismatch sends the video back to you.");
        sb.AppendLine("- Before writing pages, put the template's design tokens in `film/style.css` as CSS variables (palette hex codes, font stacks, headline/label sizes, radius, stroke width, shadow, spacing) and use only those variables in every page.");
        sb.AppendLine("- Rebuild the template's recurring elements as reusable parts: background treatment, card/panel style, headline placement and highlight style, labels, lower-third or watermark position, progress or counter elements.");
        sb.AppendLine("- Match the template's density and pacing: as much text per scene, the same balance of text vs picture, similar entrance motion and transitions.");
        sb.AppendLine("- Adapt only the content to this video's topic. Do not import colours, fonts or effects that the template does not use.");
        if (hasMotionGuide)
        {
            sb.AppendLine("- Move like the template: put MOTION_GUIDE.md's durations, staggers and easings in `film/style.css` as CSS variables (its recipe section) and use them for every animation;");
            sb.AppendLine("  use the same entrance types for text, shapes and images, the same scene transition type and length, and a similar scene length and rhythm.");
            sb.AppendLine("  Do not add motion styles the template does not use. Keep OpenFilm's rules: the picture depends only on t, and frame 0 of each page is already readable.");
        }
        sb.AppendLine();
    }

    /// <summary>Ochiq manbalardan yuklangan real rasmlar ro'yxati va ularni shablon uslubida ishlatish qoidalari.</summary>
    private static void AppendMedia(StringBuilder sb, MediaManifest media, CultureInfo inv)
    {
        sb.AppendLine("## Real images, logos and icons (already downloaded, licensed for use)");
        sb.AppendLine();
        sb.AppendLine("Use them where they make the explanation clearer and more real than drawn shapes; each was chosen for the moment shown. You may skip one that does not fit. Do not add other images from the internet.");
        sb.AppendLine();
        foreach (var a in media.Assets.OrderBy(a => a.At))
        {
            var size = a.Width > 0 ? $", {a.Width}x{a.Height}" : "";
            sb.AppendLine($"- `film/{a.File}` ({a.Kind}{size}) ~{a.At.ToString("0.0", inv)} s: {(string.IsNullOrWhiteSpace(a.Purpose) ? a.Query : a.Purpose)}");
        }
        sb.AppendLine();
        sb.AppendLine("How to use them so they look designed, not pasted:");
        sb.AppendLine("- Photos: never stretch. Place them in the template's frame/card style (`object-fit: cover`, the template's radius, border and shadow), or as a full-bleed background under a gradient overlay in the template's colours so text stays readable. A slow Ken Burns move (scale 1.00→1.06 over the scene) keeps them alive. Grade them toward the palette if the template does (e.g. a CSS filter or a coloured overlay with `mix-blend-mode`).");
        sb.AppendLine("- Logos: keep the original colours and proportions, never recolour, crop or distort; put them on a clean plate or card with enough padding, at a size where they are clearly recognisable. Only next to the words that name the brand.");
        sb.AppendLine("- Icons are single-colour SVGs that use `currentColor`: inline the SVG markup in the page (or use it as a CSS `mask-image`) and colour it with the template's accent; keep one stroke weight and size system across the video.");
        sb.AppendLine("- Do not write credits or watermarks on screen; the worker adds the required credits to the video description.");
        sb.AppendLine();
    }

    /// <summary>
    /// Video muqovasi (cover): YouTube thumbnail bo'ladi va MP4 ichiga rasm bo'lib qo'shiladi — video ochilmaganda ko'rinadigan rasm.
    /// Worker uni `openfilm look cover.html` bilan rasmga aylantiradi; sahifa bo'lmasa videodan kadr olinadi (subtitrsiz).
    /// </summary>
    private static void AppendCover(StringBuilder sb, VideoScript script, int w, int h)
    {
        var title = string.IsNullOrWhiteSpace(script.YouTubeTitle) ? "" : script.YouTubeTitle.Trim();
        sb.AppendLine("## Cover (required)");
        sb.AppendLine();
        sb.AppendLine("Make `film/cover.html`: one still picture that is shown before the video is opened (YouTube thumbnail and the cover inside the MP4).");
        sb.AppendLine($"- Same stage: `<meta name=\"viewport\" content=\"width={w}, height={h}\">`, fills the stage edge to edge, uses the same shared style (`film/style.css`, colours, fonts) as the scenes.");
        sb.AppendLine($"- A short, punchy headline of 2–5 words taken from the video title{(title.Length > 0 ? $" \"{title}\"" : "")} — huge (at least {(int)Math.Round(Math.Min(w, h) * 0.11)} px), bold, max 2 lines, near-white with a dark outline or shadow, one key word in the accent colour.");
        sb.AppendLine("- One big focal visual that makes the topic obvious at a glance (object, diagram, contrast/comparison, big number). Strong contrast, readable at phone thumbnail size.");
        sb.AppendLine($"- Keep the bottom-right corner (about {(int)Math.Round(w * 0.16)}x{(int)Math.Round(h * 0.12)} px) empty: YouTube draws the duration there. No subtitles, no small text, no logos, no URLs.");
        sb.AppendLine("- The picture is complete at t=0 (no animation needed; it must not depend on time). Check it with `openfilm look cover.html 0` and fix anything cut off or unreadable.");
        sb.AppendLine();
    }

    /// <summary>
    /// Sahna sahifalari (iframe) uchun dizayn sifati talablari. Har doim qo'shiladi (foydalanuvchi o'z montaj promptini yozgan bo'lsa ham):
    /// oldingi videolarda kontent kichik, yuqoriga tiqilgan, kadrning yarmi bo'sh, sahna boshida qora/xira kadr bo'lardi.
    /// </summary>
    private static void AppendDesignBar(StringBuilder sb, int w, int h, bool hasTemplate, bool hasMotionGuide = false)
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
        sb.AppendLine(hasMotionGuide
            ? "Motion (the picture depends only on t; the timings and styles in `reference/MOTION_GUIDE.md` replace the defaults below)"
            : "Motion (the picture depends only on t)");
        sb.AppendLine("- Frame 0 of every page is already a good-looking, readable frame: the headline and the focal visual are visible at t=0 (at most a quick 0–0.4 s ease-in on top of an already visible layout). Never start a scene on a black, empty or faded frame.");
        sb.AppendLine("- Build the explanation with staged reveals timed to the subtitle words (items appear, lines draw, numbers count up), each 0.3–0.8 s with ease-out; keep a gentle continuous motion (slow drift/scale 1.00→1.04) so no scene is a frozen slide.");
        sb.AppendLine("- Everything stays on screen until the scene ends; no element disappears early, no fade-out to black before the cut.");
        sb.AppendLine();
        sb.AppendLine("Self-review with `openfilm look`: for each scene check frame 0, the middle and the last frame. If any frame is mostly empty, has tiny text, text near the edges or in the subtitle zone, or looks unfinished, redesign that page before finishing.");
        sb.AppendLine();
    }
}
