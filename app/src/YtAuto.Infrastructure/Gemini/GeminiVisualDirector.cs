using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Visuals;

namespace YtAuto.Infrastructure.Gemini;

/// <summary>
/// Gemini vision (generateContent, inline rasm qismlari): shablon dizayn qo'llanmasi, real rasm rejasi va tayyor videoni
/// shablon bilan solishtirish. Kalitlar va 429 almashinuvi <see cref="GeminiScriptGenerator"/> bilan umumiy.
/// </summary>
public sealed class GeminiVisualDirector : IVisualDirector
{
    private readonly GeminiScriptGenerator _gen;
    private readonly Func<IReadOnlyList<string>> _models;
    /// <summary>Bitta model (eski chaqiruvlar va testlar uchun).</summary>
    public GeminiVisualDirector(GeminiScriptGenerator gen, Func<string> model) : this(gen, () => new[] { model() }) { }
    /// <summary>Model zanjiri: router limit/mavjudlik/vazifa turiga qarab tanlaydi.</summary>
    public GeminiVisualDirector(GeminiScriptGenerator gen, Func<IReadOnlyList<string>> models) { _gen = gen; _models = models; }

    private IReadOnlyList<string> Models { get { var m = _models(); return GeminiScriptGenerator.Chain(m?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(), null); } }

    public const string StyleGuidePrompt = """
You are the art director of a YouTube channel. The images are frames from the channel's template video "{name}".
Owner notes about the template: {notes}

Write STYLE_GUIDE.md: a precise design system a motion designer will follow to build NEW videos (HTML/CSS/SVG pages, 1920x1080
or 1080x1920) that look like they clearly belong to this channel. Describe the STYLE only, never the topic or content of these frames.
Be concrete and measurable: hex colours, sizes as % of frame height, positions as % of frame, stroke widths, radii.
Third-party logos, products or sponsors that merely appear in these frames are content, not the channel's branding: do not
list them as brand elements or palette sources; describe only how such logos/images are framed and treated.

Sections (use these headings):
1. Palette: 5-8 hex codes, each with its role (background, surface/card, primary accent, secondary accent, text, muted text, highlight).
2. Typography: character of the type (geometric sans, condensed, rounded, serif...), the closest Windows system font from
   Segoe UI, Segoe UI Black, Bahnschrift, Arial, Arial Black, Impact, Trebuchet MS, Georgia, Verdana, Consolas; weights, case,
   headline and label sizes (% of frame height), letter spacing, how key words are highlighted (colour, underline, box, marker).
3. Layout and composition: grid, alignment, safe margins, where headlines sit, typical split between text and visual,
   recurring containers (cards, panels, frames) with radius, border and shadow.
4. Backgrounds and texture: gradients, patterns, noise, vignette, depth layers.
5. Imagery: how photos, illustrations, icons and logos are used: framing and masks, borders, shadows, colour grading
   (natural, duotone, tinted), icon style (outline/filled/flat/3D, stroke width), how many images per scene.
6. Recurring brand elements: watermark/logo position, lower thirds, labels, progress bars, counters, stickers.
7. Motion and transitions (infer from consecutive frames): entrance style, pacing, camera moves, transition type.
8. Do / Don't: 6-10 short rules that keep new videos on-brand.

Output only the markdown document.
""";

    public async Task<string> TemplateStyleGuideAsync(IReadOnlyList<string> framePaths, string templateName, string notes, IJobLog log, CancellationToken ct)
    {
        var prompt = StyleGuidePrompt.Replace("{name}", templateName).Replace("{notes}", string.IsNullOrWhiteSpace(notes) ? "(none)" : notes.Trim());
        var parts = new JsonArray();
        foreach (var f in framePaths) parts.Add(ImagePart(f));
        parts.Add(new JsonObject { ["text"] = prompt });
        var json = await CallAsync(parts, new JsonObject { ["temperature"] = 0.3, ["maxOutputTokens"] = 8192 }, log, ct).ConfigureAwait(false);
        var text = GeminiScriptGenerator.TextOf(json).Trim();
        if (text.StartsWith("```")) text = text.Trim('`').Replace("markdown\n", "", StringComparison.OrdinalIgnoreCase).Trim();
        return text;
    }

