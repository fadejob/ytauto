using CommunityToolkit.Mvvm.ComponentModel;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

/// <summary>
/// Ilovada faqat Sozlamalar qoladi. Mavzular, promptlar va jadval Telegram bot (web app) orqali,
/// online baza (Supabase) bilan boshqariladi.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public AppServices Services { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(AppServices services)
    {
        Services = services;
        Settings = new SettingsViewModel(services);
        Settings.Load();
    }
}
