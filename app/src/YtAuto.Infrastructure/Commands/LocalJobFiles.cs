using System.Text.Json;
using System.Text.Json.Serialization;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Infrastructure.Templates;

namespace YtAuto.Infrastructure.Commands;

/// <summary>
/// Kompyuterdagi video fayllarini o'chirish (botdan «chiqqan videolar tarixini tozalash» / «kanal ma'lumotlarini o'chirish» va
/// shablonni o'chirish). Faqat sozlamalardagi ildiz papkalar ichida (video/ovoz arxivi, ish papkasi, yozuvlar); symlink/junction
/// orqali tashqariga chiqilmaydi. Bot yuborgan yo'l ildizdan tashqarida bo'lsa - o'tkazib yuboriladi.
/// </summary>
public static class LocalJobFiles
{
    /// <summary>Arxivdagi video yonidagi fayllar (bir xil nom, boshqa kengaytma): subtitr, muqova, meta.</summary>
    private static readonly string[] Sidecars = { ".mp4", ".srt", ".vtt", ".jpg", ".json" };

    public sealed class JobFiles
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("video")] public string? Video { get; set; }
        [JsonPropertyName("audio")] public string? Audio { get; set; }
        [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
        [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
        [JsonPropertyName("work_dir")] public string? WorkDir { get; set; }
    }

    /// <summary>
    /// "delete_job_files" buyrug'i: arg - JSON {"jobs":[...]}. Har vazifaning arxivi (video + yonidagi fayllar, ovoz), ish papkasi va
    /// yozuvlar papkasi o'chiriladi; shu videodan avtomatik yaratilgan shablon ham olib tashlanadi (fayli yo'q shablon qolmasin).
    /// </summary>
    public static void DeleteJobs(AppServices svc, string arg)
    {
        var jobs = JsonSerializer.Deserialize<Payload>(arg)?.Jobs ?? new();
        if (jobs.Count == 0) return;
        var s = svc.Settings.Load();
        int files = 0, dirs = 0, templates = 0;
        foreach (var j in jobs)
        {
            if (!string.IsNullOrWhiteSpace(j.Video))
            {
                templates += RemoveTemplatesFor(s, j.Video!);
                files += DeleteVideoSet(s.Paths, j.Video!);
            }
            files += DeleteFile(s.Paths, j.Audio) + DeleteFile(s.Paths, j.Subtitle) + DeleteFile(s.Paths, j.Thumbnail);
            dirs += DeleteDir(s.Paths.WorkDir, j.WorkDir);
            if (j.Id > 0) dirs += DeleteDir(svc.Paths.RecordsDir, Path.Combine(svc.Paths.RecordsDir, $"job-{j.Id}"));
        }
        if (templates > 0) svc.Settings.Save(s); // bot shablonlar ro'yxati ham yangilanadi
        svc.Log.Info($"Botdan: {jobs.Count} ta video kompyuterdan o'chirildi ({files} fayl, {dirs} papka, {templates} shablon).");
    }

    /// <summary>Arxivdagi video va uning yonidagi fayllar (.srt/.vtt/.jpg/.json). O'chirilgan fayllar soni.</summary>
    public static int DeleteVideoSet(PathSettings p, string video)
    {
        var dir = Path.GetDirectoryName(video); var stem = Path.GetFileNameWithoutExtension(video);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return 0;
        var n = DeleteFile(p, video);
        foreach (var ext in Sidecars) n += DeleteFile(p, Path.Combine(dir, stem + ext));
        return n;
    }

    /// <summary>Faylni o'chiradi - faqat video/ovoz arxivi yoki ish papkasi ichida, link bo'lmasa. 1 - o'chirildi.</summary>
    public static int DeleteFile(PathSettings p, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        try
        {
            var full = PathGuard.Full(path);
            var roots = new[] { p.VideoArchiveDir, p.AudioArchiveDir, p.WorkDir }.Where(r => !string.IsNullOrWhiteSpace(r));
            if (!roots.Any(r => PathGuard.IsUnder(r, full)) || !File.Exists(full) || PathGuard.IsReparsePoint(full)) return 0;
            if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
            return 1;
        }
        catch { return 0; }
    }

    private static int DeleteDir(string root, string? dir)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(dir)) return 0;
        try
        {
            if (!PathGuard.IsUnder(root, dir) || !Directory.Exists(dir)) return 0;
            PathGuard.SafeDeleteDirectory(root, dir);
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Shu arxiv videosiga ishora qiluvchi (Managed=false) shablonlar va ularning kichik rasm papkasi. Soni.</summary>
    private static int RemoveTemplatesFor(AppSettings s, string video)
    {
        var full = PathGuard.Full(video);
        var hit = s.Templates.Where(t => !t.Managed && !string.IsNullOrWhiteSpace(t.Path) && string.Equals(PathGuard.Full(t.Path), full, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in hit)
        {
            s.Templates.Remove(t);
            if (string.Equals(s.Defaults.Template, t.Name, StringComparison.OrdinalIgnoreCase)) s.Defaults.Template = "";
            TemplateStore.DeleteThumbnail(s.Paths.TemplatesDir, t);
        }
        return hit.Count;
    }

    private sealed class Payload { [JsonPropertyName("jobs")] public List<JobFiles> Jobs { get; set; } = new(); }
}