    public const string MotionGuidePrompt = """
You are the motion designer of a YouTube channel. The video clip(s) above are from the channel's template video "{name}"
({clips}). Owner notes about the template: {notes}

Watch the MOTION carefully, frame by frame, and write MOTION_GUIDE.md: a precise animation system another motion designer will
follow to animate NEW videos built from HTML/CSS/SVG pages (each scene is one page whose picture depends only on the time t)
so they move exactly like this channel. Describe motion only, never the topic or content. Be concrete and measurable:
durations in seconds, delays and staggers in seconds, distances as % of frame, scale factors, opacity values, easing as
CSS cubic-bezier(...) or a named curve. If something does not happen in the clips, say "not used" instead of inventing it.

Sections (use these headings):
1. Pacing: average scene/shot length (s), cuts per minute, how long a scene holds after its last element appears, rhythm (steady, accelerating, beat-driven).
2. Scene transitions: type (hard cut, cross-fade, slide/push with direction, wipe, zoom-through, mask/shape reveal, whip pan, glitch...), duration, easing, what moves.
3. Text animation: how headlines and labels enter and leave (fade, slide up from N %, scale-in, typewriter, word-by-word, per-letter, mask reveal, highlight sweep, underline draw), duration, stagger, easing; how key words are emphasised over time.
4. Shapes, cards, icons and charts: entrance (pop with overshoot, draw-on stroke, grow from origin, slide), duration, stagger between items, how lists/steps/numbers build (count-up speed), connectors and arrows.
5. Images and photos: entrance, Ken Burns / parallax (scale from→to per second, pan direction), masks, frames.
6. Background and ambient motion: moving gradients, particles, grids, light sweeps, loops (period in s), camera drift/zoom on the whole scene.
7. Exits: do elements leave before the cut or does the cut take them; exit style and duration.
8. Recurring motion brand elements: logo stings, progress bars, counters, lower thirds, stickers - when they appear and how they move.
9. Recipe: a compact CSS block a designer can paste - custom properties for the standard durations, staggers and easings
   (e.g. --dur-in, --dur-out, --stagger, --ease-in, --ease-out, --ease-pop) and @keyframes for the 4-8 most used moves,
   each with a one-line comment saying when to use it.
10. Do / Don't: 6-10 short rules that keep new videos moving on-brand.

Output only the markdown document.
""";

    /// <summary>
    /// Shablon videosining harakat tizimi. Klip(lar) video sifatida (inline, ovozsiz, past o'lchamda) yuboriladi: Gemini so'ralgan fps bilan
    /// kadr oladi va MEDIA_RESOLUTION_LOW tufayli kadr boshiga ~66 token sarflanadi. Model video_metadata/mediaResolution'ni rad etsa,
    /// ularsiz bir marta qayta so'raladi (standart 1 kadr/s).
    /// </summary>
    public async Task<string> TemplateMotionGuideAsync(IReadOnlyList<MotionClip> clips, double fps, string templateName, string notes, IJobLog log, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var desc = string.Join("; ", clips.Select((c, i) => $"clip {i + 1}: {c.StartSec.ToString("0.#", inv)}–{(c.StartSec + c.DurationSec).ToString("0.#", inv)} s of the template"));
        var prompt = MotionGuidePrompt.Replace("{name}", templateName).Replace("{clips}", desc)
            .Replace("{notes}", string.IsNullOrWhiteSpace(notes) ? "(none)" : notes.Trim());
        JsonArray Parts(bool tuned)
        {
            var parts = new JsonArray();
            foreach (var c in clips)
            {
                var part = new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "video/mp4", ["data"] = Convert.ToBase64String(File.ReadAllBytes(c.Path)) } };
                if (tuned) part["video_metadata"] = new JsonObject { ["fps"] = fps };
                parts.Add(part);
            }
            parts.Add(new JsonObject { ["text"] = prompt });
            return parts;
        }
        JsonObject Config(bool tuned)
        {
            var g = new JsonObject { ["temperature"] = 0.3, ["maxOutputTokens"] = 12288 };
            if (tuned) g["mediaResolution"] = "MEDIA_RESOLUTION_LOW";
            return g;
        }
        JsonNode json;
        try { json = await CallAsync(Parts(true), Config(true), log, ct, GeminiTask.VideoLowRes).ConfigureAwait(false); }
        catch (PipelineException e) when (e.Code == "gemini_request")
        {
            log.Warn($"Gemini video sozlamalarini (fps, past o'lcham) qabul qilmadi, standart rejimda qayta so'raladi: {e.Message}", "Edit");
            json = await CallAsync(Parts(false), Config(false), log, ct, GeminiTask.Video).ConfigureAwait(false);
        }
        var text = GeminiScriptGenerator.TextOf(json).Trim();
        if (text.StartsWith("```")) text = text.Trim('`').Replace("markdown\n", "", StringComparison.OrdinalIgnoreCase).Trim();
        return text;
    }

