using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Infrastructure.Setup;

public enum StepState { Pending, Running, Done, Skipped, Failed }

/// <summary>Sozlash qadami holati (UI ro'yxatida ko'rsatiladi). Fraction: 0..1, noma'lum bo'lsa -1.</summary>
public sealed record SetupEvent(string Key, StepState State, string Detail, double Fraction = -1);

/// <summary>Kompyuterdagi kerakli dastur/model holati.</summary>
public sealed record ToolItem(string Key, string Name, bool Ok, string Detail);

/// <summary>
/// Bitta tugma bilan kompyuterni to'liq tayyorlash ("Kerakli fayllarni yuklash"):
/// <list type="number">
/// <item>Kerakli dasturlar va modellar: OpenFilm, VoiceStudio, subtitr modeli (faster-whisper), ovoz modeli (KittenTTS) va
/// ayol/erkak ovoz profillari, Claude Code. Kompyuterda bori qayta yuklanmaydi.</item>
/// <item>Online hisob (DeviceAccount) - ilovada.</item>
/// <item>Sozlamalar va katalog serverga, ish jadvali va Task Scheduler (kunlik ish + har 10 daqiqada yengil tekshiruv).</item>
/// </list>
/// Qadamlar bir-biriga bog'liq emas: biri o'xshamasa ham qolganlari bajariladi, holat ro'yxatda ko'rinadi.
/// </summary>
public static class MachineSetup
{
    public const string TtsModel = "KittenML/kitten-tts-mini-0.8";
    private const string FemaleName = "Kitten EN ayol", FemaleVoice = "expr-voice-2-f";
    private const string MaleName = "Kitten EN erkak", MaleVoice = "expr-voice-2-m";

    public static readonly (string Key, string Name)[] ToolSteps =
    {
        ("openfilm", "OpenFilm (montaj va render)"),
        ("voicestudio", "VoiceStudio (ovoz)"),
        ("whisper", "Subtitr modeli (faster-whisper)"),
        ("tts", "Ovoz modeli va ovozlar (KittenTTS)"),
        ("claude", "Claude Code (montaj agenti)"),
    };

    public static string VoiceCacheDir(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.VoiceStudio.HfCacheDir) && Path.IsPathFullyQualified(s.VoiceStudio.HfCacheDir)
            ? s.VoiceStudio.HfCacheDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniVoice", "hf_cache");

