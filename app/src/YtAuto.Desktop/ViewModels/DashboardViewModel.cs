using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

public sealed class JobCardViewModel
{
    public VideoJob Job { get; }
    public JobCardViewModel(VideoJob j, TimeZoneInfo tz) { Job = j; Tz = tz; }
    private TimeZoneInfo Tz { get; }
    public long Id => Job.Id;
    public string Title => Job.DisplayTitle;
    public string Topic => string.IsNullOrWhiteSpace(Job.Title) ? "" : Job.Topic;
    public JobStatus Status => Job.Status;
    public string StatusText => JobStateMachine.Describe(Job.Status) + (Job.Status == JobStatus.Failed ? $" ({Job.RunAttempts})" : "");
    public string PublishText => TimeZones.ToZone(Job.PublishAtUtc, Tz).ToString("yyyy-MM-dd HH:mm")
        + (Job.AudioDurationSec is double d ? $"  •  {TimeSpan.FromSeconds(Math.Round(d)):m\\:ss}" : "")
        + (Job.PublishedAtUtc is { } p ? $"  •  chiqdi {TimeZones.ToZone(p, Tz):yyyy-MM-dd HH:mm}" : "");
    public string? Error => Job.Status is JobStatus.NeedsAttention or JobStatus.Failed ? Short(Job.AttentionReason ?? Job.LastErrorMessage) : null;
    public string? Thumbnail => Job.ThumbnailPath;
    public string Format => Job.Format switch { VideoFormat.Shorts9x16 => "9:16", VideoFormat.Square1x1 => "1:1", _ => "16:9" };
    private static string? Short(string? s) => s is null ? null : s.Length > 160 ? s[..160] + "…" : s;
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly DispatcherTimer _timer;
    public const int PageSize = 30;

    public ObservableCollection<JobCardViewModel> Upcoming { get; } = new();
    public ObservableCollection<JobCardViewModel> Released { get; } = new();

    [ObservableProperty] private int _upcomingPage;
    [ObservableProperty] private int _releasedPage;
    [ObservableProperty] private int _upcomingTotal;
    [ObservableProperty] private int _releasedTotal;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showReleased;

    public Action<long>? OpenDetails { get; set; }

    public DashboardViewModel(AppServices svc)
    {
        _svc = svc;
        // Yengil avtomatik yangilash: faqat bazadan bitta sahifa (media yuklanmaydi).
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Reload();
        _timer.Start();
    }

    public string UpcomingPageText => $"{UpcomingPage + 1} / {Math.Max(1, (UpcomingTotal + PageSize - 1) / PageSize)}  ({UpcomingTotal})";
    public string ReleasedPageText => $"{ReleasedPage + 1} / {Math.Max(1, (ReleasedTotal + PageSize - 1) / PageSize)}  ({ReleasedTotal})";

    public void Reload()
    {
        try
        {
            var tz = TimeZones.Find(_svc.Settings.Load().YouTube.TimeZoneId);
            var up = _svc.Store.Page(false, UpcomingPage * PageSize, PageSize, out var ut);
            var rel = _svc.Store.Page(true, ReleasedPage * PageSize, PageSize, out var rt);
            UpcomingTotal = ut; ReleasedTotal = rt;
            Sync(Upcoming, up.Select(j => new JobCardViewModel(j, tz)).ToList());
            Sync(Released, rel.Select(j => new JobCardViewModel(j, tz)).ToList());
            OnPropertyChanged(nameof(UpcomingPageText)); OnPropertyChanged(nameof(ReleasedPageText));
        }
        catch (Exception e) { Status = "Yangilashda xato: " + e.Message; }
    }

    private static void Sync(ObservableCollection<JobCardViewModel> target, List<JobCardViewModel> items)
    {
        // Faqat o'zgarganda almashtiramiz (ro'yxat "sakramasligi" uchun).
        bool same = target.Count == items.Count && target.Zip(items).All(p => p.First.Id == p.Second.Id && p.First.Job.UpdatedAtUtc == p.Second.Job.UpdatedAtUtc);
        if (same) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    [RelayCommand] private void NextUpcoming() { if ((UpcomingPage + 1) * PageSize < UpcomingTotal) { UpcomingPage++; Reload(); } }
    [RelayCommand] private void PrevUpcoming() { if (UpcomingPage > 0) { UpcomingPage--; Reload(); } }
    [RelayCommand] private void NextReleased() { if ((ReleasedPage + 1) * PageSize < ReleasedTotal) { ReleasedPage++; Reload(); } }
    [RelayCommand] private void PrevReleased() { if (ReleasedPage > 0) { ReleasedPage--; Reload(); } }

    [RelayCommand]
    private void Open(JobCardViewModel? card)
    {
        if (card is null) return;
        OpenDetails?.Invoke(card.Id); // tafsilotlar asosiy oyna ichida ochiladi (alohida oyna emas)
    }

    [RelayCommand]
    public async Task RefreshYouTubeAsync()
    {
        Reload();
        if (!_svc.Uploader.IsConnected) { Status = "YouTube ulanmagan - holat tekshirilmadi."; return; }
        Status = "YouTube holati tekshirilmoqda…";
        var jobs = _svc.Store.ListByStatus(JobStatus.Processing, JobStatus.Scheduled).Take(50).ToList();
        int n = 0;
        await Task.Run(async () =>
        {
            var runner = new JobRunner(_svc.Pipeline());
            foreach (var j in jobs)
            {
                try { await runner.RefreshYouTubeStatusAsync(j, new JobLog(j.Id, _svc.Paths.RecordDirFor(j.Id), _svc.Store, _svc.Log), CancellationToken.None); n++; }
                catch (Exception e) { _svc.Log.Warn($"#{j.Id} YouTube holati: {e.Message}"); }
            }
        });
        Status = $"YouTube holati yangilandi ({n}/{jobs.Count}) - {DateTime.Now:HH:mm}.";
        Reload();
    }
}
