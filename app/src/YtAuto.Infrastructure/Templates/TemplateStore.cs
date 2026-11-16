using YtAuto.Core;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Templates;

/// <summary>
/// Shablonlar ilovaning lokal shablonlar papkasida saqlanadi: yuklangan video (uslub namunasi) yoki OpenFilm loyiha papkasi
/// nusxalanadi, asl fayl o'zgartirilmaydi va keyin kerak bo'lmaydi. Har shablon - alohida papka (id), ichida thumbnail.
/// O'chirish faqat shablonlar papkasi ichida, symlink/junction'larga kirmasdan.
/// </summary>
public sealed class TemplateStore
{
    public static readonly string[] VideoExtensions = { ".mp4", ".mov", ".webm", ".mkv", ".m4v" };
    private readonly IMediaProbe? _media;
    public TemplateStore(IMediaProbe? media) => _media = media;

    public async Task<TemplateDefinition> ImportAsync(string templatesDir, string source, string name, string notes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templatesDir)) throw new InvalidOperationException("Shablonlar papkasi sozlanmagan.");
        Directory.CreateDirectory(templatesDir);
        var id = $"{TextNormalizer.Slug(name, 30)}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(templatesDir, id);
        if (PathGuard.IsUnder(templatesDir, source) || (Directory.Exists(source) && PathGuard.IsUnder(source, templatesDir)))
            throw new InvalidOperationException("Manba shablonlar papkasining ichida bo'lmasligi kerak.");
        Directory.CreateDirectory(dir);
        try
        {
            var def = new TemplateDefinition { Name = name.Trim(), Notes = notes ?? "", AddedAtUtc = DateTimeOffset.UtcNow, Managed = true };
            string? previewVideo;
            if (File.Exists(source))
            {
                var ext = Path.GetExtension(source).ToLowerInvariant();
                if (!VideoExtensions.Contains(ext)) throw new InvalidOperationException($"Qo'llab-quvvatlanmaydigan fayl turi: {ext} (MP4/MOV/WEBM/MKV).");
                var copy = await FileOps.CopyVerifiedAsync(source, Path.Combine(dir, "template" + ext), "template", ct).ConfigureAwait(false);
                def.Kind = TemplateKind.Mp4Reference; def.Path = copy.Path; def.SizeBytes = copy.Size;
                previewVideo = copy.Path;
            }
            else if (Directory.Exists(source))
            {
                if (!File.Exists(Path.Combine(source, "film.html")))
                    throw new InvalidOperationException("Bu papka OpenFilm loyihasi emas (film.html yo'q).");
                var project = Path.Combine(dir, "project");
                PathGuard.CopyDirectory(source, project, f =>
                {
                    var rel = Path.GetRelativePath(source, f).Replace('\\', '/');
                    return !(rel.StartsWith(".git") || rel.StartsWith(".film/cache") || rel == ".film/cache");
                });
                def.Kind = TemplateKind.OpenFilmProject; def.Path = project;
                def.SizeBytes = Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                // Loyiha ichida render qilingan video bo'lsa, undan rasm olinadi.
                previewVideo = Directory.EnumerateFiles(project, "*.mp4", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            else throw new FileNotFoundException("Manba topilmadi", source);

            if (previewVideo is not null && _media is not null)
            {
                try
                {
                    var info = await _media.ProbeAsync(previewVideo, ct).ConfigureAwait(false);
                    var thumb = Path.Combine(dir, "thumb.jpg");
                    await _media.ExtractFrameAsync(previewVideo, Math.Min(info.Duration * 0.3, Math.Max(0, info.Duration - 0.2)), thumb, 480, ct).ConfigureAwait(false);
                    def.Thumbnail = thumb;
                    if (def.Kind == TemplateKind.Mp4Reference && string.IsNullOrWhiteSpace(def.Notes))
                        def.Notes = $"{info.Width}x{info.Height}, {info.Duration:0.#}s";
                }
                catch { /* thumbnail ixtiyoriy */ }
            }
            return def;
        }
        catch
        {
            try { PathGuard.SafeDeleteDirectory(templatesDir, dir); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Arxivdagi tayyor videoni NUSXALAMASDAN shablon sifatida ro'yxatdan o'tkazadi (Path - arxivdagi fayl, Managed=false):
    /// faqat kichik rasm shablonlar papkasiga yoziladi. Arxiv fayli hech qachon shablon orqali o'chirilmaydi.
    /// </summary>
    public async Task<TemplateDefinition> RegisterExistingVideoAsync(string templatesDir, string videoPath, string name, string notes, CancellationToken ct)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video topilmadi", videoPath);
        var def = new TemplateDefinition
        {
            Name = name.Trim(), Notes = notes ?? "", Kind = TemplateKind.Mp4Reference, Path = videoPath, Managed = false,
            SizeBytes = new FileInfo(videoPath).Length, AddedAtUtc = DateTimeOffset.UtcNow,
        };
        if (_media is not null && !string.IsNullOrWhiteSpace(templatesDir))
        {
            try
            {
                var dir = Path.Combine(templatesDir, $"{TextNormalizer.Slug(name, 30)}-{Guid.NewGuid().ToString("N")[..6]}");
                Directory.CreateDirectory(dir);
                var info = await _media.ProbeAsync(videoPath, ct).ConfigureAwait(false);
                var thumb = Path.Combine(dir, "thumb.jpg");
                await _media.ExtractFrameAsync(videoPath, Math.Min(info.Duration * 0.3, Math.Max(0, info.Duration - 0.2)), thumb, 480, ct).ConfigureAwait(false);
                def.Thumbnail = thumb;
                if (string.IsNullOrWhiteSpace(def.Notes)) def.Notes = $"{info.Width}x{info.Height}, {info.Duration:0.#}s";
            }
            catch { /* kichik rasm ixtiyoriy */ }
        }
        return def;
    }

    /// <summary>Faqat ilova o'zi nusxalagan (Managed) shablon papkasini o'chiradi.</summary>
    public static void DeleteFiles(string templatesDir, TemplateDefinition t)
    {
        if (!t.Managed || string.IsNullOrWhiteSpace(templatesDir) || string.IsNullOrWhiteSpace(t.Path)) return;
        var full = PathGuard.Full(t.Path);
        if (!PathGuard.IsUnder(templatesDir, full)) return;
        // Shablon papkasi = templatesDir ning bevosita bolasi.
        var dir = full;
        while (!string.Equals(PathGuard.Full(Path.GetDirectoryName(dir)!), PathGuard.Full(templatesDir), StringComparison.OrdinalIgnoreCase))
            dir = Path.GetDirectoryName(dir)!;
        PathGuard.SafeDeleteDirectory(templatesDir, dir);
    }
}
