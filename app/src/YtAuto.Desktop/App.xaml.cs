using System.Windows;
using System.Windows.Threading;
using YtAuto.Desktop.ViewModels;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Birinchi ochilish (yoki yangi versiya): doimiy papkani tanlab o'rnatish. O'rnatilgan nusxa ishga tushsa, bu jarayon yopiladi.
        var mode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // o'rnatish oynasi yopilganda ilova o'zi yopilib qolmasligi uchun
        if (Ui.Installer.OfferIfNeeded()) { Shutdown(0); return; }
        ShutdownMode = mode;
        try { Services = new AppServices("desktop"); }
        catch (Exception ex)
        {
            MessageBox.Show("Ilova ma'lumotlar bazasini ochib bo'lmadi:\n" + ex.Message, "YtAuto", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        // Hisob sozlanmagan bo'lsa - bitta tugmali sozlash oynasi; tayyor bo'lgach online qism shu yerda ishga tushadi.
        // Desktop ochilishi video tayyorlashni BOSHLAMAYDI: faqat boshqaruv.
        if (Services.IsAccountReady) StartOnline();
        var main = new MainWindow { DataContext = new MainViewModel(Services, online => { Services = online; StartOnline(); }) };
        MainWindow = main;
        main.Show();
    }

    /// <summary>Online qism: Task Scheduler sinxroni, bot katalogi, kompyuter holati va har 2 daqiqalik heartbeat/buyruqlar.</summary>
    private void StartOnline()
    {
        if (_beat is not null) return;
        var svc = Services;
        var workerExe = Ui.Shell.FindWorkerExe();
        // Tarmoq va tashqi dasturlar (baza, Task Scheduler, claude auth status) - fon oqimida: oyna kutmasdan darhol ochiladi.
        _ = Task.Run(() =>
        {
            // Jadval botda (online bazada) boshqariladi: shu kompyuterdagi Task Scheduler unga moslanadi.
            Infrastructure.Scheduling.ScheduleSync.Apply(svc, workerExe);
            try { svc.Settings.PublishCatalog(); } catch (Exception ex) { svc.Log.Warn("Bot katalogi yuborilmadi: " + ex.Message); }
            Infrastructure.Heartbeat.ToolStatus.Publish(svc);
        });

        // Heartbeat: kunduzi faqat har 2 daqiqada so'rov almashiladi (bazaga yengil yozuv); botdan tekshiruv so'rovi kelsa shu yerda ham bajariladi.
        _beat = new System.Threading.Timer(async _ =>
        {
            Infrastructure.Heartbeat.MachineBeat.Beat(Services, appOpen: true);
            Infrastructure.Heartbeat.MachineBeat.StartBatchIfRequested(Services, workerExe);
            try { Infrastructure.Commands.RemoteCommands.Run(Services); } catch { }
            try { await Infrastructure.Templates.RemoteTemplateImporter.RunPendingAsync(Services, CancellationToken.None); } catch { }
            if (System.Threading.Interlocked.Exchange(ref _templatesSynced, 1) == 0)
                try { await Infrastructure.Templates.JobTemplates.SyncAsync(Services, CancellationToken.None); } catch { }
            try { await Infrastructure.Heartbeat.MachineBeat.RunCheckIfRequestedAsync(Services, workerExe, CancellationToken.None); } catch { }
        }, null, TimeSpan.Zero, TimeSpan.FromMinutes(Infrastructure.Scheduling.TaskSchedulerService.PollIntervalMinutes));
    }

    private System.Threading.Timer? _beat;
    private int _templatesSynced;

    protected override void OnExit(ExitEventArgs e)
    {
        try { _beat?.Dispose(); } catch { }
        try { if (Services is not null && Services.IsOnline) Infrastructure.Heartbeat.MachineBeat.Beat(Services, appOpen: false); } catch { }
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services?.Log.Error("UI xatosi: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "YtAuto - xato", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
