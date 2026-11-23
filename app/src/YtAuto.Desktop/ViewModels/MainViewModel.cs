using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

/// <summary>Chap menyu bo'limlari.</summary>
public enum Section { Home, Tools, Schedule, Settings }

/// <summary>Dasturlar va Jadval sahifalari bosh sahifa ma'lumotidan foydalanadi (bitta HomeViewModel): ko'rinish o'z DataTemplate'i bilan.</summary>
public sealed record ToolsPage(HomeViewModel Vm);
public sealed record SchedulePage(HomeViewModel Vm);

/// <summary>
/// Ilova: hisob sozlanmagan bo'lsa - birinchi o'rnatish ustasi (<see cref="SetupViewModel"/>, chap menyusiz), aks holda chap menyu bilan
/// bo'limlar: Bosh sahifa (tayyorlik, token, hozirgi ish), Dasturlar, Jadval, Sozlamalar. Mavzular, promptlar, Gemini va YouTube - Telegram botda.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public AppServices Services { get; private set; }
    [ObservableProperty] private object _page;
    [ObservableProperty] private bool _isSetup;
    [ObservableProperty] private Section _section = Section.Home;
    [ObservableProperty] private HomeViewModel? _home;
    private SettingsViewModel? _settings;

    public MainViewModel(AppServices services, Action<AppServices> adoptOnline)
    {
        Services = services;
        if (services.IsAccountReady) { _home = NewHome(services); _page = _home; }
        else
        {
            _isSetup = true;
            _page = new SetupViewModel(services, online =>
            {
                Services = online;
                adoptOnline(online);
                Home = NewHome(online);
                IsSetup = false;
                Section = Section.Home;
                Page = Home;
            });
        }
    }

    private HomeViewModel NewHome(AppServices s) => new(s, () => Go(Section.Settings));

    partial void OnSectionChanged(Section value)
    {
        if (IsSetup || Home is null) return;
        Page = value switch
        {
            Section.Tools => new ToolsPage(Home),
            Section.Schedule => new SchedulePage(Home),
            Section.Settings => _settings ??= new SettingsViewModel(Services).Load(),
            _ => Home,
        };
    }

    [RelayCommand] private void Go(Section s) => Section = s;
}
