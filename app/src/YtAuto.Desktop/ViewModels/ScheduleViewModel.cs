using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Desktop.Ui;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Desktop.ViewModels;

public sealed record RunRow(string Started, string Duration, string Trigger, string Outcome, string Job, string Message);

public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly TaskSchedulerService _ts = new();
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _startTime = "01:00";
    [ObservableProperty] private string _timeZone = "Asia/Tashkent";
    [ObservableProperty] private bool _wakeToRun = true;
    [ObservableProperty] private string _taskState = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _workerPath = "";
    public string WakeNote => TaskSchedulerService.WakeNote;
    public ObservableCollection<RunRow> Runs { get; } = new();

    public ScheduleViewModel(AppServices svc) => _svc = svc;

    /// <summary>
    /// Masofaviy buyruq vazifasi ("YtAuto Remote Trigger", har 2 daqiqada yengil --poll) yo'q bo'lsa qo'shiladi:
    /// jadval yoqilgan bo'lsa ikkala vazifa joriy worker yo'li bilan qayta ro'yxatga olinadi; jadval o'chiq, lekin online baza
    /// ulangan bo'lsa - faqat shu vazifa (Telegram botdagi "YouTube'ga yuklash" / "Hozir chiqarish" shu orqali seziladi).
    /// </summary>
    public static void EnsureRemoteTrigger(AppServices svc)
    {
        try
        {
            var s = svc.Settings.Load();
            var exe = Shell.FindWorkerExe();
            var ts = new TaskSchedulerService();
            if (exe is null || ts.PollTaskExists()) return;
            if (s.Schedule.Enabled) ts.Register(s.Schedule, exe);
            else if (svc.Store is Infrastructure.Data.PostgresJobStore) ts.RegisterPoll(exe);
            else return;
            svc.Log.Info($"Masofaviy buyruq (bot) vazifasi Task Scheduler'ga qo'shildi: har {TaskSchedulerService.PollIntervalMinutes} daqiqada so'rovlar tekshiriladi.");
        }
        catch (Exception e) { svc.Log.Warn("Masofaviy buyruq vazifasini qo'shib bo'lmadi: " + e.Message); }
    }

    public ScheduleViewModel Load()
    {
        var s = _svc.Settings.Load();
        Enabled = s.Schedule.Enabled; StartTime = s.Schedule.StartTime; TimeZone = s.Schedule.TimeZoneId; WakeToRun = s.Schedule.WakeToRun;
        WorkerPath = Shell.FindWorkerExe() ?? "(YtAuto.Worker.exe topilmadi)";
        RefreshState();
        return this;
    }

    [RelayCommand]
    private void RefreshState()
    {
        try
        {
            var t = _ts.Query();
            TaskState = !t.Exists ? "Task Scheduler'da vazifa yo'q."
                : $"Holat: {t.State}{(t.Enabled ? "" : " (o'chirilgan)")}\nKeyingi ishga tushish: {t.NextRunTime:yyyy-MM-dd HH:mm}\nOxirgi ishga tushish: {(t.LastRunTime is null ? "-" : t.LastRunTime.Value.ToString("yyyy-MM-dd HH:mm"))}\nOxirgi natija: {TaskSchedulerService.DescribeResult(t.LastResult)}\nBuyruq: {t.Command} --scheduled"
                  + $"\nMasofaviy 'Hozir chiqarish': {(_ts.PollTaskExists() ? $"yoqilgan (har {TaskSchedulerService.PollIntervalMinutes} daqiqada, faqat internet bo'lsa)" : "yo'q — jadvalni qayta yoqing")}";
        }
        catch (Exception e) { TaskState = "Task Scheduler holatini o'qib bo'lmadi: " + e.Message; }
        Runs.Clear();
        foreach (var r in _svc.Store.RecentRuns(20))
            Runs.Add(new RunRow(r.Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                r.Finished is null ? "…" : $"{(r.Finished.Value - r.Started).TotalMinutes:0.0} daq", r.Trigger ?? "", r.Outcome ?? "ishlamoqda/uzilgan",
                r.JobId?.ToString() ?? "-", r.Message ?? ""));
    }

    private bool SaveSettings(bool enabled)
    {
        if (!TimeOnly.TryParseExact(StartTime, "HH:mm", out _)) { Message = "Vaqt HH:mm formatida bo'lsin (masalan 01:00)."; return false; }
        var s = _svc.Settings.Load();
        s.Schedule.Enabled = enabled; s.Schedule.StartTime = StartTime; s.Schedule.TimeZoneId = TimeZone; s.Schedule.WakeToRun = WakeToRun;
        _svc.Settings.Save(s);
        return true;
    }

    [RelayCommand]
    private void Enable()
    {
        var exe = Shell.FindWorkerExe();
        if (exe is null) { Message = "YtAuto.Worker.exe topilmadi - distributiv to'liq emas."; return; }
        if (!SaveSettings(true)) return;
        try
        {
            _ts.Register(_svc.Settings.Load().Schedule, exe); // mavjud bo'lsa yangilanadi, dublikat yaratilmaydi
            Enabled = true;
            Message = $"Jadval yoqildi: har kuni {StartTime} ({TimeZone}). Navbatga qarab 1–3 ta video, orasida 15 daqiqa tanaffus. Masofaviy 'Hozir chiqarish' ham yoqildi.";
        }
        catch (Exception e) { Message = "Jadvalni yaratib bo'lmadi: " + e.Message; }
        RefreshState();
    }

    [RelayCommand]
    private void Disable()
    {
        SaveSettings(false);
        try { _ts.Unregister(); Enabled = false; Message = "Jadval o'chirildi (Task Scheduler vazifasi olib tashlandi)."; }
        catch (Exception e) { Message = "O'chirib bo'lmadi: " + e.Message; }
        RefreshState();
    }

    /// <summary>Worker'ni hozir alohida jarayon sifatida ishga tushiradi (UI unga bog'liq emas; UI yopilsa ham ishlaydi).</summary>
    [RelayCommand]
    private void RunNow()
    {
        var exe = Shell.FindWorkerExe();
        if (exe is null) { Message = "YtAuto.Worker.exe topilmadi."; return; }
        try
        {
            var t = _ts.Query();
            if (t.Exists) { _ts.RunNow(); Message = "Worker Task Scheduler orqali ishga tushirildi (jadvaldagi bilan bir xil kontekst)."; }
            else
            {
                Process.Start(new ProcessStartInfo(exe, "--manual") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) });
                Message = "Worker ishga tushirildi (qo'lda).";
            }
        }
        catch (Exception e) { Message = "Ishga tushmadi: " + e.Message; }
    }
}
