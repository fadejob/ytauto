using System.Globalization;
using System.Text;
using System.Text.Json;
using YtAuto.Core.Demo;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Media;
using YtAuto.Core.Script;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Loyiha demo videosi (Claude Code MCP orqali yuborgan materiallar). Mavjud pipeline'ning o'zi ishlatiladi - farqi faqat:
/// <list type="bullet">
/// <item>Ssenariy: materiallar yuklab olinadi va Gemini ularni ko'rib 2 bosqichda yozadi (qoralama → faqat aytiladigan matn);
/// sahnalar (qaysi materialda nima aytiladi) alohida metadata bo'lib saqlanadi.</item>
/// <item>Montaj: stok rasmlar o'rniga loyiha screenshot/videolari, sahna vaqtlari ovozdan; zoom/pan/o'tishlar va SFX.</item>
/// <item>Arxivdan keyin: natija serverga yuboriladi; chiqarish so'ralmagan bo'lsa vazifa "Tayyor" holatida to'xtaydi.</item>
/// </list>
/// </summary>
public sealed partial class JobRunner
{
    private const int DemoScriptAttempts = 3;

    internal static DemoSpec DemoSpecOf(JobContext c) => DemoSpec.FromJson(c.Job.DemoSpecJson!);
    private static string DemoDir(JobContext c) => Path.Combine(c.Dir, "demo");
    private static string DemoManifestPath(JobContext c) => Path.Combine(DemoDir(c), "media.json");
    private static string DemoScenesPath(JobContext c) => Path.Combine(DemoDir(c), "scenes.json");

    /// <summary>Imzolangan havolaning tokeni hash'ga kirmaydi: havola yangilansa ham ssenariy qayta yozilmaydi.</summary>
    private static string DemoScriptHash(JobContext c)
    {
        var spec = DemoSpecOf(c);
        foreach (var m in spec.Media) m.Url = m.Url.Split('?')[0];
        return Hashing.Sha256Text("demo-script-v1", spec.ToJson(), c.S.Gemini.Model, c.Job.Format.ToString())
            + ChannelHashSuffix(c.S.ChannelContext.ScriptBlock) + $":meta-d{WriteDescription(c)}-t{WriteTags(c)}";
    }

