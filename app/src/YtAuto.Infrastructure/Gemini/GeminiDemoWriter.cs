using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core.Demo;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Script;

namespace YtAuto.Infrastructure.Gemini;

/// <summary>
/// Loyiha demo videosining ssenariysi - Gemini, ikki bosqichda (kalitlar navbati va 429 almashinuvi <see cref="GeminiScriptGenerator"/> bilan umumiy):
/// <list type="number">
/// <item>Qoralama: loyiha konteksti + har materialning kadrlari (vision) → sahnalar (qaysi materialda nima aytiladi), title, tavsif, taglar.</item>
/// <item>Optimallashtirish: qoralamadan ortiqcha tushuntirish, sahna ko'rsatmalari va takrorlar olib tashlanadi - faqat ovozda aytiladigan matn.</item>
/// </list>
/// </summary>
public sealed class GeminiDemoWriter : IDemoScriptWriter
{
    private readonly GeminiScriptGenerator _gen;
    public GeminiDemoWriter(GeminiScriptGenerator gen) => _gen = gen;

    /// <summary>Gemini'ga yuboriladigan kadrlar chegarasi (so'rov hajmi ~20 MB dan oshmasligi uchun).</summary>
    internal const int MaxImages = 40;

    public const string DraftPrompt = """
You are the scriptwriter of a short product demo video for YouTube. The video shows a real software project using ONLY the
screenshots and screen recordings provided above (each one is labelled with its media_id). Look at every image carefully:
describe what is really visible; never invent features, numbers or UI that are not in the materials or the project description.

PROJECT
Name: {name}
What it is: {summary}
{extra}
Key features:
{features}

MATERIALS (in the order the user prepared them)
{materials}

REQUIREMENTS
Language of the narration, title and description: {language_name} ({language})
Format: {format}
{length_rule}
Audience: {audience}
Tone: {tone}
User notes: {notes}
{channel}
Write the video as a sequence of scenes. Each scene shows exactly ONE material (media_id) while the voice talks about it.
- Order scenes as a story: hook (what problem the project solves) → the main workflow, feature by feature → a short close with the
  project name{close}.
- Use every important material at least once; a material may be used in two consecutive scenes if there is a lot to say.
- narration: what the voice says during the scene, natural spoken sentences, 1-4 sentences, addressed to the viewer.
- focus: where the camera should zoom on that material (e.g. "the Save button top right", "the chart in the centre"). Not spoken.
- on_screen_text: an optional 2-5 word label shown on screen. Not spoken.
- youtube_title: max 70 characters, clear and specific, no clickbait, no < or > characters.
""";

    public const string OptimizePrompt = """
You are the editor of a product demo narration. Below is a draft scene list for the project "{name}". Rewrite it into the FINAL
narration that will be read aloud by a text-to-speech voice and shown as subtitles.

Rules:
- Keep the same scene order and the same media_id for every scene you keep. You may merge two consecutive scenes that show the same media_id.
- narration must contain ONLY words that are spoken: remove stage directions, camera/scene/visual instructions, labels like
  "Scene 2:" or "Narrator:", brackets, markdown, emojis, URLs and any text meant for the screen.
- Remove filler, repeated statements and over-explaining. Every sentence must say something new about what the viewer sees.
- Keep facts exactly as in the draft; do not add new claims.
- Write numbers and symbols the way they are spoken. Short sentences that sound natural when read aloud.
- Language: {language_name} ({language}). {length_rule}
- focus and on_screen_text: keep or tighten them (they are not spoken).
{problems}
DRAFT (JSON):
{draft}
""";