    private static bool HasModel(string? dir, string model) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, "models--" + model.Replace("/", "--")));

    private static bool HasProfile(AppSettings s, string name) => s.VoiceProfiles.Any(v => v.Name == name && v.Model == "kittentts");

    /// <summary>Hozirgi holat (yuklamasdan).</summary>
    public static List<ToolItem> Status(AppServices svc)
    {
        var s = svc.Settings.Load();
        var claude = File.Exists(s.Claude.ExecutablePath);
        return new List<ToolItem>
        {
            new("openfilm", "OpenFilm", File.Exists(s.OpenFilm.ExePath), s.OpenFilm.ExePath ?? ""),
            new("voicestudio", "VoiceStudio", File.Exists(s.VoiceStudio.ExePath), s.VoiceStudio.ExePath ?? ""),
            new("whisper", $"Subtitr modeli ({s.VoiceStudio.WhisperModel})", HasModel(s.VoiceStudio.HfCacheDir, s.VoiceStudio.WhisperModel), s.VoiceStudio.HfCacheDir ?? ""),
            new("tts", $"Ovoz modeli ({TtsModel})", HasModel(VoiceCacheDir(s), TtsModel) && HasProfile(s, FemaleName) && HasProfile(s, MaleName),
                string.Join(", ", s.VoiceProfiles.Select(v => v.Name))),
            new("claude", "Claude Code", claude, claude ? s.Claude.ExecutablePath : ""),
            new("claude-login", "Claude Code'ga kirish", claude && AppInstaller.ClaudeLoggedIn(),
                AppInstaller.ClaudeLoggedIn() ? "kirilgan" : "«Claude'ga kirish» tugmasi bilan bir marta kiring"),
        };
    }

    /// <summary>Yetishmagan dasturlar va modellarni o'rnatadi. Qaytaradi: muvaffaqiyatsiz qadamlar soni.</summary>
    public static async Task<int> InstallToolsAsync(AppServices svc, IProgress<SetupEvent> p, CancellationToken ct)
    {
        int failed = 0;
        foreach (var (key, name) in ToolSteps)
        {
            ct.ThrowIfCancellationRequested();
            p.Report(new(key, StepState.Running, "Tekshirilmoqda…"));
            try
            {
                var detail = await RunToolStepAsync(svc, key, new Progress<InstallProgress>(ip => p.Report(new(key, StepState.Running, ip.Text, ip.Fraction))), ct);
                p.Report(new(key, detail.StartsWith("✓ bor") ? StepState.Skipped : StepState.Done, detail, 1));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                svc.Log.Warn($"Sozlash: {name} - {e.Message}");
                p.Report(new(key, StepState.Failed, e.Message));
            }
        }
        ToolLocator.Reset();
        return failed;
    }

    private static async Task<string> RunToolStepAsync(AppServices svc, string key, IProgress<InstallProgress> ip, CancellationToken ct)
    {
        var s = svc.Settings.Load(); // yo'llar avtomatik topiladi (ToolLocator)
        switch (key)
        {
            case "openfilm":
                if (File.Exists(s.OpenFilm.ExePath)) return "✓ bor: " + s.OpenFilm.ExePath;
                return "O'rnatildi: " + await AppInstaller.InstallOpenFilmAsync(ip, ct);
            case "voicestudio":
                if (File.Exists(s.VoiceStudio.ExePath)) return "✓ bor: " + s.VoiceStudio.ExePath;
                return "O'rnatildi: " + await AppInstaller.InstallVoiceStudioAsync(ip, ct);
            case "whisper":
            {
                if (HasModel(s.VoiceStudio.HfCacheDir, s.VoiceStudio.WhisperModel)) return "✓ bor: " + s.VoiceStudio.HfCacheDir;
                var dir = !string.IsNullOrWhiteSpace(s.VoiceStudio.HfCacheDir) && Path.IsPathFullyQualified(s.VoiceStudio.HfCacheDir)
                    ? s.VoiceStudio.HfCacheDir
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "hf_cache");
                await AppInstaller.DownloadHfModelAsync(s.VoiceStudio.WhisperModel, dir, ip, ct);
                var cur = svc.Settings.Load(); cur.VoiceStudio.HfCacheDir = dir; svc.Settings.Save(cur);
                return "Yuklandi: " + dir;
            }
            case "tts":
            {
                var had = HasModel(VoiceCacheDir(s), TtsModel);
                if (!had) await AppInstaller.DownloadHfModelAsync(TtsModel, VoiceCacheDir(s), ip, ct);
                var cur = svc.Settings.Load();
                var added = false;
                if (!HasProfile(cur, FemaleName)) { cur.VoiceProfiles.Add(new VoiceProfile { Name = FemaleName, Model = "kittentts", Voice = FemaleVoice, Language = "en", Speed = 1.0 }); added = true; }
                if (!HasProfile(cur, MaleName)) { cur.VoiceProfiles.Add(new VoiceProfile { Name = MaleName, Model = "kittentts", Voice = MaleVoice, Language = "en", Speed = 1.0 }); added = true; }
                if (string.IsNullOrEmpty(cur.Defaults.VoiceProfile) || !cur.VoiceProfiles.Any(v => v.Name == cur.Defaults.VoiceProfile)) { cur.Defaults.VoiceProfile = FemaleName; added = true; }
                if (added) svc.Settings.Save(cur);
                return had && !added ? "✓ bor: " + string.Join(", ", cur.VoiceProfiles.Select(v => v.Name)) : "Tayyor: " + string.Join(", ", cur.VoiceProfiles.Select(v => v.Name));
            }
            case "claude":
                if (File.Exists(s.Claude.ExecutablePath)) return "✓ bor: " + s.Claude.ExecutablePath;
                var exe = await AppInstaller.InstallClaudeCodeAsync(ip, ct);
                var c2 = svc.Settings.Load(); c2.Claude.ExecutablePath = exe; svc.Settings.Save(c2);
                return "O'rnatildi: " + exe;
            default:
                throw new ArgumentOutOfRangeException(nameof(key));
        }
    }

    /// <summary>
    /// Hisob ulangandan keyin: lokal sozlamalar va katalog (ovoz profillari, shablonlar) serverga, ish jadvali (bo'lmasa standart:
    /// har kuni 01:00-04:00) serverga va Task Scheduler'ga (kunlik ish + har 10 daqiqada yengil --poll), kompyuter holati.
    /// </summary>
    public static void FinishOnline(AppServices online, string? workerExe, IProgress<SetupEvent> p)
    {
        if (!online.IsOnline) throw new InvalidOperationException("Online baza ulanmagan.");
        p.Report(new("publish", StepState.Running, "Sozlamalar serverga yuborilmoqda…"));
        var s = online.Settings.Load();
        online.Settings.Save(s); // katalog, shablonlar, ovoz ma'lumoti
        try
        {
            var cfg = JsonNode.Parse(online.Store.GetSetting(SqliteSettingsStore.ConfigKey) ?? "{}");
            var remoteProfiles = (cfg?["voice"]?["profiles"] as JsonArray)?.Count ?? 0;
            if (remoteProfiles == 0 && s.VoiceProfiles.Count > 0) online.Settings.PushVoiceProfiles(s.VoiceProfiles);
        }
        catch (Exception e) { online.Log.Warn("Ovoz profillari botga yozilmadi: " + e.Message); }
        Heartbeat.MachineBeat.Beat(online, appOpen: true);
        Heartbeat.ToolStatus.Publish(online);
        p.Report(new("publish", StepState.Done, "Ovoz profillari, shablonlar va kompyuter holati serverda."));

        p.Report(new("schedule", StepState.Running, "Ish jadvali va Task Scheduler…"));
        if (string.IsNullOrWhiteSpace(online.Store.GetSetting(SqliteSettingsStore.ScheduleKey)))
        {
            var sch = online.Settings.Load().Schedule;
            sch.Enabled = true;
            online.Settings.SaveSchedule(sch);
        }
        if (workerExe is null || !File.Exists(workerExe)) throw new InvalidOperationException("YtAuto.exe yo'li aniqlanmadi - Task Scheduler sozlanmadi.");
        ScheduleSync.Apply(online, workerExe);
        var info = new TaskSchedulerService().Query();
        var sched = online.Settings.Load().Schedule;
        p.Report(new("schedule", StepState.Done, sched.Enabled
            ? $"Har kuni {sched.StartTime}–{sched.EndTime} ({sched.TimeZoneId}); keyingi: {info.NextRunTime:yyyy-MM-dd HH:mm}. Har 10 daqiqada yengil tekshiruv."
            : "Kunlik ish o'chiq; har 10 daqiqada yengil tekshiruv (bot buyruqlari) yoqilgan."));
    }
}