    // ---------------------------------------------------------------- B: ssenariy (Gemini, 2 bosqich)
    private async Task<StageOutputs> DemoScriptStageAsync(JobContext c)
    {
        var writer = _x.DemoWriter ?? throw PipelineException.Config("demo_writer_missing", "Demo ssenariy servisi ulanmagan (YtAuto'ni yangilang).");
        var spec = DemoSpecOf(c);
        var media = await PrepareDemoMediaAsync(c, spec).ConfigureAwait(false);
        var ids = media.Items.Select(m => m.Id).ToList();
        var previews = media.Items.ToDictionary(m => m.Id, m => (IReadOnlyList<string>)m.Previews.Select(p => Path.Combine(c.Dir, p)).Where(File.Exists).ToList());
        bool writeDesc = WriteDescription(c), writeTags = WriteTags(c);
        var format = c.Job.Format switch { VideoFormat.Shorts9x16 => "vertical 9:16 short", VideoFormat.Square1x1 => "square 1:1", _ => "horizontal 16:9" };

        // 1-bosqich: qoralama (Gemini materiallarni ko'radi).
        DemoDraft? draft = null; var problems = new List<string>(); int promptTokens = 0, outputTokens = 0; string model = "";
        for (int attempt = 1; attempt <= DemoScriptAttempts; attempt++)
        {
            var req = new DemoScriptRequest
            {
                Spec = spec, Media = media.Items, PreviewFiles = previews, Format = format, Model = c.S.Gemini.Model,
                ChannelContext = c.S.ChannelContext?.ScriptBlock ?? "", WriteDescription = writeDesc, WriteTags = writeTags, PreviousProblems = problems,
            };
            var r = await Retry.TransientAsync(_ => writer.DraftAsync(req, c.Log, c.Ct), c.S.Limits.MaxTransientRetries,
                (n, e) => c.Log.Warn($"Gemini (demo qoralama) vaqtinchalik xato ({n}): {e.Message}", "Script"), c.Ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(DemoDir(c), $"gemini_draft_{attempt}.json"), r.Raw, new UTF8Encoding(false));
            promptTokens += r.PromptTokens; outputTokens += r.OutputTokens; model = r.ModelUsed;
            draft = r.Value;
            problems = new List<string>();
            if (string.IsNullOrWhiteSpace(draft.YouTubeTitle)) problems.Add("youtube_title bo'sh.");
            var cleaned = DemoRules.CleanScenes(draft.Scenes, ids);
            problems.AddRange(DemoRules.ValidateScenes(cleaned, ids));
            if (problems.Count == 0) break;
            c.Log.Warn($"Demo qoralama talablarga mos emas (urinish {attempt}/{DemoScriptAttempts}): {string.Join(" | ", problems)}", "Script");
        }
        if (draft is null || problems.Count > 0)
            throw PipelineException.Quality("demo_draft_invalid", "Gemini demo ssenariy qoralamasi talablarga mos kelmadi: " + string.Join(" | ", problems));
        c.Log.Info($"1-bosqich (Gemini): ssenariy qoralamasi - {draft.Scenes.Count} sahna, {ScriptValidator.CountWords(string.Join(" ", draft.Scenes.Select(s => s.Narration)))} so'z.", "Script");

        // 2-bosqich: optimallashtirish - faqat aytiladigan matn; takror, sahna ko'rsatmalari va ortiqcha tushuntirishlar olib tashlanadi.
        VideoScript? script = null; List<DemoScene>? scenes = null; problems = new List<string>();
        for (int attempt = 1; attempt <= DemoScriptAttempts; attempt++)
        {
            var req = new DemoOptimizeRequest { Spec = spec, Draft = draft, Media = media.Items, Model = c.S.Gemini.Model, PreviousProblems = problems };
            var r = await Retry.TransientAsync(_ => writer.OptimizeAsync(req, c.Log, c.Ct), c.S.Limits.MaxTransientRetries,
                (n, e) => c.Log.Warn($"Gemini (demo optimallashtirish) vaqtinchalik xato ({n}): {e.Message}", "Script"), c.Ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(DemoDir(c), $"gemini_final_{attempt}.json"), r.Raw, new UTF8Encoding(false));
            promptTokens += r.PromptTokens; outputTokens += r.OutputTokens;
            var notes = new List<string>();
            scenes = DemoRules.CleanScenes(r.Value, ids, notes);
            foreach (var n in notes) c.Log.Info("Demo matni: " + n, "Script");
            script = new VideoScript
            {
                SubtitleText = DemoRules.JoinNarration(scenes), YouTubeTitle = draft.YouTubeTitle.Trim(),
                YouTubeDescription = draft.YouTubeDescription.Replace("<", "‹").Replace(">", "›"), Tags = draft.Tags,
                Scenes = scenes.Select((s, i) => new SceneSpec { Index = i + 1, Narration = s.Narration, Visual = s.MediaId, OnScreenText = s.OnScreenText }).ToList(),
            };
            problems = DemoRules.ValidateScenes(scenes, ids);
            problems.AddRange(ScriptValidator.Validate(script, c.Job.Language, c.Job.TargetDurationSec));
            if (problems.Count == 0) break;
            c.Log.Warn($"Demo matni talablarga mos emas (urinish {attempt}/{DemoScriptAttempts}): {string.Join(" | ", problems)}", "Script");
        }
        if (script is null || scenes is null || problems.Count > 0)
            throw PipelineException.Quality("demo_script_invalid", "Gemini demo matnini talablarga moslay olmadi: " + string.Join(" | ", problems));

        ScriptValidator.Canonicalize(script);
        ApplyMetadataRule(c, script, writeDesc, writeTags);
        File.WriteAllText(c.ScriptJson, script.ToJson(), new UTF8Encoding(false));
        File.WriteAllText(DemoScenesPath(c), JsonSerializer.Serialize(scenes, DemoSpec.Json), new UTF8Encoding(false));
        File.Copy(c.ScriptJson, Path.Combine(c.RecordDir, "script.json"), true);
        File.Copy(DemoScenesPath(c), Path.Combine(c.RecordDir, "demo_scenes.json"), true);

        c.Job.Title = script.YouTubeTitle;
        c.Job.Description = writeDesc ? BuildDescription(script) : "";
        _x.Store.Update(c.Job);
        c.StageMetrics["words"] = ScriptValidator.CountWords(script.SubtitleText);
        c.StageMetrics["scenes"] = scenes.Count;
        c.StageMetrics["demo_media"] = media.Items.Count;
        c.StageMetrics["prompt_tokens"] = promptTokens; c.StageMetrics["output_tokens"] = outputTokens;
        c.Log.Info($"2-bosqich (Gemini): yakuniy narration {c.StageMetrics["words"]} so'z, {scenes.Count} sahna, {scenes.Select(s => s.MediaId).Distinct().Count()}/{media.Items.Count} material ishlatildi. Title \"{script.YouTubeTitle}\".", "Script");
        return new StageOutputs
        {
            Files = { FileOps.Describe(c.ScriptJson, "script"), FileOps.Describe(DemoScenesPath(c), "demo_scenes"), FileOps.Describe(DemoManifestPath(c), "demo_media") },
            Values = { ["title"] = script.YouTubeTitle, ["model"] = model, ["kind"] = "demo" },
        };
    }

