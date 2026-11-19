using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Media;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// Botda yuklangan «Mening ovozim» namunasini kompyuter ishni kutmasdan oladi: yengil poll (har 2 daqiqada, ilova yopiq bo'lsa ham)
/// namunani yuklab, VoiceStudio'da klon profilini tayyorlaydi va natijani botga yozadi. Joriy hisob doirasida.
/// Har versiya bir marta qo'shiladi (".applied" belgisi); VoiceStudio faqat yangi namuna bo'lsagina ochiladi.
/// Xato bo'lsa har 2 daqiqada VoiceStudio qayta ochilmasligi uchun shu versiya 30 daqiqadan keyin qayta uriniladi.
/// </summary>
public static class VoiceClonePrefetch
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    public static async Task RunAsync(AppServices svc, CancellationToken ct)
    {
        if (!svc.IsOnline) return;
        var c = SqliteSettingsStore.ReadVoiceClone(svc.Store.GetSetting(SqliteSettingsStore.VoiceCloneKey));
        if (!c.Active) return;
        var wav = VoiceCloneCache.WavPath(svc.Paths, c);
        var marker = VoiceCloneCache.AppliedMarker(wav);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == c.Version) return; // shu namuna allaqachon qo'shilgan
        var failed = wav + ".failed";
        if (File.Exists(failed) && DateTime.UtcNow - File.GetLastWriteTimeUtc(failed) < RetryAfter) return;

        void Status(string status, string message)
        {
            try { SqliteSettingsStore.WriteVoiceCloneStatus(svc.Store, c.Version, status, message); } catch { /* holat yozilmasa ham davom */ }
        }

        try
        {
            if (!File.Exists(wav))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                var tmp = wav + ".tmp.wav";
                await new RemoteAudioImporter(svc.Http, svc.Media(svc.FilmTool())).ImportAsync(c.Url, tmp, svc.Log, ct).ConfigureAwait(false);
                File.Move(tmp, wav, true);
                foreach (var old in VoiceCloneCache.Files(svc.Paths, c.ProfileName, wav)) { try { File.Delete(old); } catch { } }
                svc.Log.Info($"Ovoz namunasi yuklab olindi ({c.ProfileName}).");
            }
            await using (var session = await new VoiceStudioTtsService(svc.Store, svc.RunId).OpenAsync(svc.Settings.Load().VoiceStudio, svc.Log, ct).ConfigureAwait(false))
                await session.EnsureCloneProfileAsync(c.ProfileName, wav, c.RefText, c.Version, ct).ConfigureAwait(false);
            try { File.Delete(failed); } catch { }
            Status("ready", "Namuna VoiceStudio'ga qo'shildi — videolar shu ovozda o'qiladi.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            try { File.WriteAllText(failed, e.Message); } catch { }
            svc.Log.Warn("Ovoz namunasi qo'shilmadi: " + e.Message);
            Status("error", e.Message);
        }
    }
}
