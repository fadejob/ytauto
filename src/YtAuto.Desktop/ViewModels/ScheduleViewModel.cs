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
                : $"Holat: {t.State}{(t.Enabled ? "" : " (o'chirilgan)")}\nKeyingi ishga tushish: {t.NextRunTime:yyyy-MM-dd HH:mm}\nOxirgi ishga tushish: {(t.LastRunTime is null ? "-" : t.LastRunTime.Value.ToString("yyyy-MM-dd HH:mm"))}\nOxirgi natija: {TaskSchedulerService.DescribeResult(t.LastResult)}\nBuyruq: {t.Command} --scheduled";
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
            Message = $"Jadval yoqildi: har kuni {StartTime} ({TimeZone}).";
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