    /// <summary>
    /// Materiallarni yuklab oladi (oldingi urinishda yuklangani joyida bo'lsa qayta yuklanmaydi), o'lchami/davomiyligini o'qiydi
    /// va Gemini uchun kichik kadrlar tayyorlaydi (rasm - 1, video - 3 ta).
    /// </summary>
    private async Task<DemoMediaManifest> PrepareDemoMediaAsync(JobContext c, DemoSpec spec)
    {
        var fetcher = _x.DemoMedia ?? throw PipelineException.Config("demo_media_missing", "Demo materiallarini yuklab olish servisi ulanmagan.");
        if (spec.Media.Count == 0) throw PipelineException.Config("demo_no_media", "Demo so'rovida birorta screenshot yoki video yo'q.");
        if (spec.Media.Count > DemoRules.MaxMedia) throw PipelineException.Config("demo_too_many_media", $"Materiallar {DemoRules.MaxMedia} tadan ko'p.");
        var dir = Path.Combine(DemoDir(c), "media"); var prevDir = Path.Combine(DemoDir(c), "preview");
        Directory.CreateDirectory(dir); Directory.CreateDirectory(prevDir);
        DemoMediaManifest? old = null;
        if (File.Exists(DemoManifestPath(c))) try { old = DemoMediaManifest.FromJson(File.ReadAllText(DemoManifestPath(c))); } catch { }

        var manifest = new DemoMediaManifest();
        for (int i = 0; i < spec.Media.Count; i++)
        {
            var m = spec.Media[i];
            var prev = old?.Items.FirstOrDefault(x => x.Id == m.Id);
            if (prev is not null && File.Exists(Path.Combine(c.Dir, prev.File)) && Hashing.Sha256File(Path.Combine(c.Dir, prev.File)) == prev.Sha256
                && prev.Previews.All(p => File.Exists(Path.Combine(c.Dir, p))))
            {
                manifest.Items.Add(prev);
                continue;
            }
            var stem = Path.Combine(dir, $"{i + 1:00}");
            var local = await Retry.TransientAsync(_ => fetcher.FetchAsync(m, stem, c.Log, c.Ct), c.S.Limits.MaxTransientRetries,
                (n, e) => c.Log.Warn($"Material {i + 1} ({m.Title}) yuklab olinmadi, qayta urinish ({n}): {e.Message}", "Script"), c.Ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var info = await _x.Media.ProbeAsync(local, c.Ct).ConfigureAwait(false);
            if (!info.HasVideo || info.Width <= 0) throw PipelineException.Config("demo_media_bad", $"Material \"{m.Title}\" ({m.Id}) rasm yoki video sifatida o'qilmadi.");
            var item = new DemoLocalMedia
            {
                Id = m.Id, Kind = m.IsVideo ? DemoMediaKinds.Video : DemoMediaKinds.Screenshot, File = Path.GetRelativePath(c.Dir, local).Replace('\\', '/'),
                Width = info.Width, Height = info.Height, DurationSec = m.IsVideo ? Math.Round(info.Duration, 2) : 0, Sha256 = Hashing.Sha256File(local),
            };
            var times = m.IsVideo ? new[] { 0.15, 0.5, 0.85 }.Select(f => Math.Max(0, info.Duration * f)).ToArray() : new[] { 0.0 };
            for (int k = 0; k < times.Length; k++)
            {
                var p = Path.Combine(prevDir, $"{i + 1:00}_{k + 1}.jpg");
                try { await _x.Media.ExtractFrameAsync(local, times[k], p, 1280, c.Ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { c.Log.Debug($"Kadr olinmadi ({m.Id} @ {times[k]:0.0}s): {e.Message}", "Script"); }
                if (File.Exists(p)) item.Previews.Add(Path.GetRelativePath(c.Dir, p).Replace('\\', '/'));
            }
            manifest.Items.Add(item);
            c.Log.Info($"Material {i + 1}/{spec.Media.Count}: {item.Kind} \"{m.Title}\" {item.Width}x{item.Height}{(m.IsVideo ? $", {item.DurationSec:0.0}s" : "")}.", "Script");
        }
        File.WriteAllText(DemoManifestPath(c), manifest.ToJson(), new UTF8Encoding(false));
        return manifest;
    }

    // ---------------------------------------------------------------- E: montaj materiallari
    /// <summary>
    /// Montaj uchun: materiallar film/assets/demo/ ga, SFX film/assets/sfx/ ga nusxalanadi; har sahna vaqti ovozdagi so'z vaqtlaridan.
    /// </summary>
    private DemoPlanScene[] PrepareDemoPlan(JobContext c, double audioSec)
    {
        var spec = DemoSpecOf(c);
        var manifest = DemoMediaManifest.FromJson(File.ReadAllText(DemoManifestPath(c)));
        var scenes = JsonSerializer.Deserialize<List<DemoScene>>(File.ReadAllText(DemoScenesPath(c)), DemoSpec.Json) ?? new();
        var words = File.Exists(c.AlignmentJson)
            ? JsonSerializer.Deserialize<AlignmentResult>(File.ReadAllText(c.AlignmentJson))?.Words ?? new()
            : new List<AlignedWord>();
        var times = DemoRules.SceneTimes(scenes, words, audioSec);

        var assets = Path.Combine(c.FilmDir, "assets", "demo");
        Directory.CreateDirectory(assets);
        var fileOf = new Dictionary<string, string>();
        foreach (var m in manifest.Items)
        {
            var src = Path.Combine(c.Dir, m.File);
            var name = Path.GetFileName(src);
            File.Copy(src, Path.Combine(assets, name), true);
            fileOf[m.Id] = "assets/demo/" + name;
        }
        Sfx.WriteAll(Path.Combine(c.FilmDir, "assets", "sfx"));

        var plan = new List<DemoPlanScene>();
        for (int i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            var lm = manifest.Items.FirstOrDefault(m => m.Id == s.MediaId);
            var sm = spec.Media.FirstOrDefault(m => m.Id == s.MediaId);
            if (lm is null) continue;
            plan.Add(new DemoPlanScene
            {
                Index = i + 1, MediaId = s.MediaId, File = fileOf[s.MediaId], Kind = lm.Kind, Start = times[i].Start, End = times[i].End,
                Narration = s.Narration, Focus = string.IsNullOrWhiteSpace(s.Focus) ? sm?.Highlight ?? "" : s.Focus, OnScreenText = s.OnScreenText,
                MediaTitle = sm?.Title ?? "", MediaDescription = sm?.Description ?? "", Width = lm.Width, Height = lm.Height, MediaDurationSec = lm.DurationSec,
            });
        }
        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(c.EditDir, "demo_plan.json"), json, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(c.RecordDir, "demo_plan.json"), json, new UTF8Encoding(false));
        c.StageMetrics["demo_scenes"] = plan.Count;
        c.Log.Info($"Demo montaj rejasi: {plan.Count} sahna, {manifest.Items.Count} material (assets/demo), SFX: {string.Join(", ", Sfx.Catalog.Keys)}.", "Edit");
        return plan.ToArray();
    }

    // ---------------------------------------------------------------- arxivdan keyin
    /// <summary>
    /// Natija serverga yuboriladi. Chiqarish so'ralgan va ruxsat bo'lsa true emas, false qaytadi (pipeline YouTube'ga yuklashga o'tadi);
    /// aks holda vazifa "Tayyor" holatida to'xtaydi (true).
    /// </summary>
    private async Task<bool> FinishDemoAsync(JobContext c)
    {
        var spec = DemoSpecOf(c);
        await DeliverDemoResultAsync(c).ConfigureAwait(false);

        if (spec.Publish.Requested)
        {
            string? why = null;
            if (_x.PublishAllowed is { } allowed && !allowed()) why = "bu hisob uchun MCP orqali YouTube'ga chiqarish ruxsat etilmagan (faqat asosiy hisob)";
            else if (!_x.Uploader.IsConnected) why = "YouTube kanali ulanmagan";
            if (why is null)
            {
                c.PublishNow = spec.Publish.Now;
                if (!spec.Publish.Now && DateTimeOffset.TryParse(spec.Publish.When, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    c.Job.PublishAtUtc = at.ToUniversalTime();
                    _x.Store.Update(c.Job);
                }
                c.Log.Info(spec.Publish.Now
                    ? "Demo video: MCP so'rovida chiqarish buyurilgan — asosiy YouTube kanaliga darhol public yuklanadi."
                    : $"Demo video: MCP so'rovida chiqarish buyurilgan — asosiy YouTube kanaliga {c.Job.PublishAtUtc:u} ga rejalashtirib yuklanadi.", "Upload");
                return false;
            }
            c.Log.Warn($"Demo video chiqarilmadi: {why}. Video tayyor holatda qoldi (publish_video bilan keyin chiqarish mumkin).", "Upload");
        }

        JobStateMachine.EnsureTransition(c.Job.Status, JobStatus.Ready);
        c.Job.Status = JobStatus.Ready;
        _x.Store.Update(c.Job);
        c.Log.Info("Demo video tayyor: YouTube'ga chiqarish alohida buyruq bilan (MCP publish_video yoki botdagi tugma).", "Archive");
        return true;
    }

    /// <summary>Tayyor MP4 (muqovasi bilan), muqova va subtitr serverga yuboriladi. Xato videoni to'xtatmaydi - u arxivda bor.</summary>
    private async Task DeliverDemoResultAsync(JobContext c)
    {
        var video = c.Job.VideoArchivePath!;
        var sha = Hashing.Sha256File(video);
        DemoResult? existing = null;
        try { if (!string.IsNullOrEmpty(c.Job.DemoResultJson)) existing = JsonSerializer.Deserialize<DemoResult>(c.Job.DemoResultJson!, DemoSpec.Json); } catch { }
        if (existing is { Error: null, VideoPath: not null } && existing.Sha256 == sha)
        {
            c.Log.Info("Demo natijasi serverda allaqachon bor - qayta yuborilmaydi.", "Archive");
            return;
        }
        DemoResult result;
        if (_x.DemoResults is null) result = new DemoResult { Error = "Natijani serverga yuborish servisi ulanmagan." };
        else
        {
            try
            {
                var cover = File.Exists(c.CoverJpg) ? c.CoverJpg : null;
                var srt = File.Exists(c.VoiceSrt) ? c.VoiceSrt : c.Job.SubtitleArchivePath;
                result = await Retry.TransientAsync(_ => _x.DemoResults.DeliverAsync(new VideoJobRef(c.Job.Id, c.Job.AudioDurationSec ?? 0), video, cover, srt, c.Log, c.Ct),
                    c.S.Limits.MaxTransientRetries, (n, e) => c.Log.Warn($"Natijani serverga yuborish qayta urinish ({n}): {e.Message}", "Archive"), c.Ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                c.Log.Info($"Demo natijasi serverga yuborildi ({result.SizeBytes / 1048576.0:0.0} MB): MCP get_video_result havola beradi.", "Archive");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result = new DemoResult { Error = Shorten(e.Message, 400) };
                c.Log.Warn($"Demo natijasini serverga yuborib bo'lmadi (video kompyuter arxivida bor): {e.Message}", "Archive");
            }
        }
        result.Sha256 ??= sha;
        result.LocalPath = video;
        result.DurationSec = result.DurationSec > 0 ? result.DurationSec : c.Job.AudioDurationSec ?? 0;
        result.Machine ??= Environment.MachineName;
        c.Job.DemoResultJson = result.ToJson();
        _x.Store.SetDemoResult(c.Job.Id, c.Job.DemoResultJson);
    }
}