    public const string MediaPlanPrompt = """
You plan real images for an explainer video. The narration is below with timings (seconds).
Choose the moments where a REAL picture explains the words better than drawn shapes, and say what to search for.

Kinds:
- "logo": the logo of a real brand, product, company, organisation or technology that is actually named in the narration
  (query = the exact brand name, e.g. "Visa", "Stripe", "Python"). Never invent brands.
- "photo": a real-world object, place, device, animal, process or everyday scene (query = 2-4 concrete English nouns,
  e.g. "credit card terminal", "server room", "honey bee flower"). No identifiable private people, no celebrities,
  no news events, nothing violent or sensitive. Prefer objects and places.
  The same query also searches stock VIDEO footage (Pexels/Pixabay), so name something that can be filmed and looks good in
  motion: a concrete subject, place or action ("barista pouring latte", "city traffic at night"), not an abstract idea.
- "icon": an abstract concept that a simple icon shows well (query = 1-2 English words, e.g. "lock", "refresh", "warning").

Rules:
- At most {max} items in total, at most one photo and two icons/logos per idea. Fewer is fine; skip when nothing fits.
- "at" = the second where that idea starts in the narration. "purpose" = one short line: what the picture shows the viewer.
- Queries in English even if the narration is not.
{channel}
NARRATION:
{cues}
""";

