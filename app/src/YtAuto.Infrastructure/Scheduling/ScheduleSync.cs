using System.Text.Json;

namespace YtAuto.Infrastructure.Scheduling;

/// <summary>
/// Jadval sozlamasi botda (online bazada, <c>shared_settings.schedule</c>) boshqariladi. Bu yerda u shu kompyuterdagi
/// Windows Task Scheduler bilan moslashtiriladi: o'zgargan bo'lsa vazifa qayta yoziladi, o'chirilgan bo'lsa kunlik vazifa olib tashlanadi
/// (masofaviy "Hozir chiqarish" vazifasi qoladi). Ilova ochilganda va har --poll da chaqiriladi.
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
            var signature = JsonSerializer.Serialize(new { s.Enabled, s.StartTime, s.TimeZoneId, s.WakeToRun, s.ExecutionLimitHours, exe = workerExe, poll = TaskSchedulerService.PollIntervalMinutes });
            var applied = svc.LocalDb.GetSetting(AppliedKey);
            var dailyExists = ts.Query().Exists;
            if (s.Enabled)
            {
                if (applied == signature && dailyExists && ts.PollTaskExists()) return;
                ts.Register(s, workerExe);
                svc.Log.Info($"Jadval online bazadan yangilandi: har kuni {s.StartTime} ({s.TimeZoneId}).");
            }
            else
            {
                if (dailyExists) { ts.UnregisterDaily(); svc.Log.Info("Jadval online bazada o'chirilgan: kunlik vazifa olib tashlandi."); }
                if (applied != signature || !ts.PollTaskExists()) ts.RegisterPoll(workerExe);
            }
            svc.LocalDb.SetSetting(AppliedKey, signature);
        }
        catch (Exception e) { svc.Log.Warn("Jadvalni sinxronlab bo'lmadi: " + e.Message); }
    }
}
