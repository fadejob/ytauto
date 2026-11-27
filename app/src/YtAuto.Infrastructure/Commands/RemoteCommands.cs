using System.Text.Json;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Templates;

namespace YtAuto.Infrastructure.Commands;

/// <summary>
/// Botdan kelgan, aynan shu kompyuterni o'zgartiradigan buyruqlar (ytauto.machine_commands): shablonni o'chirish, upload
/// sessiyasini tozalash va tarixdan o'chirilgan videolarning fayllarini o'chirish. Yengil worker (--poll) va ochiq ilova har tekshiruvda chaqiradi.
/// </summary>
public static class RemoteCommands
{
    public const string IgnoredKey = "templates.ignored";

    public static void Run(AppServices svc)
    {
        if (!svc.IsOnline) return;
        IReadOnlyList<(long Id, string Kind, string Arg)> cmds;
        try { cmds = svc.Store.TakeCommands(Environment.MachineName); } catch { return; }
        foreach (var (id, kind, arg) in cmds)
        {
            try
            {
                switch (kind)
                {
                    case "delete_template": DeleteTemplate(svc, arg); break;
                    case "clear_upload_session":
                        svc.Secrets.Set(SecretNames.UploadSessionPrefix + arg.Trim(), null);
                        svc.Log.Info($"Botdan: #{arg} uchun upload sessiyasi tozalandi.");
                        break;
                    case "delete_job_files": LocalJobFiles.DeleteJobs(svc, arg); break;
                    default: svc.Log.Warn($"Noma'lum buyruq: {kind}"); break;
                }
            }
            catch (Exception e) { svc.Log.Warn($"Buyruq #{id} ({kind}) bajarilmadi: {e.Message}"); }
        }
    }

    private static void DeleteTemplate(AppServices svc, string name)
    {
        var s = svc.Settings.Load();
        var t = s.Templates.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (t is null) return;
        s.Templates.Remove(t);
        if (string.Equals(s.Defaults.Template, t.Name, StringComparison.OrdinalIgnoreCase)) s.Defaults.Template = "";
        try { TemplateStore.DeleteFiles(s.Paths.TemplatesDir, t); } catch (Exception e) { svc.Log.Warn("Shablon fayllarini o'chirib bo'lmadi: " + e.Message); }
        if (!t.Managed)
        {
            // Tayyor video shabloni: kichik rasm va arxivdagi video (yonidagi subtitr/muqova/meta bilan) kompyuterdan o'chadi.
            // Video hali YouTube'ga chiqarilmagan bo'lsa (arxivda kutmoqda) fayl qoladi - aks holda chiqarib bo'lmay qoladi.
            Ignore(svc, t.Path);
            TemplateStore.DeleteThumbnail(s.Paths.TemplatesDir, t);
            if (AwaitsUpload(svc, t.Path)) svc.Log.Info($"Shablon '{t.Name}' videosi hali YouTube'ga chiqarilmagan - arxivdagi fayl qoldirildi.");
            else LocalJobFiles.DeleteVideoSet(s.Paths, t.Path);
        }
        svc.Settings.Save(s); // katalog va shablonlar ro'yxati bazaga yangilanadi
        svc.Log.Info($"Botdan: shablon o'chirildi: '{t.Name}'.");
    }

    /// <summary>Shu arxiv fayli YouTube'ga chiqarilishini kutayotgan vazifaniki (arxivlangan, yuklanmoqda yoki tayyor).</summary>
    private static bool AwaitsUpload(AppServices svc, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = PathGuard.Full(path);
            return svc.Store.ListByStatus(Core.Domain.JobStatus.Archived, Core.Domain.JobStatus.Uploading, Core.Domain.JobStatus.Ready)
                .Any(j => !string.IsNullOrWhiteSpace(j.VideoArchivePath) && string.Equals(PathGuard.Full(j.VideoArchivePath), full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; } // bilmasak - o'chirmaymiz
    }

    public static HashSet<string> Ignored(AppServices svc)
    {
        try { return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(svc.LocalDb.GetSetting(IgnoredKey) ?? "[]") ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void Ignore(AppServices svc, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var set = Ignored(svc); set.Add(path);
        svc.LocalDb.SetSetting(IgnoredKey, JsonSerializer.Serialize(set));
    }
}