    public async Task<MediaPlan> PlanMediaAsync(string subtitleText, IReadOnlyList<SubtitleCue> cues, string channelContext, int maxItems, IJobLog log, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = cues.Count > 0
            ? string.Join("\n", cues.Select(q => $"{q.Start.ToString("0.0", inv)}: {q.Text.Replace("\n", " ")}"))
            : subtitleText;
        var prompt = MediaPlanPrompt.Replace("{max}", maxItems.ToString(inv)).Replace("{cues}", lines)
            .Replace("{channel}", string.IsNullOrWhiteSpace(channelContext) ? "" : "\nChannel style (keep pictures consistent with it):\n" + channelContext.Trim() + "\n");
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["kind"] = new JsonObject { ["type"] = "STRING", ["enum"] = new JsonArray("logo", "photo", "icon") },
                            ["query"] = new JsonObject { ["type"] = "STRING" },
                            ["purpose"] = new JsonObject { ["type"] = "STRING" },
                            ["at"] = new JsonObject { ["type"] = "NUMBER" },
                        },
                        ["required"] = new JsonArray("kind", "query", "purpose", "at"),
                    },
                },
            },
            ["required"] = new JsonArray("items"),
        };
        var json = await CallAsync(new JsonArray(new JsonObject { ["text"] = prompt }),
            new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 4096, ["responseMimeType"] = "application/json", ["responseSchema"] = schema }, log, ct, GeminiTask.Light).ConfigureAwait(false);
        try { return (JsonSerializer.Deserialize<MediaPlan>(GeminiScriptGenerator.TextOf(json)) ?? new()).Clean(maxItems); }
        catch (JsonException e) { throw PipelineException.Transient("gemini_bad_json", "Rasm rejasi JSON emas: " + e.Message); }
    }

    public const string ReviewPrompt = """
You are a strict art director. The first {tn} images are frames of the channel TEMPLATE. The next images are frames of a NEW video
(their timestamps are given before each one). The style guide of the channel is below.
Judge only VISUAL STYLE consistency and polish, not the topic: palette, typography, layout, backgrounds, image treatment,
recurring brand elements, readability, empty or unfinished-looking frames.
Score 0-10 (10 = indistinguishable from the template's style and professionally finished; 6 = recognisably related but with clear deviations).
List concrete problems a designer can fix in HTML/CSS, each starting with the timestamp, e.g. "12.0 s: headline uses blue #2563eb, template uses yellow #facc15".
At most 10 problems. If the video already matches, return an empty list.

STYLE GUIDE:
{guide}
""";

    public async Task<StyleReview> ReviewAgainstTemplateAsync(IReadOnlyList<string> templateFrames, IReadOnlyList<string> videoFrames, string styleGuide, IJobLog log, CancellationToken ct)
    {
        var parts = new JsonArray();
        foreach (var f in templateFrames) parts.Add(ImagePart(f));
        foreach (var f in videoFrames)
        {
            parts.Add(new JsonObject { ["text"] = "New video frame at " + FrameLabel(f) });
            parts.Add(ImagePart(f));
        }
        parts.Add(new JsonObject { ["text"] = ReviewPrompt.Replace("{tn}", templateFrames.Count.ToString(CultureInfo.InvariantCulture)).Replace("{guide}", styleGuide) });
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["score"] = new JsonObject { ["type"] = "NUMBER" },
                ["summary"] = new JsonObject { ["type"] = "STRING" },
                ["problems"] = new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" } },
            },
            ["required"] = new JsonArray("score", "summary", "problems"),
        };
        var json = await CallAsync(parts,
            new JsonObject { ["temperature"] = 0.1, ["maxOutputTokens"] = 4096, ["responseMimeType"] = "application/json", ["responseSchema"] = schema }, log, ct, GeminiTask.Light).ConfigureAwait(false);
        try
        {
            var r = JsonSerializer.Deserialize<StyleReview>(GeminiScriptGenerator.TextOf(json)) ?? new();
            r.Score = Math.Clamp(r.Score, 0, 10);
            r.Problems = r.Problems.Where(p => !string.IsNullOrWhiteSpace(p)).Take(10).ToList();
            return r;
        }
        catch (JsonException e) { throw PipelineException.Transient("gemini_bad_json", "Uslub tekshiruvi JSON emas: " + e.Message); }
    }

    /// <summary>Fayl nomidagi vaqt (frame_012.5s.jpg → "12.5 s"), bo'lmasa fayl nomi.</summary>
    internal static string FrameLabel(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+(?:\.\d+)?)s$");
        return m.Success ? m.Groups[1].Value + " s" : Path.GetFileName(path);
    }

    private static JsonObject ImagePart(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : "image/png";
        return new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mime, ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)) } };
    }

    private async Task<JsonNode> CallAsync(JsonArray parts, JsonObject generationConfig, IJobLog log, CancellationToken ct, GeminiTask task = GeminiTask.Vision)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generationConfig,
        };
        // 503 (yuqori yuklama), tarmoq va vaqt tugashi - qisqa kutib qayta urinish; 429/kalit xatolari CallAsync ichida hal qilinadi.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var (json, _, _) = await _gen.CallAsync(_gen.KeysForCall(), Models, task, body, TimeSpan.FromSeconds(180), log, ct).ConfigureAwait(false);
                return json;
            }
            catch (PipelineException e) when (attempt < 3 && e.Code is "gemini_server" or "gemini_network" or "gemini_timeout" or "gemini_empty")
            {
                log.Warn($"Gemini vision vaqtincha ishlamadi ({e.Message}); {attempt * 10} s dan keyin qayta urinadi.", "Edit");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 10), ct).ConfigureAwait(false);
            }
        }
    }
}
