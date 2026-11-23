using System.Globalization;
using System.Text;
using YtAuto.Core.Demo;
using YtAuto.Core.Domain;
using YtAuto.Core.Script;
using YtAuto.Core.Visuals;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Claude uchun instructions.md: saqlangan Claude (montaj) prompti + subtitr + ovoz + shablon (bo'lsa) + worker tekshiradigan
/// texnik talablar. Sahnalarni Claude subtitr ma'nosiga qarab o'zi belgilaydi.
/// </summary>
/// <summary>Demo video uchun montaj ma'lumoti: so'rov (loyiha konteksti) va ovozga bog'langan sahna rejasi.</summary>
internal sealed record DemoBrief(DemoSpec Spec, IReadOnlyList<DemoPlanScene> Scenes);

internal static class EditorInstructions
{
    /// <summary>Kanal tahlilidan (bot) montaj uslubi: shablon/brief bilan birga Claude'ga beriladi.</summary>
    public const string MontageHeading = "## Channel style (learned from this channel's latest videos; keep new videos consistent with it)";
    /// <summary>
    /// Video uchun shablon tanlangan bo'lsa kanalning oldingi videolaridan olingan uslub ikkinchi darajali: foydalanuvchi yuklagan shablon ustun.
    /// </summary>
    public const string MontageHeadingBelowTemplate = "## Channel habits (from this channel's latest videos) - LOWER priority than the template";
    public const string TemplatePriorityRule =
        "The template chosen for this video outranks this section. Use this section only for what the template does not decide " +
        "(how the topic is framed, how much information a scene carries, how explanations are paced). Wherever the two differ - colours, fonts, " +
        "layout, recurring brand elements, image treatment, motion, transitions - follow the template and ignore this section.";

    public static string Build(JobContext c, VideoScript script, IReadOnlyList<SubtitleCue> cues, double audioSec,
        TemplateDefinition? tpl, IReadOnlyList<string> refFrames, bool hasStyleGuide = false, MediaManifest? media = null, bool hasMotionGuide = false,
        StockCandidates? stock = null, MusicPick? music = null, DemoBrief? demo = null)
    {
        var (w, h) = c.StageSize;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Task: build the OpenFilm project for one video");
        sb.AppendLine();
        sb.AppendLine("You are running unattended in a fresh session. Never ask questions. Work only inside this folder.");
        sb.AppendLine("Do not render the final video and do not delete anything outside `film/`.");
        sb.AppendLine();
        if (demo is not null)
        {
            // Demo: kanal brifi va kanal uslubi (fonlar, bezaklar) emas - faqat loyiha materiallari ustida montaj.
            sb.AppendLine("## Brief");
            sb.AppendLine();
            sb.AppendLine(DemoBriefText);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Brief (Claude prompt from the channel settings)");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(c.S.Claude.MontagePrompt) ? DefaultPrompts.Montage : c.S.Claude.MontagePrompt.Trim());
            sb.AppendLine();
        }
        if (demo is null && c.S.ChannelContext?.MontageBlock is { Length: > 0 } channel)
        {
            // Shablon bor bo'lsa - kanal odatlari shablondan past: foydalanuvchi yuklagan shablon har doim ustun.
            sb.AppendLine(tpl is null ? MontageHeading : MontageHeadingBelowTemplate);
            sb.AppendLine();
            if (tpl is not null) { sb.AppendLine(TemplatePriorityRule); sb.AppendLine(); }
            sb.AppendLine(channel);
            sb.AppendLine();
        }
        if (!c.Job.IsDemo)
        {
            // Format uzunligi: 16:9 - 2-3 daqiqa, Shorts/kvadrat - 55 soniyagacha (ovoz shunga sig'dirilgan; sifat tekshiruvi ham shuni talab qiladi).
            var len = VideoLength.For(c.Job.Format);
            sb.AppendLine("## Length");
            sb.AppendLine();
            var target = len == VideoLength.Landscape ? "2-3 minutes" : $"up to {len.MaxSec} seconds";
            var voice = audioSec.ToString("0.0", inv);
            sb.AppendLine($"- This is a {VideoLength.FormatName(c.Job.Format)}: target length {target}. " +
                $"The finished film must end **no later than {len.MaxSec.ToString(inv)} s**. The voice is {voice} s: keep any closing beat after it short (2-3 s at most) and never pad the ending.");
            sb.AppendLine();
        }
        if (c.Job.HasHook)
        {
            var hook = HookRule.Normalize(c.Job.Hook);
            var end = HookRule.EndSec(cues, hook);
            sb.AppendLine("## Opening hook (written by the channel owner)");
            sb.AppendLine();
            var span = end is double e ? $" (spoken from 0 s to about {e.ToString("0.0", inv)} s)" : "";
            sb.AppendLine($"The voice opens with this question{span}: **\"{hook}\"**");
            sb.AppendLine();
            sb.AppendLine("- The film starts on this question at t=0: the first scene poses it, with the question (or its key words) as big, bold on-screen text and a strong visual that makes the viewer need the answer.");
            sb.AppendLine("- Nothing before it: no logo, intro card, title sequence, black frame or slow fade-in. Motion from the very first frame; the question must be readable in under a second.");
            sb.AppendLine("- Build the rest of the montage as the answer to this question and make the payoff moment visually clear near the end.");
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
        if (music is not null)
        {
            var len = music.DurationSec > 0 ? $"{music.DurationSec.ToString("0.0", inv)} seconds" : "length unknown — check it";
            sb.AppendLine($"- `film/{music.File}`: background music \"{music.Track.Title}\" ({len}). Instrumental bed under the voice; do not edit or replace it.");
        }
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
        if (tpl is null && demo is null) sb.AppendLine("- Template: none for this video. Use the default design from the brief.");
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
        if (stock is { Total: > 0 }) AppendStock(sb, stock, w, h, audioSec, inv);
        if (media is { Assets.Count: > 0 }) AppendMedia(sb, media, inv);
        if (demo is not null) AppendDemo(sb, demo, inv, w, h);
        if (demo is null) AppendDesignBar(sb, w, h, tpl is not null || refFrames.Count > 0, hasMotionGuide);
        sb.AppendLine("## Hard requirements (the worker checks them automatically)");
        sb.AppendLine();
        sb.AppendLine($"1. `film/film.html` with `<meta name=\"viewport\" content=\"width={w}, height={h}\">`.");
        sb.AppendLine($"2. Visual clips cover 0.00 → {audioSec.ToString("0.00", inv)} s with no gap (every page clip has `#t=0,<length>` and an `at`). A moment with no clip is black, which is an error.");
        if (music is null)
            sb.AppendLine("3. `assets/voice.wav` on its own audio track, `at=\"0\"`, volume 1, speed 1. Optional music on another track at volume ≤ 0.2.");
        else
        {
            var vol = music.Volume.ToString("0.00", inv);
            sb.AppendLine("3. `assets/voice.wav` on its own audio track, `at=\"0\"`, volume 1, speed 1.");
            sb.AppendLine($"   Background music: `{music.File}` on a SECOND audio track (never on the voice track), starting at `at=\"0\"`, volume exactly {vol}, speed 1, covering 0 → {audioSec.ToString("0.00", inv)} s.");
            sb.AppendLine(music.DurationSec > 0 && music.DurationSec < audioSec
                ? $"   It is shorter than the video: place it again end to end (same file, same volume) until the video end, and trim the last clip so it ends exactly at {audioSec.ToString("0.00", inv)} s."
                : $"   Trim it so it ends exactly at {audioSec.ToString("0.00", inv)} s (the video end). If OpenFilm supports audio fades, fade it out over the last 1.5 s.");
            sb.AppendLine(demo is null
                ? "   No other music or sound files. Do not write the music credit on screen; the worker adds it to the video description."
                : "   No other music. Sound effects only from `assets/sfx/` (see the demo section). Do not write the music credit on screen; the worker adds it to the video description.");
        }
        if (demo is not null)
            sb.AppendLine("   Sound effects: `assets/sfx/*.wav` on a THIRD audio track, each clip at its moment, volume 0.15–0.3, never on the voice or music track.");
        sb.AppendLine("4. No subtitles drawn in pages (they are burned in from voice.vtt). Keep the lower 22% of the frame free of key text.");
        sb.AppendLine("5. Every page follows \"the picture depends only on t\". No external network resources (no CDN fonts or images); everything local" +
            (media is { Assets.Count: > 0 } || stock is { Total: > 0 } ? " (the real images and clips are already in `film/assets/media/` and `film/assets/stock/`; do not download others)."
             : demo is not null ? " (the product screenshots and recordings are already in `film/assets/demo/`; do not download or invent other product images)." : "."));
        sb.AppendLine("6. All text inside the safe area and readable on a phone. Nothing cut off at the edges.");
        sb.AppendLine("7. Leave `film/assets/voice.wav` and `film/assets/voice.vtt` untouched.");
        sb.AppendLine("8. `film/cover.html`: the video cover (see below). It is NOT on the timeline.");
        sb.AppendLine();
        AppendCover(sb, script, w, h, demo is not null);
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
        sb.AppendLine("- Priority for every design decision: the template (its project files, frames, STYLE_GUIDE.md, MOTION_GUIDE.md) first, then the channel habits section, then the generic rules in this file. Never let the channel's older videos override the template.");
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

    /// <summary>
    /// OpenFilm topgan stok nomzodlar: Claude avval har birini kontekst va sifat bo'yicha tekshiradi, tanlovini film/media_selection.json ga
    /// yozadi, so'ng faqat tanlanganlari bilan animatsiyali dizayn qiladi.
    /// </summary>
    /// <summary>Bitta sahnada ko'pi bilan shuncha stok rasm va video ishlatiladi (mos va sifatlilari - sahna uzunligiga qarab).</summary>
    internal const int MaxStockPhotosPerScene = 5, MaxStockClipsPerScene = 3;
    /// <summary>Har kadr ekranda taxminan shuncha turadi: sahna uzunligi / shu = nechta rasm/video sig'adi.</summary>
    internal const double SecondsPerStockShot = 2.5;

    /// <summary>
    /// Sahnaga taxminan nechta stok kadr sig'adi: hikoyaning shu qismi uzunligi (keyingi stok sahnagacha yoki ovoz oxirigacha) /
    /// <see cref="SecondsPerStockShot"/>, 1 dan 8 gacha (5 rasm + 3 video).
    /// </summary>
    internal static IReadOnlyDictionary<string, (double Span, int Shots)> StockRoom(StockCandidates stock, double audioSec)
    {
        var ordered = stock.Scenes.Where(s => s.Candidates.Count > 0).OrderBy(s => s.At).ToList();
        var d = new Dictionary<string, (double, int)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var end = i + 1 < ordered.Count ? ordered[i + 1].At : audioSec;
            var span = Math.Max(1, end - ordered[i].At);
            d[ordered[i].Id] = (span, Math.Clamp((int)Math.Round(span / SecondsPerStockShot), 1, MaxStockPhotosPerScene + MaxStockClipsPerScene));
        }
        return d;
    }

    private static void AppendStock(StringBuilder sb, StockCandidates stock, int w, int h, double audioSec, CultureInfo inv)
    {
        var room = StockRoom(stock, audioSec);
        var minLong = (int)Math.Round(Math.Max(w, h) * 2 / 3.0);
        var maxImg = stock.Scenes.Max(s => s.Images);
        var maxClip = stock.Scenes.Max(s => s.Clips);
        sb.AppendLine("## Stock footage and photos (found by OpenFilm on Pexels/Pixabay; YOU decide which are good)");
        sb.AppendLine();
        sb.AppendLine($"OpenFilm searched the web for the scenes below and downloaded several candidates for each: up to {maxImg} photos (I1, I2, ...) and up to {maxClip} short video clips (V1, V2, ...).");
        sb.AppendLine("They are NOT all good. You are the art director: review every candidate, choose the best ones, and only then design with them.");
        sb.AppendLine();
        foreach (var s in stock.Scenes.Where(s => s.Candidates.Count > 0).OrderBy(s => s.At))
        {
            sb.AppendLine($"### {s.Id} ~{s.At.ToString("0.0", inv)} s: {(string.IsNullOrWhiteSpace(s.Purpose) ? s.Query : s.Purpose)} (search: \"{s.Query}\")");
            var (span, shots) = room[s.Id];
            sb.AppendLine($"- This part of the narration lasts about {span.ToString("0.0", inv)} s: room for about {shots} shot{(shots == 1 ? "" : "s")} (one every ~{SecondsPerStockShot.ToString("0.0", inv)} s).");
            if (s.Sheet.Length > 0) sb.AppendLine($"- Contact sheet: `{s.Sheet}`");
            foreach (var x in s.Candidates)
            {
                var len = x.Type == StockTypes.Video ? $", {x.Duration.ToString("0.0", inv)} s" : "";
                var mb = x.Bytes > 0 ? $", {(x.Bytes / 1048576.0).ToString("0.0", inv)} MB" : "";
                sb.AppendLine($"- {x.Label} `film/{x.File}` ({x.Type}, {x.Width}x{x.Height}{len}{mb}, {x.Provider})");
            }
            sb.AppendLine();
        }
        sb.AppendLine("Step 1 - review (before writing any page):");
        sb.AppendLine("- Open each scene's contact sheet with the Read tool. Row 1 = the photos; each further row = one clip shown as three frames (start, middle, end) with its length and size.");
        sb.AppendLine("  When a tile is too small to judge, open the photo file itself, or run `openfilm look assets/stock/<scene>/<clip>.mp4` inside `film/` to see a clip closer.");
        sb.AppendLine("- Accept a candidate only if ALL of these hold:");
        sb.AppendLine("  1. Context: it clearly shows what the narration says at that moment (the scene line above) - same subject, place and era, or an obvious visual metaphor. Reject loosely related generic stock clichés (handshakes, people smiling at laptops, random office scenes) unless the narration is about exactly that.");
        sb.AppendLine($"  2. Quality: sharp and well exposed, no blur, motion smear, heavy noise, banding or compression blocks. At least {minLong} px on the long side to fill the frame (smaller ones only inside a card). Clips: steady, no jump cuts or shaky handheld in the part you use, not a slideshow of stills.");
        sb.AppendLine("  3. Clean: no watermark or stock-site stamp, no burned-in text, captions or subtitles, no visible brand logos or app UI unless that brand is the topic, no recognisable celebrity, nothing violent or sensitive.");
        sb.AppendLine($"  4. Fit: its colours and mood can sit inside the template style; its orientation works on the {w}x{h} stage (cropping with `object-fit: cover` is fine if the subject stays in frame).");
        sb.AppendLine($"- How many to use: there is NO fixed number - use every candidate that passes, as many as the scene's length allows (the \"room for\" line above), up to {MaxStockPhotosPerScene} photos and up to {MaxStockClipsPerScene} clips per scene.");
        sb.AppendLine("  A long scene should show several pictures in turn (the picture changes every 2-3 s, cut on the narration's phrases) instead of one still for the whole scene; a short scene needs only one or two. Never pad with a candidate that failed a check just to fill the room: then hold the good ones longer.");
        sb.AppendLine("  Each photo stays on screen at least 2 s, each clip at least 2.5 s. Prefer good clips over photos (real motion keeps the video alive). If only one passes, use one; if nothing passes, choose nothing there and design that scene with drawn graphics.");
        sb.AppendLine();
        sb.AppendLine($"Step 2 - write `film/{JobRunner.StockSelectionFile}` (the worker reads it) before building the pages. List EVERY candidate once, chosen or rejected, with a short concrete reason:");
        sb.AppendLine("```json");
        sb.AppendLine("{\"scenes\":[{\"scene\":\"s01\",\"chosen\":[{\"file\":\"assets/stock/s01/clip2.mp4\",\"reason\":\"steady close-up of the exact device, sharp\"},");
        sb.AppendLine("  {\"file\":\"assets/stock/s01/img3.jpg\",\"reason\":\"same device in use, clean and sharp\"}],");
        sb.AppendLine("  \"rejected\":[{\"file\":\"assets/stock/s01/img1.jpg\",\"reason\":\"watermark bottom-right\"}]}]}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Step 3 - design with the chosen media only (never a rejected one, never other images from the internet):");
        sb.AppendLine("- Clips go straight on the film.html timeline as `<video src=\"assets/stock/s01/clip2.mp4#t=IN,OUT\" at=\"...\" muted style=\"object-fit: cover\">` on a footage track BELOW the page track; the scene page on the track above paints the headline, labels and overlays (what a page does not paint is transparent). Always `muted`. Choose the best stretch with `#t=IN,OUT`; if the clip is shorter than the scene use `speed` 0.5-0.8 for slow motion, or cut to a second chosen candidate. Never play a clip past its length.");
        sb.AppendLine("- Photos: as an `<img>` clip on the footage track, or inside a scene page (`assets/stock/...` from a page in `film/`). Never stretch them.");
        sb.AppendLine("- Several chosen media in one scene: cut them one after another on the footage track (each starting where the previous ends, timed to the words), or show two side by side / as a framed sequence in the page when they compare things. Keep the scene's headline and overlay steady across the cuts.");
        sb.AppendLine("- Make it look designed and animated, not pasted: a gradient or colour overlay in the template palette so text stays readable over footage; a slow Ken Burns push or pan on photos (scale 1.00 to 1.08 over the scene); framed or masked reveals of the picture timed to the words; animated headline and labels over the footage; the template's transition (or a short cross-dissolve) between scenes. Every frame depends only on t.");
        sb.AppendLine("- Do not write credits or watermarks on screen.");
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
    /// Demo video: loyihaning o'z screenshot va action videolari, sahna vaqtlari ovozdan (bo'lish Claude'ga qoldirilmaydi),
    /// zoom/pan/harakat, o'tishlar va SFX qoidalari.
    /// </summary>
    private static void AppendDemo(StringBuilder sb, DemoBrief demo, CultureInfo inv, int w, int h)
    {
        var p = demo.Spec.Project;
        sb.AppendLine("## Product demo (this video shows a real software project; this section overrides \"group these lines into scenes\" above)");
        sb.AppendLine();
        sb.AppendLine($"Project: **{p.Name}** — {OneLine(p.Summary, 400)}");
        if (!string.IsNullOrWhiteSpace(demo.Spec.Requirements.Audience)) sb.AppendLine($"Audience: {OneLine(demo.Spec.Requirements.Audience, 200)}");
        if (!string.IsNullOrWhiteSpace(demo.Spec.Requirements.Tone)) sb.AppendLine($"Tone: {OneLine(demo.Spec.Requirements.Tone, 120)}");
        if (!string.IsNullOrWhiteSpace(demo.Spec.Requirements.Notes)) sb.AppendLine($"User requirements: {OneLine(demo.Spec.Requirements.Notes, 600)}");
        sb.AppendLine();
        sb.AppendLine("The scenes below are FIXED: one page per scene, in this order, exactly at these times (they come from the real voice timings). Each scene shows its own product material.");
        sb.AppendLine();
        foreach (var s in demo.Scenes)
        {
            var kind = s.Kind == DemoMediaKinds.Video ? $"screen recording, {s.MediaDurationSec.ToString("0.0", inv)} s" : "screenshot";
            sb.AppendLine($"{s.Index}. **{s.Start.ToString("0.00", inv)}–{s.End.ToString("0.00", inv)} s** · `film/{s.File}` ({kind}, {s.Width}x{s.Height}) — {OneLine(s.MediaTitle, 120)}");
            if (!string.IsNullOrWhiteSpace(s.MediaDescription)) sb.AppendLine($"   - Shows: {OneLine(s.MediaDescription, 300)}");
            if (!string.IsNullOrWhiteSpace(s.Focus)) sb.AppendLine($"   - Focus (zoom/pan target): {OneLine(s.Focus, 200)}");
            if (!string.IsNullOrWhiteSpace(s.OnScreenText)) sb.AppendLine($"   - On-screen label: \"{OneLine(s.OnScreenText, 60)}\"");
            sb.AppendLine($"   - Voice says: \"{OneLine(s.Narration, 400)}\"");
        }
        sb.AppendLine();
        sb.AppendLine("### Only the product on screen (no backgrounds)");
        sb.AppendLine();
        sb.AppendLine("Every frame of this video is the project itself: its screenshots and screen recordings, moved, cut and highlighted. Nothing else is drawn.");
        sb.AppendLine($"- Each material fills the whole {w}x{h} stage edge to edge (full-bleed). Use `object-fit: cover` when the crop keeps the UI the voice talks about; otherwise `contain`.");
        sb.AppendLine("- When `contain` leaves empty bars (e.g. a 16:9 screenshot in a 9:16 video), fill them ONLY with the same material: a blurred, darkened, enlarged copy of it behind (`filter: blur(40px) brightness(0.45)`, `object-fit: cover`). Never a colour, gradient or pattern.");
        sb.AppendLine("- Forbidden: decorative backgrounds (gradients, glows, blurred blobs, grids, particles, bokeh), device or laptop mockups, desks and rooms, a window frame floating on a backdrop, stock photos or clips, icons and illustrations, drawn fake UI, emoji. Do not invent features.");
        sb.AppendLine("- Allowed on top of the product: the highlight effects below, the short on-screen label, the title and the end card text. They sit directly on the product image (the title and end card over the dimmed product screen, not on an empty background).");
        sb.AppendLine("- Do not use `style.css` tokens for background colours; the only colours you add are one accent (for outlines/labels) and near-white text with a dark shadow.");
        sb.AppendLine();
        AppendDemoEffects(sb, demo);
        sb.AppendLine("### Montage");
        sb.AppendLine();
        sb.AppendLine("It must feel like a polished, energetic product intro — a real edit, not a slideshow:");
        sb.AppendLine("- A new shot every 2–4 s: within one scene cut between the whole screen, close-ups of the Focus area and other parts of the same material (cut on the narration's phrases). Never hold one static framing for a whole long scene.");
        sb.AppendLine("- Screen recordings: a `<video>` clip with `muted` (never its own sound), on the timeline at the scene start. If the recording is shorter than the scene, freeze its last frame (and keep the camera moving) — never loop visibly; if longer, trim it or speed-ramp the dull part.");
        sb.AppendLine("- Every move uses ease-in-out curves; no jerky jumps. Frame 0 of each scene is already a sharp, readable product frame — never black or empty.");
        sb.AppendLine("- Transitions finish exactly at the scene boundary so the clips cover the timeline end to end. Do not use the same transition twice in a row.");
        sb.AppendLine("- First scene: the product name as a short title over the first product screen (1–2 s). Last scene: end card with the project name" + (string.IsNullOrWhiteSpace(p.Url) ? "" : $" and `{OneLine(p.Url!, 80)}` as plain text") + " over the last product screen, dimmed.");
        sb.AppendLine("- Sound effects, synced to what happens on screen (a whoosh on transitions and fast moves, a click on cursor clicks, a pop when a label or outline appears), at most one every ~2 s: " + string.Join("; ", Media.Sfx.Catalog.Select(kv => $"`assets/sfx/{kv.Key}` = {kv.Value}")) + ". Place each as its own audio clip at the exact moment (volume 0.15–0.3).");
        sb.AppendLine("- Text you add (labels, titles) is short, matches the narration and stays out of the lower 22% (subtitles).");
        sb.AppendLine();
        sb.AppendLine("Self-review with `openfilm look`: check frame 0, the middle and the end of every scene. Any frame that shows something other than the product (an empty background, a decorative layer, a gap) or looks static, cut off or unreadable must be fixed before finishing.");
        sb.AppendLine();
    }

    /// <summary>Demo uchun effektlar: foydalanuvchi tanlagani (yoki butun katalog), guruhlab, turli-tumanlik talabi bilan.</summary>
    private static void AppendDemoEffects(StringBuilder sb, DemoBrief demo)
    {
        var effects = DemoEffects.Resolve(demo.Spec.Requirements.Effects);
        var chosenByUser = demo.Spec.Requirements.Effects is { Count: > 0 } && effects.Count < DemoEffects.Catalog.Count;
        var min = DemoEffects.MinDistinct(demo.Scenes.Count, effects.Count);
        sb.AppendLine(chosenByUser ? "### Effects (chosen by the user — use these)" : "### Effects (pick the right one for each moment)");
        sb.AppendLine();
        sb.AppendLine($"Use at least {min} different effects across the video, mixed from several groups — not only zoom. Every scene has at least one camera move and at least one highlight, text or rhythm effect; vary them from scene to scene so the edit never repeats itself.");
        foreach (var g in new[] { (DemoEffects.Camera, "Camera (on the product image)"), (DemoEffects.Transition, "Transitions"), (DemoEffects.Highlight, "Highlights"), (DemoEffects.Text, "Text"), (DemoEffects.Rhythm, "Rhythm") })
        {
            var list = effects.Where(e => e.Group == g.Item1).ToList();
            if (list.Count == 0) continue;
            sb.AppendLine($"- {g.Item2}:");
            foreach (var e in list) sb.AppendLine($"  - `{e.Key}`: {e.Recipe}.");
        }
        sb.AppendLine("Write in `film/BUILD_NOTES.md` which effects each scene uses.");
        sb.AppendLine();
    }

    /// <summary>Demo video uchun montaj brifi (kanal sozlamasidagi Claude prompti o'rniga).</summary>
    internal const string DemoBriefText =
        "Edit a short, high-energy product intro video for a software project. The product's own screenshots and screen recordings are the " +
        "only visuals: bring them to life with camera moves, cuts, transitions, highlights, short kinetic labels and sound effects, timed to the voice. " +
        "No decorative or invented backgrounds, mockups or stock media.";

    private static string OneLine(string? s, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }

    /// <summary>
    /// Video muqovasi (cover): YouTube thumbnail bo'ladi va MP4 ichiga rasm bo'lib qo'shiladi — video ochilmaganda ko'rinadigan rasm.
    /// Worker uni `openfilm look cover.html` bilan rasmga aylantiradi; sahifa bo'lmasa videodan kadr olinadi (subtitrsiz).
    /// </summary>
    private static void AppendCover(StringBuilder sb, VideoScript script, int w, int h, bool demo = false)
    {
        var title = string.IsNullOrWhiteSpace(script.YouTubeTitle) ? "" : script.YouTubeTitle.Trim();
        sb.AppendLine("## Cover (required)");
        sb.AppendLine();
        sb.AppendLine("Make `film/cover.html`: one still picture that is shown before the video is opened (YouTube thumbnail and the cover inside the MP4).");
        sb.AppendLine($"- Same stage: `<meta name=\"viewport\" content=\"width={w}, height={h}\">`, fills the stage edge to edge, uses the same shared style (`film/style.css`, colours, fonts) as the scenes.");
        sb.AppendLine($"- A short, punchy headline of 2–5 words taken from the video title{(title.Length > 0 ? $" \"{title}\"" : "")} — huge (at least {(int)Math.Round(Math.Min(w, h) * 0.11)} px), bold, max 2 lines, near-white with a dark outline or shadow, one key word in the accent colour.");
        sb.AppendLine(demo
            ? "- The picture is the product itself: the most telling screenshot from `assets/demo/` fills the whole stage (cropped/zoomed onto its best part, slightly darkened under the headline). No other background, shapes or stock images. Strong contrast, readable at phone thumbnail size."
            : "- One big focal visual that makes the topic obvious at a glance (object, diagram, contrast/comparison, big number). Strong contrast, readable at phone thumbnail size.");
        sb.AppendLine($"- Keep the bottom-right corner (about {(int)Math.Round(w * 0.16)}x{(int)Math.Round(h * 0.12)} px) empty: YouTube draws the duration there. No subtitles, no small text, no logos, no URLs.");
        sb.AppendLine("- The picture is complete at t=0 (no animation needed; it must not depend on time), but the page still needs `<script>window.film={duration:1,frame(){}}</script>` — `openfilm look` refuses a page without it. Check it with `openfilm look cover.html 0` and fix anything cut off or unreadable.");
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
