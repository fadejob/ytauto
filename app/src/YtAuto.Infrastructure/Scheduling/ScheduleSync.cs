using System.Text.Json;

namespace YtAuto.Infrastructure.Scheduling;

/// <summary>
/// Jadval sozlamasi botda (online bazada, <c>shared_settings.schedule</c>) boshqariladi. Bu yerda u shu kompyuterdagi
/// Windows Task Scheduler bilan moslashtiriladi: o'zgargan bo'lsa vazifa qayta yoziladi, o'chirilgan bo'lsa kunlik vazifa olib tashlanadi
/// (doimiy agent vazifasi qoladi). Ilova ochilganda va har worker ishga tushishida chaqiriladi. Agent ishlamayotgan bo'lsa ishga tushiriladi.
/// </summary>
public static class ScheduleSync
{
    private const string AppliedKey = "schedule.applied";

    public static void Apply(AppServices svc, string? workerExe)
    {
        try
        {
            if (!svc.IsOnline || workerExe is null || !File.Exists(workerExe)) return;
            var s = svc.Settings.Load().Schedule;
            var ts = new TaskSchedulerService();
            var signature = JsonSerializer.Serialize(new { s.Enabled, s.StartTime, s.TimeZoneId, s.WakeToRun, s.ExecutionLimitHours, exe = workerExe, agent = TaskSchedulerService.AgentTaskVersion });
            var applied = svc.LocalDb.GetSetting(AppliedKey);
            var dailyExists = ts.Query().Exists;
            if (s.Enabled)
            {
                if (applied != signature || !dailyExists || !ts.PollTaskExists())
                {
                    ts.Register(s, workerExe);
                    svc.Log.Info($"Jadval online bazadan yangilandi: har kuni {s.StartTime} ({s.TimeZoneId}); doimiy agent vazifasi yozildi.");
                }
            }
            else
            {
                if (dailyExists) { ts.UnregisterDaily(); svc.Log.Info("Jadval online bazada o'chirilgan: kunlik vazifa olib tashlandi."); }
                if (applied != signature || !ts.PollTaskExists()) ts.RegisterPoll(workerExe);
            }
            svc.LocalDb.SetSetting(AppliedKey, signature);
            EnsureAgent(svc, ts);
        }
        catch (Exception e) { svc.Log.Warn("Jadvalni sinxronlab bo'lmadi: " + e.Message); }
    }

    /// <summary>Agent (doimiy server) ishlamayotgan bo'lsa Task Scheduler orqali ishga tushiradi (o'z sessiyasida, oynasiz).</summary>
    public static void EnsureAgent(AppServices svc, TaskSchedulerService? ts = null)
    {
        try
        {
            if (Agent.AgentHost.IsRunning() || !svc.IsAccountReady) return;
            (ts ?? new TaskSchedulerService()).StartAgent();
            svc.Log.Info("Doimiy agent ishga tushirildi (Cloudflare tunnel, real vaqt).");
        }
        catch (Exception e) { svc.Log.Debug("Agentni ishga tushirib bo'lmadi: " + e.Message); }
    }
}
