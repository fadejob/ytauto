using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

/// <summary>
/// Ilova: hisob sozlanmagan bo'lsa - bitta tugmali sozlash (<see cref="SetupViewModel"/>), aks holda bosh sahifa
/// (<see cref="HomeViewModel"/>: token, ish vaqti, dasturlar). Mavzular, promptlar, Gemini va YouTube - Telegram botda.
/// Kengaytirilgan sozlamalar (yo'llar, shablonlar, ovozlar) alohida oynada.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public AppServices Services { get; private set; }
    [ObservableProperty] private object _page;

    public MainViewModel(AppServices services, Action<AppServices> adoptOnline)
    {
        Services = services;
        _page = services.IsAccountReady
            ? new HomeViewModel(services, OpenAdvanced)
            : new SetupViewModel(services, online =>
            {
                Services = online;
                adoptOnline(online);
                Page = new HomeViewModel(online, OpenAdvanced);
            });
    }

    private Window? _advanced;

    private void OpenAdvanced()
    {
        if (_advanced is { IsLoaded: true }) { _advanced.Activate(); return; }
        _advanced = new Window
        {
            Title = "YtAuto — kengaytirilgan sozlamalar", Width = 1000, Height = 760, Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (System.Windows.Media.Brush)Application.Current.Resources["Bg"],
            Content = new System.Windows.Controls.Border
            {
                Padding = new Thickness(24, 20, 24, 16),
                Child = new Views.SettingsView { DataContext = new SettingsViewModel(Services).Load() },
            },
        };
        _advanced.Show();
    }
}
