using System.Text.Json;

namespace YtAuto.Infrastructure.Heartbeat;

/// <summary>
/// "Kerakli dasturlar" holati (OpenFilm, VoiceStudio, subtitr va ovoz modellari, Claude, YouTube, Gemini kaliti, jadval) online bazaga yoziladi
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
            var geminiKeys = svc.Gemini.KeyCount;
            // Jadval: bot'dagi sozlama (online baza) va shu kompyuterdagi Task Scheduler vazifasi mosmi.
            bool schedOk; string schedDetail;
            try
            {
                var t = new Scheduling.TaskSchedulerService().Query();
                var at = $"har kuni {s.Schedule.StartTime} ({s.Schedule.TimeZoneId})";
                (schedOk, schedDetail) = s.Schedule.Enabled
                    ? (t.Exists, t.Exists ? $"{at}, keyingi: {t.NextRunTime:yyyy-MM-dd HH:mm}" : $"{at} - Task Scheduler'da vazifa yo'q")
                    : (true, "o'chiq (faqat «Hozir chiqarish» ishlaydi)");
            }
            catch (Exception e) { (schedOk, schedDetail) = (false, e.Message); }
            var items = new[]
            {
                new { key = "openfilm", name = "OpenFilm", ok = File.Exists(s.OpenFilm.ExePath), detail = s.OpenFilm.ExePath ?? "" },
                new { key = "voicestudio", name = "VoiceStudio", ok = File.Exists(s.VoiceStudio.ExePath), detail = s.VoiceStudio.ExePath ?? "" },
                new { key = "whisper", name = $"Subtitr modeli ({s.VoiceStudio.WhisperModel})", ok = HasModel(s.VoiceStudio.HfCacheDir, s.VoiceStudio.WhisperModel), detail = s.VoiceStudio.HfCacheDir ?? "" },
                new { key = "tts", name = $"Ovoz modeli ({TtsModel})", ok = HasModel(voiceCache, TtsModel), detail = string.Join(", ", s.VoiceProfiles.Select(v => v.Name)) },
                new { key = "claude", name = "Claude Code", ok = File.Exists(s.Claude.ExecutablePath), detail = s.Claude.ExecutablePath ?? "" },
                new { key = "claude-login", name = "Claude Code'ga kirish", ok = auth.LoggedIn == true, detail = auth.Message },
                new { key = "youtube", name = "YouTube kanali", ok = svc.Uploader.IsConnected, detail = s.YouTube.ChannelTitle ?? "" },
                new { key = "gemini", name = "Gemini kaliti", ok = geminiKeys > 0, detail = geminiKeys > 0 ? $"{geminiKeys} ta kalit · {s.Gemini.Model}" : "kiritilmagan" },
                new { key = "schedule", name = "Windows jadvali", ok = schedOk, detail = schedDetail },
            };
            svc.Store.SetSetting("tools.status." + Environment.MachineName, JsonSerializer.Serialize(
                new { machine = Environment.MachineName, updatedAtUtc = DateTimeOffset.UtcNow, items },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }
        catch (Exception e) { svc.Log.Debug("Dasturlar holati yozilmadi: " + e.Message); }
    }
}
