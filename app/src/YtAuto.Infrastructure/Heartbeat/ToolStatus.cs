using System.Text.Json;

namespace YtAuto.Infrastructure.Heartbeat;

/// <summary>
/// "Kerakli dasturlar" holati (OpenFilm, VoiceStudio, subtitr va ovoz modellari, Claude, YouTube) online bazaga yoziladi
/// (shared_settings: tools.status.KOMPYUTER): bot har kompyuter uchun nima o'rnatilgan/yo'qligini ko'rsatadi.
/// </summary>
public static class ToolStatus
{
    public const string TtsModel = "KittenML/kitten-tts-mini-0.8";

    public static void Publish(AppServices svc)
    {
        if (!svc.IsOnline) return;
        try
        {
            var s = svc.Settings.Load();
            static bool HasModel(string? dir, string model) =>
                !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, "models--" + model.Replace("/", "--")));
            var voiceCache = string.IsNullOrWhiteSpace(s.VoiceStudio.HfCacheDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniVoice", "hf_cache")
                : s.VoiceStudio.HfCacheDir;
            var auth = svc.Claude().AuthStatus();
            var items = new[]
            {
                new { key = "openfilm", name = "OpenFilm", ok = File.Exists(s.OpenFilm.ExePath), detail = s.OpenFilm.ExePath ?? "" },
                new { key = "voicestudio", name = "VoiceStudio", ok = File.Exists(s.VoiceStudio.ExePath), detail = s.VoiceStudio.ExePath ?? "" },
                new { key = "whisper", name = $"Subtitr modeli ({s.VoiceStudio.WhisperModel})", ok = HasModel(s.VoiceStudio.HfCacheDir, s.VoiceStudio.WhisperModel), detail = s.VoiceStudio.HfCacheDir ?? "" },
                new { key = "tts", name = $"Ovoz modeli ({TtsModel})", ok = HasModel(voiceCache, TtsModel), detail = string.Join(", ", s.VoiceProfiles.Select(v => v.Name)) },
                new { key = "claude", name = "Claude Code", ok = File.Exists(s.Claude.ExecutablePath), detail = s.Claude.ExecutablePath ?? "" },
                new { key = "claude-login", name = "Claude Code'ga kirish", ok = auth.LoggedIn == true, detail = auth.Message },
                new { key = "youtube", name = "YouTube kanali", ok = svc.Uploader.IsConnected, detail = s.YouTube.ChannelTitle ?? "" },
            };
            svc.Store.SetSetting("tools.status." + Environment.MachineName, JsonSerializer.Serialize(
                new { machine = Environment.MachineName, updatedAtUtc = DateTimeOffset.UtcNow, items },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch (Exception e) { svc.Log.Debug("Dasturlar holati yozilmadi: " + e.Message); }
    }
}
