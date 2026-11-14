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
        try { Services = new AppServices("desktop"); }
        catch (Exception ex)
        {
            MessageBox.Show("Ilova ma'lumotlar bazasini ochib bo'lmadi:\n" + ex.Message, "YtAuto", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        // Desktop ochilishi video tayyorlashni BOSHLAMAYDI: faqat boshqaruv.
        var main = new MainWindow { DataContext = new MainViewModel(Services) };
        MainWindow = main;
        main.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services?.Log.Error("UI xatosi: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "YtAuto - xato", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