    public async Task<DemoStepResult<DemoDraft>> DraftAsync(DemoScriptRequest r, IJobLog log, CancellationToken ct)
    {
        var parts = new JsonArray();
        int images = 0;
        foreach (var m in r.Media)
        {
            var spec = r.Spec.Media.FirstOrDefault(x => x.Id == m.Id);
            parts.Add(new JsonObject { ["text"] = $"media_id: {m.Id} ({m.Kind}{(m.Kind == DemoMediaKinds.Video ? $", {m.DurationSec:0.#} s, frames at start/middle/end" : "")}) — {spec?.Title}" });
            if (r.PreviewFiles.TryGetValue(m.Id, out var files))
                foreach (var f in files)
                {
                    if (images >= MaxImages) break;
                    parts.Add(ImagePart(f)); images++;
                }
        }
        var prompt = FillDraft(r);
        parts.Add(new JsonObject { ["text"] = prompt });

        var schema = DraftSchema(r.WriteDescription, r.WriteTags);
        var body = Body(parts, schema, 0.6, 16384);
        var (json, _) = await _gen.CallAsync(_gen.KeysForCall(), Model(r.Model), body, TimeSpan.FromSeconds(240), log, ct).ConfigureAwait(false);
        var text = GeminiScriptGenerator.TextOf(json);
        DemoDraft draft;
        try { draft = JsonSerializer.Deserialize<DemoDraft>(text, DemoSpec.Json) ?? new DemoDraft(); }
        catch (JsonException e) { throw PipelineException.Transient("gemini_bad_json", "Demo qoralama javobi JSON emas: " + e.Message); }
        draft.Tags = r.WriteTags ? ScriptValidator.CleanTags(draft.Tags, DefaultPrompts.TagsPerVideo) : new();
        if (!r.WriteDescription) draft.YouTubeDescription = "";
        return Result(draft, text, json, r.Model);
    }

    public async Task<DemoStepResult<List<DemoScene>>> OptimizeAsync(DemoOptimizeRequest r, IJobLog log, CancellationToken ct)
    {
        var draftJson = JsonSerializer.Serialize(new { scenes = r.Draft.Scenes }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var prompt = OptimizePrompt.Replace("{name}", r.Spec.Project.Name)
            .Replace("{language_name}", LanguageNames.Of(r.Spec.Requirements.Language)).Replace("{language}", r.Spec.Requirements.Language)
            .Replace("{length_rule}", LengthRule(r.Spec))
            .Replace("{problems}", r.PreviousProblems.Count == 0 ? "" : "\nYour previous answer was rejected by an automatic check. Fix ALL of these problems:\n- " + string.Join("\n- ", r.PreviousProblems) + "\n")
            .Replace("{draft}", draftJson);
        var body = Body(new JsonArray(new JsonObject { ["text"] = prompt }), ScenesSchema(), 0.3, 16384);
        var (json, _) = await _gen.CallAsync(_gen.KeysForCall(), Model(r.Model), body, TimeSpan.FromSeconds(180), log, ct).ConfigureAwait(false);
        var text = GeminiScriptGenerator.TextOf(json);
        List<DemoScene> scenes;
        try { scenes = JsonNode.Parse(text)?["scenes"]?.Deserialize<List<DemoScene>>(DemoSpec.Json) ?? new(); }
        catch (JsonException e) { throw PipelineException.Transient("gemini_bad_json", "Demo matni javobi JSON emas: " + e.Message); }
        return Result(scenes, text, json, r.Model);
    }

    internal static string FillDraft(DemoScriptRequest r)
    {
        var s = r.Spec; var p = s.Project; var q = s.Requirements;
        var extra = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(p.Url)) extra.AppendLine($"Website: {p.Url}");
        if (!string.IsNullOrWhiteSpace(p.Repository)) extra.AppendLine($"Repository: {p.Repository}");
        if (!string.IsNullOrWhiteSpace(p.TechStack)) extra.AppendLine($"Built with: {p.TechStack}");
        var features = p.Features.Count == 0 ? "(see the materials)" : string.Join("\n", p.Features.Select(f => $"- {f.Name}: {f.Description}"));
        var materials = string.Join("\n", s.Media.Select(m =>
            $"- {m.Id} [{m.Kind}] {m.Title}: {m.Description}" + (string.IsNullOrWhiteSpace(m.Feature) ? "" : $" (feature: {m.Feature})")
            + (string.IsNullOrWhiteSpace(m.Highlight) ? "" : $" (important area: {m.Highlight})")));
        return DraftPrompt.Replace("{name}", p.Name).Replace("{summary}", p.Summary).Replace("{extra}", extra.ToString().TrimEnd())
            .Replace("{features}", features).Replace("{materials}", materials)
            .Replace("{language_name}", LanguageNames.Of(q.Language)).Replace("{language}", q.Language).Replace("{format}", r.Format)
            .Replace("{length_rule}", LengthRule(s)).Replace("{audience}", Or(q.Audience, "developers and potential users of the project"))
            .Replace("{tone}", Or(q.Tone, "clear, confident, friendly; no hype")).Replace("{notes}", Or(q.Notes, "(none)"))
            .Replace("{channel}", string.IsNullOrWhiteSpace(r.ChannelContext) ? "" : "\nCHANNEL STYLE (keep the voice consistent with it):\n" + r.ChannelContext.Trim() + "\n")
            .Replace("{close}", string.IsNullOrWhiteSpace(p.Url) ? "" : " and where to find it")
            + GeminiScriptGenerator.MetadataInstruction(r.WriteDescription, r.WriteTags)
            + (r.PreviousProblems.Count == 0 ? "" : "\nYour previous answer was rejected by an automatic check. Fix ALL of these problems:\n- " + string.Join("\n- ", r.PreviousProblems));
    }

    internal static string LengthRule(DemoSpec s)
    {
        var wps = LanguageNames.WordsPerSecond(s.Requirements.Language);
        if (s.Requirements.DurationSec > 0)
            return $"Target length: about {s.Requirements.DurationSec} seconds of narration in total, i.e. about {Math.Round(s.Requirements.DurationSec * wps)} spoken words across all scenes.";
        var auto = Math.Clamp(s.Media.Count * 12 + 15, 45, 180);
        return $"Length: as long as the materials need, normally {auto - 15}-{auto + 15} seconds (about {Math.Round((auto - 15) * wps)}-{Math.Round((auto + 15) * wps)} spoken words in total).";
    }

    private static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    private static string Model(string m) => string.IsNullOrWhiteSpace(m) ? "gemini-2.5-flash" : m.Trim();

    private static DemoStepResult<T> Result<T>(T value, string raw, JsonNode json, string model) => new()
    {
        Value = value, Raw = raw, ModelUsed = Model(model),
        PromptTokens = json["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0,
        OutputTokens = json["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0,
    };

    private static JsonObject Body(JsonArray parts, JsonObject schema, double temperature, int maxTokens) => new()
    {
        ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
        ["generationConfig"] = new JsonObject
        {
            ["temperature"] = temperature, ["maxOutputTokens"] = maxTokens,
            ["responseMimeType"] = "application/json", ["responseSchema"] = schema,
        },
    };

    private static JsonObject Str() => new() { ["type"] = "STRING" };

    private static JsonObject SceneSchema() => new()
    {
        ["type"] = "OBJECT",
        ["properties"] = new JsonObject { ["media_id"] = Str(), ["narration"] = Str(), ["focus"] = Str(), ["on_screen_text"] = Str() },
        ["required"] = new JsonArray("media_id", "narration"),
        ["propertyOrdering"] = new JsonArray("media_id", "narration", "focus", "on_screen_text"),
    };

    internal static JsonObject ScenesSchema() => new()
    {
        ["type"] = "OBJECT",
        ["properties"] = new JsonObject { ["scenes"] = new JsonObject { ["type"] = "ARRAY", ["items"] = SceneSchema() } },
        ["required"] = new JsonArray("scenes"),
    };

    internal static JsonObject DraftSchema(bool writeDescription, bool writeTags)
    {
        var props = new JsonObject { ["youtube_title"] = Str() };
        var order = new List<string> { "youtube_title" };
        if (writeDescription) { props["youtube_description"] = Str(); order.Add("youtube_description"); }
        if (writeTags)
        {
            props["tags"] = new JsonObject { ["type"] = "ARRAY", ["items"] = Str(), ["minItems"] = DefaultPrompts.TagsPerVideo, ["maxItems"] = DefaultPrompts.TagsPerVideo };
            order.Add("tags");
        }
        props["scenes"] = new JsonObject { ["type"] = "ARRAY", ["items"] = SceneSchema() };
        order.Add("scenes");
        return new JsonObject
        {
            ["type"] = "OBJECT", ["properties"] = props,
            ["required"] = new JsonArray(order.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
            ["propertyOrdering"] = new JsonArray(order.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
        };
    }

    private static JsonObject ImagePart(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var mime = ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : "image/png";
        return new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mime, ["data"] = Convert.ToBase64String(File.ReadAllBytes(path)) } };
    }
}
