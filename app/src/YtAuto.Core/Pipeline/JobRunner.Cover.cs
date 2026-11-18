using System.Globalization;
using System.Text.RegularExpressions;
using YtAuto.Core.Domain;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// Video muqovasi (cover) har videoda bo'lishi kafolatlanadi:
///   1) Claude chizgan film/cover.html → `openfilm look cover.html 0` (to'liq o'lchamli PNG);
///   2) sahifa yo'q yoki chizilmasa → loyihaning o'zidan kadr (`openfilm look &lt;t&gt;`: subtitrlar render'da kuydiriladi, bu yerda yo'q);
///   3) u ham bo'lmasa → tayyor videodan kadr.
/// Natija render/cover.jpg: YouTube thumbnail va arxivdagi MP4 ichidagi rasm (attached_pic).
/// </summary>
public sealed partial class JobRunner
{
    /// <summary>YouTube thumbnail chegarasi 2 MB; 1280 px kenglik (vertikalda 720) yetarli va kichik.</summary>
    private static int CoverWidth(JobContext c) { var (w, h) = c.StageSize; return h > w ? 720 : 1280; }

    private async Task RenderCoverAsync(JobContext c, IFilmSession film)
    {
        if (File.Exists(c.CoverJpg)) File.Delete(c.CoverJpg);
        if (File.Exists(c.CoverHtml) && await TryLookCoverAsync(c, film, new[] { "cover.html", "0" }, "look-cover").ConfigureAwait(false))
        {
            c.Log.Info("Muqova (cover.html) tayyor.", "Render");
            return;
        }
        c.Log.Warn(File.Exists(c.CoverHtml) ? "cover.html rasmga aylanmadi — muqova videoning o'zidan olinadi." : "Claude cover.html yaratmagan — muqova videoning o'zidan olinadi.", "Render");
        // Birinchi sahna to'liq chizilgan payt (subtitrsiz kadr).
        var dur = c.Job.AudioDurationSec ?? 10;
        var t = Math.Clamp(dur * 0.08, 0.8, 3.0).ToString("0.0", CultureInfo.InvariantCulture);
        if (await TryLookCoverAsync(c, film, new[] { t }, "look-cover-frame").ConfigureAwait(false)) c.Log.Info($"Muqova loyiha kadridan ({t}s) olindi.", "Render");
    }

    /// <summary>`openfilm look ...` chiqargan PNG'ni topib cover.jpg ga aylantiradi. Bo'sh (bir xil rangli) rasm qabul qilinmaydi.</summary>
    private async Task<bool> TryLookCoverAsync(JobContext c, IFilmSession film, string[] args, string prefix)
    {
        try
        {
            var since = DateTime.UtcNow;
            var r = await film.LookAsync(c.FilmDir, args, prefix, TimeSpan.FromMinutes(3), c.Ct).ConfigureAwait(false);
            if (r.ExitCode != 0 || r.TimedOut) return false;
            var png = LookPng(c.FilmDir, r, since);
            if (png is null) return false;
            await _x.Media.ExtractFrameAsync(png, 0, c.CoverJpg, CoverWidth(c), c.Ct).ConfigureAwait(false);
            return CoverLooksReal(c.CoverJpg);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.Log.Warn($"Muqova chizilmadi ({string.Join(' ', args)}): {e.Message}", "Render");
            return false;
        }
    }

    /// <summary>
    /// look chiqargan PNG: chiqishdagi oxirgi mavjud .png yo'li (qator bo'yicha - yo'lda bo'sh joy bo'lishi mumkin; nisbiy bo'lsa
    /// film papkasiga nisbatan), topilmasa `.film/cache/look/` dagi shu chaqiruvdan keyin yozilgan eng yangi PNG.
    /// </summary>
    internal static string? LookPng(string filmDir, ToolRunResult r, DateTime? sinceUtc = null)
    {
        var text = r.StdoutTail;
        try { if (File.Exists(r.StdoutPath)) text = File.ReadAllText(r.StdoutPath); } catch { }
        foreach (var line in text.Split('\n').Reverse())
        {
            var end = line.LastIndexOf(".png", StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            var head = line[..(end + 4)].Trim().Trim('"', '\'', '`');
            // To'liq yo'l (bo'sh joyli bo'lishi mumkin) haydovchi harfidan boshlanadi; aks holda oxirgi so'z - nisbiy yo'l.
            var drive = Regex.Match(head, @"[A-Za-z]:[\\/]");
            var p = drive.Success ? head[drive.Index..] : head.Split(' ', '\t').Last().Trim('"', '\'', '`');
            try
            {
                var full = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(filmDir, p));
                if (File.Exists(full)) return full;
            }
            catch { /* yo'l emas */ }
        }
        var cache = Path.Combine(filmDir, ".film", "cache", "look");
        if (!Directory.Exists(cache)) return null;
        return new DirectoryInfo(cache).EnumerateFiles("*.png", SearchOption.AllDirectories)
            .Where(f => sinceUtc is null || f.LastWriteTimeUtc >= sinceUtc.Value.AddSeconds(-2))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    private static bool CoverLooksReal(string jpg) => File.Exists(jpg) && new FileInfo(jpg).Length > 0;

    /// <summary>
    /// Muqovani YouTube thumbnail qiladi: yangi yuklangan video hali qayta ishlanayotganda rad etilishi mumkin - bir marta qayta urinadi.
    /// O'rnatilmasa sababi logga yoziladi (masalan kanal telefon orqali tasdiqlanmagan - YouTube maxsus thumbnail'ga ruxsat bermaydi).
    /// Video baribir chiqadi: muqova MP4 ichida ham bor.
    /// </summary>
    private async Task SetThumbnailAsync(JobContext c, string videoId, string image)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (await _x.Uploader.SetThumbnailAsync(videoId, image, c.Ct).ConfigureAwait(false)) { c.Log.Info("Muqova YouTube thumbnail qilib o'rnatildi.", "Upload"); return; }
                c.Log.Warn("Muqova o'rnatilmadi: rasm 2 MB dan katta.", "Upload");
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (attempt == 2 || e is PipelineException { Category: ErrorCategory.Configuration })
                {
                    c.Log.Warn($"Muqova YouTube'ga o'rnatilmadi: {e.Message}. Kanal telefon raqami bilan tasdiqlanmagan bo'lsa (youtube.com/verify) YouTube maxsus thumbnail qabul qilmaydi.", "Upload");
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(20), c.Ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Oxirgi chora: tayyor videodan kadr (subtitr bo'lishi mumkin) — muqovasiz video chiqmaydi.</summary>
    private async Task EnsureCoverFromVideoAsync(JobContext c)
    {
        if (File.Exists(c.CoverJpg)) return;
        try
        {
            var info = await _x.Media.ProbeAsync(c.FinalMp4, c.Ct).ConfigureAwait(false);
            await _x.Media.ExtractFrameAsync(c.FinalMp4, Math.Min(info.Duration * 0.3, Math.Max(0, info.Duration - 0.5)), c.CoverJpg, CoverWidth(c), c.Ct).ConfigureAwait(false);
            c.Log.Warn("Muqova tayyor videodan olindi (cover.html ham, loyiha kadri ham chiqmadi).", "Render");
        }
        catch (Exception e) when (e is not OperationCanceledException) { c.Log.Warn($"Muqova yaratilmadi: {e.Message}", "Render"); }
    }
}
