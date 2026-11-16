using System.Diagnostics;
using System.IO.Compression;

namespace YtAuto.Infrastructure.Templates;

/// <summary>
/// Botdan (Supabase Storage orqali) yuklangan shablonlarni shu kompyuterga yuklab olib o'rnatadi.
/// Ilova ochiq bo'lganda har 30 soniyada chaqiriladi. Holat (yuklanmoqda %, tayyor, xato) bazaga yoziladi, bot ko'rsatadi.
/// Xavfsizlik: faqat https://*.supabase.co manzillari, hajm cheklovi, zip ichida yo'l buzishdan (zip-slip) himoya,
/// MP4 uchun fayl turi tekshiruvi; fayl faqat ilovaning shablonlar papkasiga nusxalanadi.
/// </summary>
public static class RemoteTemplateImporter
{
    private static int _running;
    private const long MaxBytes = 600L * 1024 * 1024;

    public static async Task RunPendingAsync(AppServices svc, CancellationToken ct)
    {
        if (!svc.IsOnline) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            while (svc.Store.ClaimTemplateUpload(Environment.MachineName) is { } job)
                await ImportOneAsync(svc, job, ct).ConfigureAwait(false);
        }
        catch (Exception e) { svc.Log.Warn("Shablon yuklashda xato: " + e.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static async Task ImportOneAsync(AppServices svc, (long Id, string Name, string Kind, string Url, long Size) job, CancellationToken ct)
    {
        var tmp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "downloads", $"tpl-{job.Id}");
        try
        {
            svc.Log.Info($"Botdan shablon keldi: '{job.Name}' ({job.Kind}) - yuklab olinmoqda.");
            if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Yuklab olish manzili ishonchsiz (faqat https://*.supabase.co).");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            var isProject = job.Kind == "OpenFilmProject";
            if (isProject && ext != ".zip") ext = ".zip";
            if (!isProject && !TemplateStore.VideoExtensions.Contains(ext)) ext = ".mp4";
            var file = Path.Combine(tmp, "download" + ext);

            using (var resp = await svc.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? job.Size;
                if (total > MaxBytes) throw new InvalidOperationException($"Fayl juda katta ({total / 1048576} MB).");
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(file);
                var buf = new byte[256 * 1024]; long done = 0; int n; var sw = Stopwatch.StartNew(); var last = TimeSpan.Zero;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (done > MaxBytes) throw new InvalidOperationException("Fayl ruxsat etilgan hajmdan oshdi.");
                    if (sw.Elapsed - last < TimeSpan.FromSeconds(2)) continue;
                    last = sw.Elapsed;
                    svc.Store.UpdateTemplateUpload(job.Id, "downloading", total > 0 ? (int)Math.Min(90, done * 90 / total) : 10, null);
                }
            }

            svc.Store.UpdateTemplateUpload(job.Id, "importing", 92, "O'rnatilmoqda (nusxa, kichik rasm)…");
            var source = file;
            if (isProject)
            {
                var projectDir = Path.Combine(tmp, "project");
                SafeExtract(file, projectDir);
                source = FindProjectRoot(projectDir) ?? throw new InvalidOperationException("Zip ichida OpenFilm loyihasi topilmadi (film.html yo'q).");
            }

            var s = svc.Settings.Load();
            var name = job.Name.Trim(); var baseName = name; var k = 2;
            while (s.Templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} ({k++})";
            var store = new TemplateStore(svc.Media(svc.FilmTool()));
            var def = await store.ImportAsync(s.Paths.TemplatesDir, source, name, "Bot orqali yuklangan", ct).ConfigureAwait(false);
            s.Templates.Add(def);
            svc.Settings.Save(s); // katalog (bot ro'yxati) ham yangilanadi
            svc.Store.UpdateTemplateUpload(job.Id, "done", 100, $"O'rnatildi: '{def.Name}'");
            svc.Log.Info($"Shablon o'rnatildi: '{def.Name}'.");
        }
        catch (Exception e)
        {
            svc.Log.Warn($"Shablon #{job.Id} o'rnatilmadi: {e.Message}");
            try { svc.Store.UpdateTemplateUpload(job.Id, "error", 0, e.Message.Length > 300 ? e.Message[..300] : e.Message); } catch { }
        }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
    }

    private static void SafeExtract(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        long total = 0;
        using var za = ZipFile.OpenRead(zip);
        foreach (var e in za.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Zip ichida xavfli yo'l topildi.");
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            total += e.Length;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidOperationException("Zip ochilgandagi hajmi juda katta.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    private static string? FindProjectRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "film.html"))) return dir;
        var subs = Directory.GetDirectories(dir);
        return subs.Length == 1 && File.Exists(Path.Combine(subs[0], "film.html")) ? subs[0] : null;
    }
}
