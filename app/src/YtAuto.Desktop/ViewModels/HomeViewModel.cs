using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Domain;
using YtAuto.Desktop.Ui;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Accounts;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Scheduling;
using YtAuto.Infrastructure.Setup;

namespace YtAuto.Desktop.ViewModels;

/// <summary>
/// Sozlashdan keyingi bosh sahifa: token (nusxalash, yangilash), botni ochish, ish vaqti (jadval), Gemini holati
/// va kerakli dasturlar. Mavzular, promptlar, Gemini kalitlari va YouTube - Telegram botda.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Action _openAdvanced;
    private string _token = "";

    [ObservableProperty] private string _accountId = "";
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _tokenText = "";
    [ObservableProperty] private bool _tokenVisible;
    [ObservableProperty] private string _telegramInfo = "";
    [ObservableProperty] private string _botLabel = "";
    [ObservableProperty] private bool _geminiReady;
    [ObservableProperty] private string _geminiText = "";
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _startTime = "01:00";
    [ObservableProperty] private string _endTime = "04:00";
    [ObservableProperty] private string _timeZoneId = "Asia/Tashkent";
    [ObservableProperty] private string _scheduleInfo = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _toolsMissing;
    [ObservableProperty] private string _installText = "";
    public ObservableCollection<ToolItem> Tools { get; } = new();

    public HomeViewModel(AppServices svc, Action openAdvanced)
    {
        _svc = svc; _openAdvanced = openAdvanced;
        _ = RefreshAsync();
    }

    private static string Mask(string t) => t.Length > 10 ? t[..5] + new string('•', 18) + t[^4..] : t;

    partial void OnTokenVisibleChanged(bool value) => TokenText = value ? _token : Mask(_token);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var acc = DeviceAccount.Load(_svc.LocalDb);
            _token = DeviceAccount.Token(_svc.Secrets) ?? "";
            TokenText = TokenVisible ? _token : Mask(_token);
            AccountId = acc?.AccountId.ToString() ?? "";
            DeviceName = acc?.DeviceName ?? "";
            BotLabel = string.IsNullOrEmpty(acc?.BotUsername) ? (acc?.BotUrl ?? "") : "@" + acc!.BotUsername;

            var (info, gemini, sched, tools, task) = await Task.Run(() =>
            {
                var i = (_svc.Store as PostgresJobStore)?.AccountInfo();
                var s = _svc.Settings.Load().Schedule;
                return (i, _svc.HasGeminiKey(), s, MachineSetup.Status(_svc), new TaskSchedulerService().Query());
            });
            TelegramInfo = info is { TelegramUsers: > 0 } ? $"Botga kirgan Telegram foydalanuvchilar: {info.Value.TelegramUsers}" : "Botga hali hech kim bu token bilan kirmagan.";
            GeminiReady = gemini;
            GeminiText = gemini
                ? "Gemini kaliti kiritilgan — mavzular ishlanadi."
                : "Gemini kaliti yo'q: mavzular muzlatilgan, botda mavzu qo'shish va AI yordamchi o'chiq. Botda Sozlamalar → Gemini'ga kalit qo'shing.";
            ScheduleEnabled = sched.Enabled; StartTime = sched.StartTime; EndTime = sched.EndTime; TimeZoneId = sched.TimeZoneId;
            ScheduleInfo = task.Exists
                ? $"Task Scheduler: keyingi ishga tushish {task.NextRunTime:yyyy-MM-dd HH:mm}, oxirgi natija: {TaskSchedulerService.DescribeResult(task.LastResult)}."
                : sched.Enabled ? "Task Scheduler vazifasi topilmadi — «Saqlash» ni bosing." : "Kunlik ish o'chiq (har 10 daqiqadagi yengil tekshiruv ishlaydi).";
            Tools.Clear(); foreach (var t in tools) Tools.Add(t);
            ToolsMissing = tools.Any(t => !t.Ok && t.Key != "claude-login");
        }
        catch (Exception e) { Message = "Holatni o'qib bo'lmadi: " + e.Message; }
    }

    [RelayCommand]
    private void CopyToken()
    {
        if (string.IsNullOrEmpty(_token)) return;
        try { Clipboard.SetText(_token); Message = "Token nusxalandi. Telegram botni oching va birinchi kirishda shu tokenni kiriting."; }
        catch (Exception e) { Message = "Nusxalab bo'lmadi: " + e.Message; }
    }

    [RelayCommand]
    private async Task RotateToken()
    {
        if (MessageBox.Show("Token yangilansinmi?\n\nEski token bilan botga kirganlar chiqarib yuboriladi — botga yangi token bilan qayta kirish kerak bo'ladi. Kompyuter ishi to'xtamaydi.",
                "YtAuto", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Busy = true;
        try
        {
            var store = _svc.Store as PostgresJobStore ?? throw new InvalidOperationException("Online baza ulanmagan.");
            _token = await Task.Run(() => DeviceAccount.RotateToken(store, _svc.Secrets));
            TokenVisible = true; TokenText = _token;
            Message = "Yangi token yaratildi. Uni nusxalab, botga qayta kiriting.";
            await RefreshAsync();
        }
        catch (Exception e) { Message = "Token yangilanmadi: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void OpenBot()
    {
        var acc = DeviceAccount.Load(_svc.LocalDb);
        if (acc is null) return;
        Shell.OpenUrl(!string.IsNullOrEmpty(acc.BotUsername) ? $"https://t.me/{acc.BotUsername}" : acc.BotUrl);
    }

    [RelayCommand]
    private async Task SaveSchedule()
    {
        if (!TimeOnly.TryParseExact(StartTime.Trim(), "HH:mm", out _) || !TimeOnly.TryParseExact(EndTime.Trim(), "HH:mm", out _))
        { Message = "Vaqt HH:mm formatida bo'lsin (masalan 01:00)."; return; }
        try { Core.Scheduling.TimeZones.Find(TimeZoneId.Trim()); } catch { Message = "Vaqt zonasi noto'g'ri (masalan Asia/Tashkent)."; return; }
        Busy = true;
        try
        {
            var exe = Shell.FindWorkerExe();
            await Task.Run(() =>
            {
                var s = _svc.Settings.Load().Schedule;
                s.Enabled = ScheduleEnabled; s.StartTime = StartTime.Trim(); s.EndTime = EndTime.Trim(); s.TimeZoneId = TimeZoneId.Trim();
                _svc.Settings.SaveSchedule(s);
                ScheduleSync.Apply(_svc, exe);
            });
            Message = ScheduleEnabled
                ? $"Saqlandi: worker har kuni {StartTime}–{EndTime} oralig'ida video tayyorlaydi (botda ham shu jadval)."
                : "Saqlandi: kunlik ish o'chirildi (botdan «Hozir ishga tushirish» ishlaydi).";
            await RefreshAsync();
        }
        catch (Exception e) { Message = "Saqlanmadi: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task InstallMissing()
    {
        Busy = true; Message = "";
        try
        {
            var progress = new Progress<SetupEvent>(e => InstallText = e.State == StepState.Running ? e.Detail : "");
            var failed = await Task.Run(() => MachineSetup.InstallToolsAsync(_svc, progress, CancellationToken.None));
            await Task.Run(() => { _svc.Settings.Save(_svc.Settings.Load()); Infrastructure.Heartbeat.ToolStatus.Publish(_svc); });
            Message = failed == 0 ? "Barcha kerakli dasturlar tayyor." : $"{failed} ta qadam o'xshamadi — ro'yxatni ko'ring (logs\\desktop.log).";
            await RefreshAsync();
        }
        catch (Exception e) { Message = "O'rnatishda xato: " + e.Message; }
        finally { Busy = false; InstallText = ""; }
    }

    /// <summary>Claude Code'ga kirish: konsolda claude ochiladi, foydalanuvchi brauzer orqali tizimga kiradi.</summary>
    [RelayCommand]
    private void ClaudeLogin()
    {
        var exe = _svc.Settings.Load().Claude.ExecutablePath;
        if (!File.Exists(exe)) { Message = "Avval Claude Code o'rnatilsin («Yetishmaganlarini o'rnatish»)."; return; }
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\"\"") { UseShellExecute = true });
        Message = "Ochilgan oynada Claude Code'ga kiring (brauzer ochiladi), so'ng oynani yopib «Yangilash» ni bosing.";
    }

    [RelayCommand] private void OpenAdvanced() => _openAdvanced();
}
