using System.Text.RegularExpressions;
using YtAuto.Core.Domain;
using YtAuto.Core.Media;

namespace YtAuto.Core.Pipeline;

/// <summary>
/// «Mening ovozim» namunalari keshi (kompyuterda): DataRoot/voice-clone/&lt;profil&gt;-&lt;version&gt;.wav. Yonidagi ".applied" fayli -
/// shu versiya bu kompyuterdagi VoiceStudio'ga qo'shilgani belgisi. Namuna almashsa eski versiyalar o'chiriladi.
/// </summary>
public static class VoiceCloneCache
{
    public static string Dir(IAppPaths paths) => Path.Combine(paths.DataRoot, "voice-clone");
    public static string Safe(string profileName) => Regex.Replace(profileName, @"[^A-Za-z0-9_-]+", "_");
    public static string WavPath(IAppPaths paths, VoiceCloneSettings c) => Path.Combine(Dir(paths), $"{Safe(c.ProfileName)}-{Regex.Replace(c.Version, "[^A-Za-z0-9]", "")}.wav");
    public static string AppliedMarker(string wav) => wav + ".applied";

    /// <summary>Shu profilning barcha keshlangan fayllari (keep'dan tashqari).</summary>
    public static IEnumerable<string> Files(IAppPaths paths, string profileName, string? keepWav = null)
    {
        var dir = Dir(paths);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, Safe(profileName) + "-*")
            .Where(f => keepWav is null || (!string.Equals(f, keepWav, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, AppliedMarker(keepWav), StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed partial class JobRunner
{
    /// <summary>
    /// Vazifa egasining ovoz namunasini (bot Storage) yuklab WAV'ga o'tkazadi va VoiceStudio'da klon profilini tayyorlaydi.
    /// Xato bo'lsa ish to'xtaydi (foydalanuvchi o'z ovozini tanlagan - boshqa ovozda jimgina chiqarilmaydi), holat botga yoziladi.
    /// </summary>
    private async Task PrepareVoiceCloneAsync(JobContext c, ITtsSession session)
    {
        var clone = c.S.VoiceClone;
        var wav = VoiceCloneCache.WavPath(_x.Paths, clone);
        try
        {
            if (!File.Exists(wav))
            {
                if (_x.AudioImporter is null) throw PipelineException.Config("voice_clone_importer", "Ovoz namunasini yuklab olish bu muhitda sozlanmagan.");
                Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
                var tmp = wav + ".tmp.wav";
                await _x.AudioImporter.ImportAsync(clone.Url, tmp, c.Log, c.Ct).ConfigureAwait(false);
                File.Move(tmp, wav, true);
                foreach (var old in VoiceCloneCache.Files(_x.Paths, clone.ProfileName, wav)) { try { File.Delete(old); } catch { } }
                c.Log.Info($"Ovoz namunasi yuklab olindi ({clone.ProfileName}, {Wav.ReadInfo(wav).Duration:0.0}s).", "Voice");
            }
            await session.EnsureCloneProfileAsync(clone.ProfileName, wav, clone.RefText, clone.Version, c.Ct).ConfigureAwait(false);
            c.Snapshot.IntegrationVersions["voice_clone"] = clone.Version;
            Report(clone.Version, "ready", $"Namuna VoiceStudio'ga qo'shildi — videolar shu ovozda o'qiladi.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Report(clone.Version, "error", e.Message);
            throw;
        }
    }

    private void Report(string version, string status, string message)
    {
        try { _x.OnVoiceCloneStatus?.Invoke(version, status, message); } catch { /* holat yozilmasa ham ish davom etadi */ }
    }
}
