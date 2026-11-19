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
    /// <summary>Uslub to'plami: shablon kadrlari (edit/ ga nisbiy), STYLE_GUIDE.md va MOTION_GUIDE.md matni (bo'lmasa "").</summary>
    internal sealed record TemplateStyle(IReadOnlyList<string> Frames, IReadOnlyList<string> FrameFiles, string Guide, string Motion = "");

    private static readonly double[] StyleFractions = { 0.03, 0.1, 0.18, 0.26, 0.34, 0.42, 0.5, 0.58, 0.66, 0.74, 0.84, 0.95 };

    /// <summary>Gemini ko'radigan kadrlar chegarasi (MEDIA_RESOLUTION_LOW: kadr ~66 token, ya'ni tahlil ~30k token, bir marta).</summary>
    internal const int MotionMaxFrames = 450;
    private const double MotionWholeMaxSec = 150, MotionSegmentSec = 75;

    /// <summary>
    /// Harakat tahlili uchun qaysi qismlar va qanday fps: 150 s gacha - butun video; uzunrog'i - boshidan 75 s (intro, birinchi sahnalar)
    /// va o'rtasidan 75 s. fps = 1..5, jami kadr <see cref="MotionMaxFrames"/> dan oshmaydi (tez o'tishlarni ko'rish uchun imkon qadar yuqori).
    /// </summary>
    internal static (IReadOnlyList<(double Start, double Duration)> Segments, double Fps) MotionPlan(double duration)
    {
        if (duration < 2) return (Array.Empty<(double, double)>(), 0);
        var segs = duration <= MotionWholeMaxSec
            ? new List<(double, double)> { (0, duration) }
            : new List<(double, double)> { (0, MotionSegmentSec), (Math.Round(duration / 2 - MotionSegmentSec / 2, 1), MotionSegmentSec) };
        var total = segs.Sum(s => s.Item2);
        return (segs, Math.Clamp(Math.Floor(MotionMaxFrames / total), 1, 5));
    }

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

        var motion = await PrepareMotionGuideAsync(c, tpl!, refMp4, info.Duration, cacheDir).ConfigureAwait(false);

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
        if (motion.Length > 0) File.WriteAllText(Path.Combine(refDir, "MOTION_GUIDE.md"), motion, new UTF8Encoding(false));
        return new(rel, cached, guide, motion);
    }

    /// <summary>
    /// Shablon harakati (MOTION_GUIDE.md): kichik ovozsiz klip(lar) Gemini'ga VIDEO sifatida beriladi - animatsiya, o'tishlar, temp, easing.
    /// Shablon bo'yicha keshlanadi (bir marta tahlil qilinadi). Xato bo'lsa ogohlantirish, video faqat kadrlar bilan davom etadi.
    /// </summary>
    private async Task<string> PrepareMotionGuideAsync(JobContext c, TemplateDefinition tpl, string refMp4, double duration, string cacheDir)
    {
        var path = Path.Combine(cacheDir, "MOTION_GUIDE.md");
        if (File.Exists(path)) return File.ReadAllText(path);
        if (!c.S.Visuals.TemplateMotionGuide || _x.Visuals is null) return "";
        var (segs, fps) = MotionPlan(duration);
        if (segs.Count == 0) return "";
        var clips = new List<MotionClip>();
        try
        {
            foreach (var (start, dur) in segs)
            {
                var clip = Path.Combine(cacheDir, $"motion_{clips.Count + 1}.mp4");
                await _x.Media.MakeAnalysisClipAsync(refMp4, start, dur, clip, 640, c.Ct).ConfigureAwait(false);
                clips.Add(new MotionClip(clip, start, dur));
            }
            var bytes = clips.Sum(x => new FileInfo(x.Path).Length);
            if (bytes > 18L * 1024 * 1024) throw new InvalidOperationException($"tahlil klipi juda katta ({bytes / 1048576.0:0.0} MB)");
            var frames = (int)(segs.Sum(s => s.Duration) * fps);
            c.Log.Info($"Shablon «{tpl.Name}» harakati tahlil qilinmoqda: {segs.Sum(s => s.Duration):0} s video, {fps:0} kadr/s (~{frames} kadr, ~{frames * 66 / 1000 + 2}k token, bir marta).", "Edit");
            var motion = await _x.Visuals.TemplateMotionGuideAsync(clips, fps, tpl.Name, tpl.Notes ?? "", c.Log, c.Ct).ConfigureAwait(false);
            if (motion.Length <= 200) return "";
            File.WriteAllText(path, motion, new UTF8Encoding(false));
            c.Log.Info($"Shablon «{tpl.Name}» harakati tahlil qilindi (MOTION_GUIDE.md).", "Edit");
            return motion;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.Warn($"Shablon harakatini tahlil qilib bo'lmadi, faqat kadrlar va uslub qo'llanmasi beriladi: {e.Message}", "Edit");
            return "";
        }
        finally
        {
            foreach (var x in clips) try { File.Delete(x.Path); } catch { }
        }
    }

    private string MediaCacheDir(JobContext c) => Path.Combine(c.EditDir, "media_cache");
    private string MediaManifestPath(JobContext c) => Path.Combine(MediaCacheDir(c), "manifest.json");
    private string StockCacheDir(JobContext c) => Path.Combine(c.EditDir, "stock_cache");
    private string StockManifestPath(JobContext c) => Path.Combine(StockCacheDir(c), "stock.json");
    /// <summary>Claude ko'radigan kontakt varaqlar (edit/ ichida, film/ dan tashqarida - render'ga tushmaydi).</summary>
    internal const string StockReviewDir = "stock_review";
    /// <summary>Claude tanlovi: film/ ichida (Claude faqat shu papkaga yozadi).</summary>
    internal const string StockSelectionFile = "media_selection.json";

    /// <summary>Tayyorlangan real media: ikon/logo/fotolar (MediaLibrary) va stok nomzodlar (OpenFilm). Ikkalasi ham ixtiyoriy.</summary>
    internal sealed record PreparedMedia(MediaManifest? Media, StockCandidates? Stock);

    private static string StageOrientation(JobContext c) { var (w, h) = c.StageSize; return h > w ? "portrait" : "landscape"; }

    private bool StockEnabled(JobContext c)
    {
        var v = c.S.Visuals;
        return v.OpenFilmStock && v.MaxStockScenes > 0 && (v.StockImagesPerScene > 0 || v.StockClipsPerScene > 0) && _x.Stock is { IsConfigured: true };
    }

    /// <summary>
    /// Real rasmlar: Gemini ssenariydan rasm rejasini tuzadi. "photo" bandlari (sahnalar) uchun - OpenFilm stok qidiruvi (har biriga bir nechta
    /// rasm va video nomzod; tanlovni Claude qiladi). Ikon/logolar va OpenFilm hech narsa topmagan fotolar - ochiq manbalardan (MediaLibrary).
    /// Natija ssenariy matni va sozlama bo'yicha keshlanadi (tuzatish sikllarida qayta yuklanmaydi).
    /// </summary>
    private async Task<PreparedMedia> PrepareMediaAsync(JobContext c, VideoScript script, IReadOnlyList<SubtitleCue> cues)
    {
        var v = c.S.Visuals;
        if (!v.RealMedia || v.MaxMedia <= 0 || _x.Visuals is null) return new(null, null);
        var useStock = StockEnabled(c);
        if (_x.MediaLibrary is null && !useStock) return new(null, null);
        if (v.OpenFilmStock && !useStock && _x.Stock is not null)
            c.Log.Info("Stok video/rasm nomzodlari o'chiq: botda (Sozlamalar → Video sifati) Pexels yoki Pixabay kalitini kiriting - OpenFilm ular orqali qidiradi.", "Media");
        var inv = CultureInfo.InvariantCulture;
        var cacheDir = MediaCacheDir(c);
        var stockDir = StockCacheDir(c);
        var planHash = Hashing.Sha256Text("media-v2", script.SubtitleText, v.MaxMedia.ToString(inv),
            useStock ? $"stock:{v.StockImagesPerScene}:{v.StockClipsPerScene}:{v.MaxStockScenes}:{StageOrientation(c)}" : "no-stock");
        MediaManifest? m = null;
        StockCandidates? stock = null;
        if (File.Exists(MediaManifestPath(c)))
        {
            try
            {
                var old = MediaManifest.FromJson(File.ReadAllText(MediaManifestPath(c)));
                if (old.PlanHash == planHash && old.Assets.All(a => File.Exists(Path.Combine(cacheDir, a.File)))) m = old;
            }
            catch { }
        }
        if (m is not null && useStock)
        {
            // Stok keshi ham butun bo'lishi kerak; bo'lmasa hammasi qaytadan (reja bitta, ikkalasi undan chiqadi).
            try
            {
                var old = File.Exists(StockManifestPath(c)) ? StockCandidates.FromJson(File.ReadAllText(StockManifestPath(c))) : null;
                if (old is not null && old.PlanHash == planHash && old.Scenes.SelectMany(s => s.Candidates).All(x => File.Exists(Path.Combine(stockDir, x.File)))) stock = old;
                else m = null;
            }
            catch { m = null; }
        }
        if (m is null)
        {
            try
            {
                foreach (var d in new[] { cacheDir, stockDir })
                    if (Directory.Exists(d)) PathGuard.SafeDeleteDirectory(c.WorkRoot, d, s => c.Log.Debug(s));
                Directory.CreateDirectory(cacheDir);
                var plan = await _x.Visuals.PlanMediaAsync(script.SubtitleText, cues, c.S.ChannelContext?.MontageBlock ?? "", v.MaxMedia, c.Log, c.Ct).ConfigureAwait(false);
                c.Log.Info($"Rasm rejasi: {plan.Items.Count} ta ({string.Join(", ", plan.Items.Select(i => $"{i.Kind}:{i.Query}"))}).", "Media");
                var libPlan = plan;
                if (useStock)
                {
                    stock = await FetchStockAsync(c, plan, stockDir).ConfigureAwait(false) ?? new StockCandidates();
                    // Bo'sh natija ham keshlanadi: tuzatish sikllarida qayta qidirilmaydi.
                    stock.PlanHash = planHash;
                    Directory.CreateDirectory(stockDir);
                    File.WriteAllText(StockManifestPath(c), stock.ToJson(), new UTF8Encoding(false));
                    var covered = (stock?.Scenes ?? new()).Where(s => s.Candidates.Count > 0).Select(s => s.Query).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    libPlan = new MediaPlan { Items = plan.Items.Where(i => !(i.Kind == MediaKinds.Photo && covered.Contains(i.Query))).ToList() };
                }
                m = libPlan.Items.Count == 0 || _x.MediaLibrary is null ? new MediaManifest() : await _x.MediaLibrary.FetchAsync(libPlan, cacheDir, c.Log, c.Ct).ConfigureAwait(false);
                m.PlanHash = planHash;
                File.WriteAllText(MediaManifestPath(c), m.ToJson(), new UTF8Encoding(false));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.Log.Warn($"Real rasmlarni tayyorlab bo'lmadi, video chizilgan grafika bilan davom etadi: {e.Message}", "Media");
                return new(null, null);
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
        var filmStock = stock is { Total: > 0 } ? PlaceStockInFilm(c, stock) : null;
        return new(m, filmStock);
    }

    /// <summary>
    /// OpenFilm stok qidiruvi: rejadagi "photo" bandlari (ko'pi bilan MaxStockScenes) - sahnalar. Har nomzod ffprobe bilan tekshiriladi
    /// (o'qib bo'lmaydigani tashlanadi, haqiqiy o'lcham/davomiylik yoziladi), sahna papkasiga img1.jpg / clip1.mp4 nomi bilan qo'yiladi va
    /// har sahnaga kontakt varaq yasaladi. Xato videoni to'xtatmaydi: null - fotolar MediaLibrary'dan olinadi.
    /// </summary>
    private async Task<StockCandidates?> FetchStockAsync(JobContext c, MediaPlan plan, string stockDir)
    {
        var v = c.S.Visuals;
        var reqs = plan.Items.Where(i => i.Kind == MediaKinds.Photo).Take(v.MaxStockScenes)
            .Select((i, k) => new StockRequest($"s{k + 1:00}", i.Query, i.Purpose, i.At)).ToList();
        if (reqs.Count == 0) return null;
        var images = Math.Clamp(v.StockImagesPerScene, 0, 8);
        var clips = Math.Clamp(v.StockClipsPerScene, 0, 4);
        try
        {
            Directory.CreateDirectory(stockDir);
            c.Log.Info($"Stok: OpenFilm {reqs.Count} sahna uchun internetdan qidiradi (har biriga {images} rasm va {clips} video parcha nomzodi).", "Media");
            var raw = await _x.Stock!.FetchAsync(reqs, Path.Combine(stockDir, "openfilm"), new StockOptions(images, clips, StageOrientation(c)), c.Log, c.Ct).ConfigureAwait(false);
            foreach (var scene in raw.Scenes)
            {
                var dir = Path.Combine(stockDir, scene.Id);
                Directory.CreateDirectory(dir);
                var kept = new List<StockCandidate>();
                int ni = 0, nv = 0;
                foreach (var x in scene.Candidates.OrderBy(x => x.Type == StockTypes.Video ? 1 : 0))
                {
                    var video = x.Type == StockTypes.Video;
                    MediaInfo info;
                    try { info = await _x.Media.ProbeAsync(x.File, c.Ct).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { scene.Notes.Add($"{Path.GetFileName(x.File)} o'qilmadi: {e.Message}"); continue; }
                    if (!info.HasVideo || (video && info.Duration < 1)) { scene.Notes.Add($"{Path.GetFileName(x.File)}: rasm/video oqimi yo'q"); continue; }
                    if (info.Width > 0) { x.Width = info.Width; x.Height = info.Height; }
                    if (video) x.Duration = Math.Round(info.Duration, 2);
                    var ext = video ? ".mp4" : (Path.GetExtension(x.File) is { Length: > 1 } e2 ? e2.ToLowerInvariant() : ".jpg");
                    var name = video ? $"clip{++nv}{ext}" : $"img{++ni}{ext}";
                    x.Label = video ? $"V{nv}" : $"I{ni}";
                    var target = Path.Combine(dir, name);
                    File.Copy(x.File, target, true);
                    x.Bytes = new FileInfo(target).Length;
                    x.File = $"{scene.Id}/{name}";
                    kept.Add(x);
                }
                scene.Candidates = kept;
                if (kept.Count > 0)
                {
                    try { scene.Sheet = await MakeStockSheetAsync(c, scene, stockDir).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException) { c.Log.Warn($"Stok {scene.Id}: kontakt varaq yasalmadi ({e.Message}); Claude fayllarni o'zi ochadi.", "Media"); }
                }
            }
            // Asl yuklamalar endi sahna papkalarida: ikki nusxa disk egallamasin (loglar qoladi).
            try { var dl = Path.Combine(stockDir, "openfilm", "assets"); if (Directory.Exists(dl)) PathGuard.SafeDeleteDirectory(c.WorkRoot, dl, s => c.Log.Debug(s)); } catch { }
            c.Log.Info($"Stok nomzodlar tayyor: {raw.Scenes.Count(s => s.Candidates.Count > 0)}/{raw.Scenes.Count} sahna, {raw.Scenes.Sum(s => s.Images)} rasm, {raw.Scenes.Sum(s => s.Clips)} video " +
                       $"({raw.Scenes.SelectMany(s => s.Candidates).Sum(x => x.Bytes) / 1048576.0:0} MB). Tanlovni Claude qiladi.", "Media");
            return raw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.Warn($"OpenFilm stok qidiruvi ishlamadi, fotolar ochiq manbalardan olinadi: {e.Message}", "Media");
            return null;
        }
    }

    /// <summary>
    /// Sahna kontakt varag'i: 1-qator - rasmlar (I1..), keyingi har qator - bitta video (V1..) boshi/o'rtasi/oxiri kadrlari.
    /// Natija - stockDir ga nisbiy yo'l.
    /// </summary>
    private async Task<string> MakeStockSheetAsync(JobContext c, StockScene scene, string stockDir)
    {
        var (w, h) = c.StageSize;
        var (tw, th) = h > w ? (216, 384) : (384, 216);
        var tiles = new List<SheetTile>();
        var col = 0;
        foreach (var x in scene.Candidates.Where(x => x.Type == StockTypes.Image))
            tiles.Add(new SheetTile(Path.Combine(stockDir, x.File), $"{x.Label} {x.Width}x{x.Height}", 0, col++));
        var row = col > 0 ? 1 : 0;
        var framesDir = Path.Combine(stockDir, scene.Id, "frames");
        Directory.CreateDirectory(framesDir);
        foreach (var x in scene.Candidates.Where(x => x.Type == StockTypes.Video))
        {
            var marks = new[] { (0.15, $"{x.Label} {x.Duration.ToString("0", CultureInfo.InvariantCulture)}s {x.Width}x{x.Height}"), (0.5, $"{x.Label} mid"), (0.85, $"{x.Label} end") };
            for (int i = 0; i < marks.Length; i++)
            {
                var f = Path.Combine(framesDir, $"{x.Label}_{i + 1}.jpg");
                await _x.Media.ExtractFrameAsync(Path.Combine(stockDir, x.File), x.Duration * marks[i].Item1, f, 480, c.Ct).ConfigureAwait(false);
                tiles.Add(new SheetTile(f, marks[i].Item2, row, i));
            }
            row++;
        }
        var rel = $"{scene.Id}/sheet.jpg";
        await _x.Media.ContactSheetAsync(tiles, tw, th, Path.Combine(stockDir, rel), c.Ct).ConfigureAwait(false);
        return rel;
    }

    /// <summary>
    /// Nomzodlar film/assets/stock/ ga (Claude dizaynda ishlatishi uchun), kontakt varaqlar edit/stock_review/ ga ko'chiriladi.
    /// Katta video fayllar o'zgarmagan bo'lsa qayta nusxalanmaydi. Natija - film/ ga nisbiy yo'llar bilan (instructions.md va record uchun).
    /// </summary>
    private StockCandidates PlaceStockInFilm(JobContext c, StockCandidates stock)
    {
        var view = StockCandidates.FromJson(stock.ToJson());
        var reviewDir = Path.Combine(c.EditDir, StockReviewDir);
        Directory.CreateDirectory(reviewDir);
        foreach (var s in view.Scenes)
        {
            foreach (var x in s.Candidates)
            {
                var src = Path.Combine(StockCacheDir(c), x.File);
                var dst = Path.Combine(c.FilmDir, "assets", "stock", x.File);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length) File.Copy(src, dst, true);
                x.File = "assets/stock/" + x.File;
            }
            if (s.Sheet.Length > 0 && File.Exists(Path.Combine(StockCacheDir(c), s.Sheet)))
            {
                File.Copy(Path.Combine(StockCacheDir(c), s.Sheet), Path.Combine(reviewDir, $"{s.Id}.jpg"), true);
                s.Sheet = $"{StockReviewDir}/{s.Id}.jpg";
            }
            else s.Sheet = "";
        }
        File.WriteAllText(Path.Combine(c.RecordDir, "stock.json"), view.ToJson(), new UTF8Encoding(false));
        c.StageMetrics["stock_scenes"] = view.Scenes.Count(s => s.Candidates.Count > 0);
        c.StageMetrics["stock_candidates"] = view.Total;
        return view;
    }

    /// <summary>
    /// Claude tugagach: film/media_selection.json (tanlov) va film'da haqiqatda ishlatilgan stok fayllar solishtiriladi. Faqat log va
    /// metrika (video to'xtatilmaydi): nechta nomzoddan nechtasi tanlandi, rad etilganlardan biri ishlatilgan bo'lsa - ogohlantirish.
    /// </summary>
    internal void ReviewStockSelection(JobContext c, StockCandidates stock)
    {
        var known = stock.Scenes.SelectMany(s => s.Candidates).Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = StockReferences(c.FilmDir).Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(c.FilmDir, StockSelectionFile);
        var sel = File.Exists(path) ? StockSelection.TryParse(File.ReadAllText(path)) : null;
        c.StageMetrics["stock_used"] = used.Count;
        if (sel is null)
        {
            c.Log.Warn($"Claude {StockSelectionFile} yozmadi (yoki JSON buzilgan): {known.Count} stok nomzoddan {used.Count} tasi videoda ishlatilgan.", "Edit");
            return;
        }
        File.Copy(path, Path.Combine(c.RecordDir, StockSelectionFile), true);
        var chosen = sel.ChosenFiles.Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rejected = sel.RejectedFiles.Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        c.StageMetrics["stock_chosen"] = chosen.Count;
        c.StageMetrics["stock_rejected"] = rejected.Count;
        var reasons = sel.Scenes.SelectMany(s => s.Rejected).Where(p => p.Reason.Length > 0).Select(p => p.Reason.Trim()).Take(4).ToList();
        c.Log.Info($"Stok tanlovi (Claude): {known.Count} nomzoddan {chosen.Count} tasi tanlandi, {rejected.Count} tasi rad etildi, videoda {used.Count} tasi ishlatilgan" +
                   (reasons.Count > 0 ? $". Rad etish sabablari: {string.Join("; ", reasons)}" : "") + ".", "Edit");
        var bad = used.Where(rejected.Contains).ToList();
        if (bad.Count > 0) c.Log.Warn($"Claude o'zi rad etgan stok faylni ishlatgan: {string.Join(", ", bad)}.", "Edit");
        var unchecked_ = used.Where(f => !chosen.Contains(f) && !rejected.Contains(f)).ToList();
        if (unchecked_.Count > 0) c.Log.Warn($"Stok fayl tanlov ro'yxatisiz ishlatilgan: {string.Join(", ", unchecked_)}.", "Edit");
    }

    private static readonly System.Text.RegularExpressions.Regex StockRef = new(@"assets/stock/[A-Za-z0-9_\-]+/[A-Za-z0-9_\-]+\.[A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>film/ ichidagi HTML/CSS/JS fayllarda tilga olingan stok fayllar (assets/stock/sNN/...).</summary>
    internal static IReadOnlyCollection<string> StockReferences(string filmDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(filmDir)) return set;
        foreach (var f in Directory.EnumerateFiles(filmDir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".html" or ".htm" or ".css" or ".js" or ".mjs")) continue;
            var rel = Path.GetRelativePath(filmDir, f).Replace('\\', '/');
            if (rel.StartsWith(".film/") || rel.StartsWith("assets/")) continue;
            foreach (System.Text.RegularExpressions.Match m in StockRef.Matches(File.ReadAllText(f))) set.Add(m.Value);
        }
        return set;
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
