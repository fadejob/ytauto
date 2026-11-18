using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// Foydalanuvchi botda «Mening ovozim» namunasini o'chirganda (voice.clone = {deleted:true}) shu kompyuterdagi nusxalar ham o'chadi:
/// keshdagi WAV va (bu kompyuterda qo'shilgan bo'lsa) VoiceStudio'dagi klon profil. Joriy hisob doirasida, yengil poll'da chaqiriladi;
/// VoiceStudio faqat o'chiriladigan profil bo'lsagina ochiladi.
/// </summary>
public static class VoiceCloneCleanup
{
    public static async Task RunAsync(AppServices svc, CancellationToken ct)
    {
        if (!svc.IsOnline) return;
        var c = SqliteSettingsStore.ReadVoiceClone(svc.Store.GetSetting(SqliteSettingsStore.VoiceCloneKey));
        if (!c.Deleted || c.ProfileName.Length == 0) return;
        var files = VoiceCloneCache.Files(svc.Paths, c.ProfileName).ToList();
        if (files.Count == 0) return;
        if (files.Any(f => f.EndsWith(".applied", StringComparison.OrdinalIgnoreCase)))
        {
            await using var session = await new VoiceStudioTtsService(svc.Store, svc.RunId).OpenAsync(svc.Settings.Load().VoiceStudio, svc.Log, ct).ConfigureAwait(false);
            await session.DeleteCloneProfileAsync(c.ProfileName, ct).ConfigureAwait(false);
        }
        foreach (var f in files) { try { File.Delete(f); } catch { } }
        svc.Log.Info($"«Mening ovozim» namunasi bu kompyuterdan o'chirildi ({c.ProfileName}).");
    }
}
