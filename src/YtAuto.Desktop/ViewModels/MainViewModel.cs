using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppServices Services { get; }
    public DashboardViewModel Dashboard { get; }
    public NewTopicViewModel NewTopic { get; }
    public SettingsViewModel Settings { get; }
    public ScheduleViewModel Schedule { get; }

    [ObservableProperty] private object _current;
    [ObservableProperty] private string _page = "dashboard";

    public MainViewModel(AppServices services)
    {
        Services = services;
        Dashboard = new DashboardViewModel(services);
        NewTopic = new NewTopicViewModel(services, () => { Navigate("dashboard"); Dashboard.Reload(); });
        Settings = new SettingsViewModel(services);
        Schedule = new ScheduleViewModel(services);
        Dashboard.OpenDetails = id =>
        {
            Page = "details";
            Current = new JobDetailsViewModel(services, id, Dashboard.Reload, () => NavigateCommand.Execute("dashboard"));
        };
        _current = Dashboard;
        Dashboard.Reload();
        _ = Dashboard.RefreshYouTubeAsync();
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        Page = page;
        Current = page switch
        {
            "new" => NewTopic.Prepare(),
            "settings" => Settings.Load(),
            "schedule" => Schedule.Load(),
            _ => Dashboard,
        };
        if (page == "dashboard") Dashboard.Reload();
    }
}
