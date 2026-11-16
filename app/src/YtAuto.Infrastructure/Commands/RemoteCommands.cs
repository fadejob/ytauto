using System.Text.Json;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Templates;

namespace YtAuto.Infrastructure.Commands;

/// <summary>
/// Botdan kelgan, aynan shu kompyuterni o'zgartiradigan buyruqlar (ytauto.machine_commands): shablonni o'chirish va
/// upload sessiyasini tozalash. Yengil worker (--poll) va ochiq ilova har tekshiruvda chaqiradi.
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
            // Tayyor video shabloni: arxivdagi video O'CHIRILMAYDI, faqat kichik rasm papkasi. Qayta avtomatik qo'shilmasligi uchun yo'l eslab qolinadi.
            Ignore(svc, t.Path);
            try
            {
                if (!string.IsNullOrWhiteSpace(t.Thumbnail) && PathGuard.IsUnder(s.Paths.TemplatesDir, t.Thumbnail))
                    PathGuard.SafeDeleteDirectory(s.Paths.TemplatesDir, Path.GetDirectoryName(t.Thumbnail)!);
            }
            catch { }
        }
        svc.Settings.Save(s); // katalog va shablonlar ro'yxati bazaga yangilanadi
        svc.Log.Info($"Botdan: shablon o'chirildi: '{t.Name}'.");
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
