using YtAuto.Core.Domain;

namespace YtAuto.Infrastructure.Templates;

/// <summary>
/// Har bir tayyor bo'lgan (arxivlangan) video avtomatik shablon bo'lib qoladi: arxivdagi fayl NUSXALANMAYDI va Supabase'ga YUKLANMAYDI,
/// faqat ma'lumot (nom, hajm, kichik rasm) shablonlar ro'yxatiga qo'shiladi. Shu ro'yxat online bazaga yoziladi, bot ko'rsatadi.
/// Boshqa kompyuterda tayyorlangan videolar (fayli bu kompyuterda yo'q) o'tkazib yuboriladi.
/// </summary>
public static class JobTemplates
{
    public static async Task SyncAsync(AppServices svc, CancellationToken ct)
    {
        try
        {
            var s = svc.Settings.Load();
            var jobs = svc.Store.ListByStatus(JobStatus.Archived, JobStatus.Uploading, JobStatus.Processing, JobStatus.Scheduled, JobStatus.Published);
            var known = new HashSet<string>(s.Templates.Select(t => t.Path ?? ""), StringComparer.OrdinalIgnoreCase);
            known.UnionWith(YtAuto.Infrastructure.Commands.RemoteCommands.Ignored(svc)); // botdan o'chirilgan video shablonlari qayta qo'shilmaydi
            var added = 0;
            TemplateStore? store = null;
            foreach (var j in jobs.OrderBy(j => j.Id))
            {
                var p = j.VideoArchivePath;
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p) || known.Contains(p)) continue;
                store ??= new TemplateStore(svc.Media(svc.FilmTool()));
                var title = (j.DisplayTitle ?? "").Trim();
                var name = $"#{j.Id} {(title.Length > 45 ? title[..45] + "…" : title)}".Trim();
                var baseName = name; var k = 2;
                while (s.Templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} ({k++})";
                var def = await store.RegisterExistingVideoAsync(s.Paths.TemplatesDir, p, name, $"Video #{j.Id}", ct).ConfigureAwait(false);
                s.Templates.Add(def); known.Add(p); added++;
            }
            if (added > 0)
            {
                svc.Settings.Save(s); // katalog (bot ro'yxati) ham yangilanadi
                svc.Log.Info($"{added} ta tayyor video shablon sifatida saqlandi (nusxalanmadi, yuklanmadi).");
            }
        }
        catch (Exception e) { svc.Log.Warn("Tayyor videolarni shablonga qo'shib bo'lmadi: " + e.Message); }
    }
}
