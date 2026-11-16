using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Pipeline;
using YtAuto.Desktop.Ui;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Accounts;
using YtAuto.Infrastructure.Setup;

namespace YtAuto.Desktop.ViewModels;

public sealed partial class SetupStepVm : ObservableObject
{
    public string Key { get; }
    public string Name { get; }
    [ObservableProperty] private StepState _state = StepState.Pending;
    [ObservableProperty] private string _detail = "";
    public SetupStepVm(string key, string name) { Key = key; Name = name; }

    public string Icon => State switch
    {
        StepState.Running => "⏳", StepState.Done => "✓", StepState.Skipped => "✓", StepState.Failed => "✗", _ => "•",
    };
    partial void OnStateChanged(StepState value) => OnPropertyChanged(nameof(Icon));
}

/// <summary>
/// Birinchi ochilish: bitta tugma - "Kerakli fayllarni yuklash va sozlash". Dasturlar/modellar (bori qayta yuklanmaydi),
/// online hisob (token), sozlamalar serverga va Task Scheduler. Oxirida bosh sahifa token bilan ochiladi.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Action<AppServices> _done;

    public ObservableCollection<SetupStepVm> Steps { get; } = new();
    [ObservableProperty] private string _deviceName = Environment.MachineName;
    [ObservableProperty] private string _botUrl = DeviceAccount.DefaultBotUrl;
    [ObservableProperty] private bool _useExistingToken;
    [ObservableProperty] private string _existingToken = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate;

    /// <summary>Bot manzili build'ga kiritilmagan bo'lsa so'raladi.</summary>
    public bool AskBotUrl { get; } = string.IsNullOrEmpty(DeviceAccount.DefaultBotUrl);
    /// <summary>Eski (bitta foydalanuvchili) o'rnatma: bazaning egasi ulanish satri bor - eski ma'lumotlar shu hisobga o'tadi.</summary>
    public bool HasLegacy { get; }

    public SetupViewModel(AppServices svc, Action<AppServices> done)
    {
        _svc = svc; _done = done;
        HasLegacy = !string.IsNullOrWhiteSpace(svc.Secrets.Get(SecretNames.PostgresConnection));
        foreach (var (k, n) in MachineSetup.ToolSteps) Steps.Add(new SetupStepVm(k, n));
        Steps.Add(new SetupStepVm("account", HasLegacy ? "Online hisob (eski ma'lumotlar shu hisobga o'tadi)" : "Online hisob va token"));
        Steps.Add(new SetupStepVm("publish", "Sozlamalar va katalog serverga"));
        Steps.Add(new SetupStepVm("schedule", "Ish jadvali va Task Scheduler"));
    }

    private void Apply(SetupEvent e)
    {
        var step = Steps.FirstOrDefault(s => s.Key == e.Key);
        if (step is not null) { step.State = e.State; step.Detail = e.Detail; }
        ProgressText = e.State == StepState.Running ? $"{step?.Name}: {e.Detail}" : "";
        Indeterminate = e.State == StepState.Running && e.Fraction < 0;
        Progress = e.Fraction >= 0 ? e.Fraction * 100 : 0;
    }

    [RelayCommand]
    private async Task Run()
    {
        if (Running) return;
        Message = ""; IsError = false;
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Trim().Length > 80) { Fail("Qurilma nomini kiriting (1–80 belgi)."); return; }
        var useToken = UseExistingToken && !string.IsNullOrWhiteSpace(ExistingToken);
        if (UseExistingToken && !DeviceAccount.LooksLikeToken(ExistingToken)) { Fail("Token noto'g'ri ko'rinishda (yt_ bilan boshlanadi)."); return; }
        var needBot = !HasLegacy || useToken;
        if (needBot || !string.IsNullOrWhiteSpace(BotUrl))
        {
            try { BotUrl = DeviceAccount.NormalizeBotUrl(BotUrl); }
            catch (Exception e) { Fail(e.Message + " (bot manzilini kiriting)"); return; }
        }

        Running = true;
        foreach (var s in Steps.Where(s => s.State is StepState.Failed or StepState.Pending)) { s.State = StepState.Pending; s.Detail = ""; }
        var progress = new Progress<SetupEvent>(Apply);
        try
        {
            // 1) Dasturlar va modellar (bori o'tkazib yuboriladi).
            var failedTools = await Task.Run(() => MachineSetup.InstallToolsAsync(_svc, progress, CancellationToken.None));

            // 2) Online hisob.
            Apply(new("account", StepState.Running, "Hisob yaratilmoqda…"));
            AccountState acc;
            try
            {
                var device = DeviceName.Trim(); var bot = BotUrl; var token = useToken ? ExistingToken.Trim() : null;
                acc = await Task.Run(async () =>
                {
                    var master = _svc.Secrets.Get(SecretNames.PostgresConnection);
                    if (!string.IsNullOrWhiteSpace(master) && token is null)
                        return await DeviceAccount.ClaimLegacyAsync(master, device, bot, _svc.Secrets, _svc.LocalDb, CancellationToken.None);
                    return await DeviceAccount.RegisterAsync(_svc.Http, bot, device, token, _svc.Secrets, _svc.LocalDb, CancellationToken.None);
                });
            }
            catch (Exception e)
            {
                Apply(new("account", StepState.Failed, e.Message));
                Fail("Hisob yaratilmadi: " + e.Message);
                return;
            }
            Apply(new("account", StepState.Done, $"Hisob: {acc.AccountId}"));

            // 3) Online servislar (shu hisob roli bilan), sozlamalar serverga, jadval va Task Scheduler.
            AppServices online;
            try
            {
                online = await Task.Run(() => new AppServices("desktop"));
                var exe = Shell.FindWorkerExe();
                await Task.Run(() => MachineSetup.FinishOnline(online, exe, progress));
            }
            catch (Exception e)
            {
                foreach (var s in Steps.Where(s => s.Key is "publish" or "schedule" && s.State != StepState.Done)) { s.State = StepState.Failed; s.Detail = e.Message; }
                Fail("Sozlash oxiriga yetmadi: " + e.Message + " — «Qayta urinish» ni bosing.");
                return;
            }

            Message = failedTools == 0
                ? "Tayyor! Endi tokenni nusxalab, Telegram botga kiriting."
                : $"Hisob tayyor, lekin {failedTools} ta dastur o'rnatilmadi — bosh sahifada «Yetishmaganlarini o'rnatish» bilan qayta urinib ko'ring.";
            _done(online);
        }
        catch (Exception e) { Fail("Kutilmagan xato: " + e.Message); }
        finally { Running = false; Indeterminate = false; ProgressText = ""; }
    }

    private void Fail(string msg) { Message = msg; IsError = true; }
}
