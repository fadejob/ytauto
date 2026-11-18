using System.Globalization;
using System.Text;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Qa;
using YtAuto.Core.Script;
using YtAuto.Core.Visuals;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Video sifati: shablon uslub to'plami (kadrlar + Gemini STYLE_GUIDE.md), ochiq manbalardan real rasmlar va tayyor videoni
/// shablon bilan solishtirish. Bularning hech biri ishni to'xtatmaydi: xato bo'lsa ogohlantirish yoziladi va video
/// oddiy (shablon kadrlari bilan) usulda davom etadi.
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>Uslub to'plami: shablon kadrlari (edit/ ga nisbiy) va STYLE_GUIDE.md matni (bo'lmasa "").</summary>
    internal sealed record TemplateStyle(IReadOnlyList<string> Frames, IReadOnlyList<string> FrameFiles, string Guide);

    private static readonly double[] StyleFractions = { 0.03, 0.1, 0.18, 0.26, 0.34, 0.42, 0.5, 0.58, 0.66, 0.74, 0.84, 0.95 };

    /// <summary>
    /// Shablon videosidan 12 kadr va (sozlamada yoqilgan bo'lsa) Gemini dizayn qo'llanmasi. Natija shablonlar papkasida
    /// keshlanadi (fayl yo'li + hajm + vaqt bo'yicha): har videoda qayta tahlil qilinmaydi.
    /// </summary>
    private async Task<TemplateStyle> PrepareTemplateStyleAsync(JobContext c, TemplateDefinition? tpl)
    {
        var refMp4 = tpl?.Kind == TemplateKind.Mp4Reference ? tpl.Path : tpl?.ReferenceMp4;
        if (string.IsNullOrWhiteSpace(refMp4) || !File.Exists(refMp4)) return new(Array.Empty<string>(), Array.Empty<string>(), "");

        var fi = new FileInfo(refMp4);
        var key = Hashing.Sha256Text("style-v1", fi.FullName, fi.Length.ToString(CultureInfo.InvariantCulture), fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))[..16];
        var root = string.IsNullOrWhiteSpace(c.S.Paths.TemplatesDir) ? Path.Combine(c.EditDir, "style-cache") : c.S.Paths.TemplatesDir;
        var cacheDir = Path.Combine(root, "_style", key);
        Directory.CreateDirectory(cacheDir);

        var cached = new List<string>();
        var info = await _x.Media.ProbeAsync(refMp4, c.Ct).ConfigureAwait(false);
        for (int i = 0; i < StyleFractions.Length; i++)
        {
            var p = Path.Combine(cacheDir, $"frame_{i + 1:00}.jpg");
            if (!File.Exists(p))
                try { await _x.Media.ExtractFrameAsync(refMp4, info.Duration * StyleFractions[i], p, 1280, c.Ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { c.Log.Debug($"Shablon kadri olinmadi ({StyleFractions[i]:P0}): {e.Message}", "Edit"); }
            if (File.Exists(p)) cached.Add(p);
        }

        var guidePath = Path.Combine(cacheDir, "STYLE_GUIDE.md");
        var guide = File.Exists(guidePath) ? File.ReadAllText(guidePath) : "";
        if (guide.Length == 0 && c.S.Visuals.TemplateStyleGuide && _x.Visuals is not null && cached.Count >= 3)
        {
            try
            {
                guide = await _x.Visuals.TemplateStyleGuideAsync(cached, tpl!.Name, tpl.Notes ?? "", c.Log, c.Ct).ConfigureAwait(false);
                if (guide.Length > 200) { File.WriteAllText(guidePath, guide, new UTF8Encoding(false)); c.Log.Info($"Shablon «{tpl.Name}» uslubi tahlil qilindi (STYLE_GUIDE.md, {cached.Count} kadr).", "Edit"); }
                else guide = "";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.Log.Warn($"Shablon uslubini tahlil qilib bo'lmadi, faqat kadrlar beriladi: {e.Message}", "Edit");
                guide = "";
            }
        }

        // Claude o'z papkasi ichidagilarni o'qiydi: kadrlar va qo'llanma edit/reference/ ga nusxalanadi.
        var refDir = Path.Combine(c.EditDir, "reference");
        Directory.CreateDirectory(refDir);
        var rel = new List<string>();
        foreach (var f in cached)
        {
            var dst = Path.Combine(refDir, "style_" + Path.GetFileName(f));
            if (!File.Exists(dst)) File.Copy(f, dst, true);
            rel.Add(Path.GetRelativePath(c.EditDir, dst).Replace('\\', '/'));
        }
        if (guide.Length > 0) File.WriteAllText(Path.Combine(refDir, "STYLE_GUIDE.md"), guide, new UTF8Encoding(false));
        return new(rel, cached, guide);
    }

    private string MediaCacheDir(JobContext c) => Path.Combine(c.EditDir, "media_cache");
    private string MediaManifestPath(JobContext c) => Path.Combine(MediaCacheDir(c), "manifest.json");

    /// <summary>
    /// Real rasmlar: Gemini ssenariydan rasm rejasini tuzadi, ochiq manbalardan yuklanadi (litsenziyasi tekshirilgan), so'ng
    /// film/assets/media/ ga nusxalanadi. Natija ssenariy matni bo'yicha keshlanadi (tuzatish sikllarida qayta yuklanmaydi).
    /// </summary>
    private async Task<MediaManifest?> PrepareMediaAsync(JobContext c, VideoScript script, IReadOnlyList<SubtitleCue> cues)
    {
        if (!c.S.Visuals.RealMedia || c.S.Visuals.MaxMedia <= 0 || _x.Visuals is null || _x.MediaLibrary is null) return null;
        var cacheDir = MediaCacheDir(c);
        var planHash = Hashing.Sha256Text("media-v1", script.SubtitleText, c.S.Visuals.MaxMedia.ToString(CultureInfo.InvariantCulture));
        MediaManifest? m = null;
        if (File.Exists(MediaManifestPath(c)))
        {
            try
            {
                var old = MediaManifest.FromJson(File.ReadAllText(MediaManifestPath(c)));
                if (old.PlanHash == planHash && old.Assets.All(a => File.Exists(Path.Combine(cacheDir, a.File)))) m = old;
            }
            catch { }
        }
        if (m is null)
        {
            try
            {
                if (Directory.Exists(cacheDir)) PathGuard.SafeDeleteDirectory(c.WorkRoot, cacheDir, s => c.Log.Debug(s));
                Directory.CreateDirectory(cacheDir);
                var plan = await _x.Visuals.PlanMediaAsync(script.SubtitleText, cues, c.S.ChannelContext?.MontageBlock ?? "", c.S.Visuals.MaxMedia, c.Log, c.Ct).ConfigureAwait(false);
                c.Log.Info($"Rasm rejasi: {plan.Items.Count} ta ({string.Join(", ", plan.Items.Select(i => $"{i.Kind}:{i.Query}"))}).", "Media");
                m = plan.Items.Count == 0 ? new MediaManifest() : await _x.MediaLibrary.FetchAsync(plan, cacheDir, c.Log, c.Ct).ConfigureAwait(false);
                m.PlanHash = planHash;
                File.WriteAllText(MediaManifestPath(c), m.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.Log.Warn($"Real rasmlarni tayyorlab bo'lmadi, video chizilgan grafika bilan davom etadi: {e.Message}", "Media");
                return null;
            }
        }
        var dst = Path.Combine(c.FilmDir, "assets", "media");
        Directory.CreateDirectory(dst);
        foreach (var a in m.Assets)
        {
            File.Copy(Path.Combine(cacheDir, a.File), Path.Combine(dst, a.File), true);
            a.File = "assets/media/" + a.File;
        }
        File.WriteAllText(Path.Combine(c.RecordDir, "media.json"), m.ToJson(), new UTF8Encoding(false));
        c.StageMetrics["media_assets"] = m.Assets.Count;
        c.StageMetrics["media_credits"] = m.Assets.Count(a => a.NeedsCredit);
        return m;
    }

    /// <summary>YouTube tavsifi uchun: CC BY rasmlar mualliflari (bo'lmasa "").</summary>
    internal string MediaCreditsBlock(JobContext c)
    {
        try { return File.Exists(MediaManifestPath(c)) ? MediaManifest.FromJson(File.ReadAllText(MediaManifestPath(c))).CreditsBlock() : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Render'dan keyin: video kadrlari shablon kadrlari va STYLE_GUIDE.md bilan solishtiriladi. Ball past bo'lsa - tuzatish
    /// sikli qolgan bo'lsa Claude'ga qaytariladigan xato, qolmagan bo'lsa ogohlantirish.
    /// </summary>
    private async Task ReviewStyleAsync(JobContext c, IReadOnlyList<double> times, QaReport r)
    {
        if (!c.S.Visuals.StyleReview || _x.Visuals is null || times.Count == 0) return;
        var refDir = Path.Combine(c.EditDir, "reference");
        var guidePath = Path.Combine(refDir, "STYLE_GUIDE.md");
        var tplFrames = Directory.Exists(refDir) ? Directory.GetFiles(refDir, "style_frame_*.jpg").OrderBy(f => f).ToList() : new();
        if (!File.Exists(guidePath) || tplFrames.Count < 3) return;
        try
        {
            var picked = times.Count <= 8 ? times.ToList() : Enumerable.Range(0, 8).Select(i => times[i * times.Count / 8]).ToList();
            var dir = Path.Combine(c.QaDir, "style");
            Directory.CreateDirectory(dir);
            var frames = new List<string>();
            foreach (var t in picked)
            {
                var p = Path.Combine(dir, $"frame_{t.ToString("0.0", CultureInfo.InvariantCulture)}s.jpg");
                await _x.Media.ExtractFrameAsync(c.FinalMp4, t, p, 960, c.Ct).ConfigureAwait(false);
                if (File.Exists(p)) frames.Add(p);
            }
            if (frames.Count == 0) return;
            var tplPick = tplFrames.Count <= 6 ? tplFrames : Enumerable.Range(0, 6).Select(i => tplFrames[i * tplFrames.Count / 6]).ToList();
            var review = await _x.Visuals.ReviewAgainstTemplateAsync(tplPick, frames, File.ReadAllText(guidePath), c.Log, c.Ct).ConfigureAwait(false);
            r.Checked.Add("style_match");
            r.Metrics["style_score"] = review.Score;
            c.Log.Info($"Shablonga moslik: {review.Score:0.0}/10. {review.Summary}", "Validate");
            if (review.Score >= c.S.Visuals.MinStyleScore || review.Problems.Count == 0) return;
            var canFix = c.Job.QaFixCycles < c.S.Limits.MaxQaFixCycles;
            var text = $"The video does not match the channel template style well enough (score {review.Score:0.0}/10, need {c.S.Visuals.MinStyleScore:0.0}). " +
                       "Re-read reference/STYLE_GUIDE.md and the reference frames, then fix: " + string.Join(" | ", review.Problems);
            r.Add("style_match", canFix ? QaSeverity.Error : QaSeverity.Warning, text);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.Warn("Shablonga moslik tekshiruvi o'tkazib yuborildi: " + e.Message, "Validate");
        }
    }
}
