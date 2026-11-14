using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;
using YtAuto.Core.Scheduling;
using YtAuto.Desktop.Ui;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

public sealed record StageRow(string Stage, string State, string Started, string Duration, int Attempts, string Error, string Metrics);

public sealed partial class JobDetailsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Action _changed;
    private readonly Action _back;
    public long JobId { get; }

    // ---- Tahrirlash (video hali YouTube'ga yuklanmagan bo'lsa)
    [ObservableProperty] private string _editTopic = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private string _editLanguage = "en";
    [ObservableProperty] private VideoFormat _editFormat;
    [ObservableProperty] private TemplateOption? _editTemplate;
    [ObservableProperty] private string _editVoice = "";
    public List<string> Languages { get; } = new() { "en", "uz", "ru", "tr", "kk" };
    public List<VideoFormat> Formats { get; } = Enum.GetValues<VideoFormat>().ToList();
    public List<TemplateOption> Templates { get; private set; } = new();
    public List<string> VoiceProfiles { get; private set; } = new();
    public bool CanEdit => Job is not null && string.IsNullOrEmpty(Job.YouTubeVideoId) && Job.Status is not (JobStatus.Cancelled or JobStatus.Published);
    public string EditNote => Job is null ? "" : !string.IsNullOrEmpty(Job.YouTubeVideoId)
        ? "Video YouTube'ga yuklangan — mavzu va vaqtni bu yerda o'zgartirib bo'lmaydi (YouTube Studio'da o'zgartiring)."
        : Job.Status is JobStatus.Published or JobStatus.Cancelled ? "Bu vazifani endi tahrirlab bo'lmaydi." : "";

    [RelayCommand] private void Back() { StopMedia(); _back(); }

    /// <summary>Mavzuni butunlay o'chiradi (online bazadan ham). Arxivdagi video/ovoz va YouTube'dagi video o'chirilmaydi.</summary>
    [RelayCommand]
    private void Delete()
    {
        if (_svc.Store.IsClaimed(JobId)) { Message = "Worker hozir shu vazifa ustida ishlamoqda — avval 'Bekor qilish'ni bosing."; return; }
        var extra = !string.IsNullOrEmpty(Job.YouTubeVideoId) ? "\n\nDiqqat: video YouTube'ga yuklangan — u YouTube'da qoladi (kerak bo'lsa YouTube Studio'da o'chiring)." : "";
        if (MessageBox.Show($"\"{Job.DisplayTitle}\" mavzusi o'chirilsinmi?\nBu amalni qaytarib bo'lmaydi. Arxivdagi fayllar saqlanadi.{extra}",
                "YtAuto", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        StopMedia();
        var workDir = Job.WorkDir;
        if (!_svc.Store.Delete(JobId)) { Message = "O'chirib bo'lmadi (worker band qilgan bo'lishi mumkin)."; return; }
        // Vaqtinchalik ish papkasi (faqat ilovaning jobs papkasi ichida).
        try
        {
            var root = _svc.Settings.Load().Paths.WorkDir;
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir) && Path.GetFileName(workDir.TrimEnd('\\')) == $"job-{JobId}")
                PathGuard.SafeDeleteDirectory(root, workDir);
        }
        catch { /* ish papkasi qolsa ham zarari yo'q */ }
        _svc.Secrets.Set(SecretNames.UploadSessionPrefix + JobId, null);
        _svc.Log.Info($"#{JobId} mavzusi o'chirildi.");
        _changed();
        _back();
    }

    [ObservableProperty] private VideoJob _job = null!;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _subtitles = "";
    [ObservableProperty] private string _logTail = "";
    [ObservableProperty] private Uri? _videoSource;
    [ObservableProperty] private Uri? _audioSource;
    [ObservableProperty] private DateTime _newPublishDate = DateTime.Today;
    [ObservableProperty] private string _newPublishTime = "18:00";
    [ObservableProperty] private string _message = "";
    public ObservableCollection<StageRow> Stages { get; } = new();

    public JobDetailsViewModel(AppServices svc, long jobId, Action changed, Action back)
    {
        _svc = svc; JobId = jobId; _changed = changed; _back = back;
        Load();
        ResetEditForm();
    }

    private void ResetEditForm()
    {
        var s = _svc.Settings.Load();
        Templates = new List<TemplateOption> { new("", "Shablonsiz (standart dizayn)", null, "") }
            .Concat(s.Templates.Select(t => new TemplateOption(t.Name, t.Name, t.Thumbnail,
                t.Kind == TemplateKind.OpenFilmProject ? "OpenFilm loyiha" : t.Kind == TemplateKind.Mp4Reference ? "Video namuna" : "")))
            .ToList();
        VoiceProfiles = s.VoiceProfiles.Select(v => v.Name).ToList();
        if (!string.IsNullOrEmpty(Job.VoiceProfileName) && !VoiceProfiles.Contains(Job.VoiceProfileName)) VoiceProfiles.Insert(0, Job.VoiceProfileName);
        OnPropertyChanged(nameof(Templates)); OnPropertyChanged(nameof(VoiceProfiles));
        EditTopic = Job.Topic; EditNotes = Job.Notes; EditLanguage = Job.Language; EditFormat = Job.Format; EditVoice = Job.VoiceProfileName;
        EditTemplate = Templates.FirstOrDefault(t => string.Equals(t.Name, Job.TemplateName, StringComparison.OrdinalIgnoreCase)) ?? Templates[0];
    }

    /// <summary>
    /// Chiqmagan (YouTube'ga yuklanmagan) videoning mavzusi/sozlamalari/vaqtini yangilaydi. Mazmun o'zgarsa, vazifa joriy sozlamalar bilan
    /// qayta snapshot qilinadi va navbatga qaytadi: faqat o'zgarishga bog'liq bosqichlar qayta bajariladi (mavzu → ssenariydan, shablon → montajdan).
    /// </summary>
    [RelayCommand]
    private void SaveEdit()
    {
        if (!CanEdit) { Message = EditNote; return; }
        if (string.IsNullOrWhiteSpace(EditTopic)) { Message = "Mavzu bo'sh bo'lmasligi kerak."; return; }
        if (_svc.Store.IsClaimed(JobId)) { Message = "Worker hozir shu vazifa ustida ishlamoqda — tugagach yoki bekor qilingach o'zgartiring."; return; }
        var s = _svc.Settings.Load();
        if (s.FindVoice(EditVoice) is null || !s.VoiceProfiles.Any(v => v.Name == EditVoice)) { Message = "Ovoz profili topilmadi — Sozlamalar → Ovoz bo'limidan tanlang."; return; }
        var tz = Tz;
        var utc = TimeZones.ToUtc(DateOnly.FromDateTime(NewPublishDate), TimeZones.ParseTime(NewPublishTime, new TimeOnly(18, 0)), tz);
        if (utc <= DateTimeOffset.UtcNow.AddMinutes(30)) { Message = "Chiqish vaqti kamida 30 daqiqa kelajakda bo'lishi kerak."; return; }

        var j = _svc.Store.Get(JobId)!;
        var tpl = EditTemplate?.Name ?? "";
        var textChanged = j.Topic != EditTopic.Trim() || j.Notes != (EditNotes ?? "").Trim() || j.Language != EditLanguage;
        var contentChanged = textChanged || j.Format != EditFormat || !string.Equals(j.TemplateName, tpl, StringComparison.OrdinalIgnoreCase) || j.VoiceProfileName != EditVoice;
        var timeChanged = j.PublishAtUtc != utc;
        if (!contentChanged && !timeChanged) { Message = "O'zgarish yo'q."; return; }

        j.Topic = EditTopic.Trim(); j.Notes = (EditNotes ?? "").Trim(); j.Language = EditLanguage; j.Format = EditFormat;
        j.TemplateName = tpl; j.VoiceProfileName = EditVoice; j.PublishAtUtc = utc;
        if (contentChanged)
        {
            j.SettingsSnapshotJson = null;          // yangi shablon/ovoz profillari ham ko'rinsin
            if (textChanged) j.Title = null;        // yangi ssenariy bilan qayta yoziladi
        }
        _svc.Store.Update(j);
        var requeue = contentChanged ? j.Status != JobStatus.Queued : j.Status == JobStatus.NeedsAttention;
        if (requeue) _svc.Store.Requeue(JobId, resetAttempts: true);
        Message = contentChanged
            ? "Saqlandi. Vazifa navbatda: keyingi ishga tushishda faqat o'zgarishga bog'liq bosqichlar qayta bajariladi."
            : $"Yangi chiqish vaqti saqlandi: {TimeZones.ToZone(utc, tz):yyyy-MM-dd HH:mm}." + (requeue ? " Vazifa navbatga qaytdi (video qayta yaratilmaydi)." : "");
        _svc.Log.Info($"#{JobId} tahrirlandi: mazmun={(contentChanged ? "ha" : "yo'q")}, vaqt={(timeChanged ? "ha" : "yo'q")}.");
        Done();
        ResetEditForm();
    }

    private TimeZoneInfo Tz => TimeZones.Find(_svc.Settings.Load().YouTube.TimeZoneId);

    public string PublishText => Job is null ? "" : $"{TimeZones.ToZone(Job.PublishAtUtc, Tz):yyyy-MM-dd HH:mm} ({_svc.Settings.Load().YouTube.TimeZoneId})";
    public bool CanRetry => Job is { Status: JobStatus.Failed or JobStatus.NeedsAttention };
    public bool CanCancel => Job is not null && !JobStateMachine.IsTerminal(Job.Status) && Job.Status != JobStatus.Published;
    public bool CanReschedule => Job is not null && string.IsNullOrEmpty(Job.YouTubeVideoId) && Job.Status is not (JobStatus.Cancelled or JobStatus.Published);
    public bool CanForceNewUpload => Job is { Status: JobStatus.NeedsAttention } && string.IsNullOrEmpty(Job.YouTubeVideoId) && Job.LastErrorCode is "upload_session_lost" or "upload_session_expired" or "upload_no_id";

    [RelayCommand]
    public void Load()
    {
        Job = _svc.Store.Get(JobId) ?? throw new InvalidOperationException("Vazifa topilmadi");
        StatusText = JobStateMachine.Describe(Job.Status);
        var local = TimeZones.ToZone(Job.PublishAtUtc, Tz);
        NewPublishDate = local.Date; NewPublishTime = local.ToString("HH:mm");
        Stages.Clear();
        foreach (var st in _svc.Store.GetStages(JobId))
        {
            var metrics = "";
            if (st.MetricsJson is { Length: > 2 })
                try { metrics = string.Join(", ", JsonSerializer.Deserialize<Dictionary<string, double>>(st.MetricsJson)!.Select(kv => $"{kv.Key}={kv.Value:0.##}")); } catch { }
            Stages.Add(new StageRow(st.Stage.ToString(), st.State.ToString(), st.StartedAtUtc?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "",
                st.DurationMs is null ? "" : $"{st.DurationMs / 1000.0:0.0}s", st.Attempts, st.ErrorMessage ?? "", metrics));
        }
        // Subtitr va loglar: faqat boshi/oxiri o'qiladi (katta fayllar to'liq yuklanmaydi).
        Subtitles = Job.SubtitleArchivePath is { } srt && File.Exists(srt) ? FileOps.ReadHead(srt, 64 * 1024) : "(subtitr hali yo'q)";
        var events = _svc.Store.GetEvents(JobId, 300);
        LogTail = string.Join(Environment.NewLine, events.Select(e => $"{e.AtUtc.ToLocalTime():MM-dd HH:mm:ss} {e.Level,-5} {(e.Stage is null ? "" : $"[{e.Stage}] ")}{e.Message}"));
        OnPropertyChanged(nameof(PublishText)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanReschedule)); OnPropertyChanged(nameof(CanForceNewUpload));
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(EditNote));
    }

    // Media faqat foydalanuvchi bosganda yuklanadi; bir vaqtda faqat bitta.
    [RelayCommand] private void PlayVideo() { AudioSource = null; if (File.Exists(Job.VideoArchivePath)) VideoSource = new Uri(Job.VideoArchivePath!); }
    [RelayCommand] private void PlayAudio() { VideoSource = null; if (File.Exists(Job.AudioArchivePath)) AudioSource = new Uri(Job.AudioArchivePath!); }
    [RelayCommand] public void StopMedia() { VideoSource = null; AudioSource = null; }

    [RelayCommand] private void OpenVideoFolder() => Shell.OpenFolderOf(Job.VideoArchivePath);
    [RelayCommand] private void OpenAudioFolder() => Shell.OpenFolderOf(Job.AudioArchivePath);
    [RelayCommand] private void OpenRecords() => Shell.OpenFolderOf(_svc.Paths.RecordDirFor(JobId));
    [RelayCommand] private void OpenWorkDir() => Shell.OpenFolderOf(Job.WorkDir);
    [RelayCommand] private void OpenYouTube() => Shell.OpenUrl(Job.YouTubeUrl);

    [RelayCommand]
    private void Retry()
    {
        if (_svc.Store.IsClaimed(JobId)) { Message = "Worker hozir shu vazifa ustida ishlamoqda."; return; }
        _svc.Store.Requeue(JobId, resetAttempts: true);
        Message = "Navbatga qaytarildi: tayyor bosqichlar saqlanadi, keyingi ishga tushishda tugallanmagan bosqichdan davom etadi.";
        Done();
    }

    [RelayCommand]
    private void RetryWithCurrentSettings()
    {
        if (_svc.Store.IsClaimed(JobId)) { Message = "Worker hozir shu vazifa ustida ishlamoqda."; return; }
        if (MessageBox.Show("Vazifa joriy sozlamalar/promptlar bilan qayta snapshot qilinadi. O'zgargan bosqichlar qayta bajariladi. Davom etilsinmi?",
                "YtAuto", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var j = _svc.Store.Get(JobId)!;
        j.SettingsSnapshotJson = null;
        _svc.Store.Update(j);
        _svc.Store.Requeue(JobId, resetAttempts: true);
        Message = "Joriy sozlamalar bilan navbatga qaytarildi.";
        Done();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (MessageBox.Show("Vazifa bekor qilinsinmi? YouTube'ga yuklangan video o'chirilmaydi.", "YtAuto", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_svc.Store.TryCancelUnclaimed(JobId)) Message = "Bekor qilindi.";
        else { _svc.Store.RequestCancel(JobId); Message = "Worker ishlamoqda: bekor qilish so'rovi yuborildi (u o'zi ochgan jarayonlarni yopib to'xtaydi)."; }
        Done();
    }

    [RelayCommand]
    private void Reschedule()
    {
        var tz = Tz;
        var time = TimeZones.ParseTime(NewPublishTime, new TimeOnly(18, 0));
        var utc = TimeZones.ToUtc(DateOnly.FromDateTime(NewPublishDate), time, tz);
        if (utc <= DateTimeOffset.UtcNow.AddMinutes(30)) { Message = "Yangi vaqt kamida 30 daqiqa kelajakda bo'lishi kerak."; return; }
        if (_svc.Store.IsClaimed(JobId)) { Message = "Worker hozir shu vazifa ustida ishlamoqda; keyinroq o'zgartiring."; return; }
        var j = _svc.Store.Get(JobId)!;
        j.PublishAtUtc = utc;
        _svc.Store.Update(j);
        if (j.Status == JobStatus.NeedsAttention) _svc.Store.Requeue(JobId, resetAttempts: true);
        Message = $"Yangi chiqish vaqti: {TimeZones.ToZone(utc, tz):yyyy-MM-dd HH:mm}." + (j.Status == JobStatus.NeedsAttention ? " Vazifa navbatga qaytdi (arxivdagi video yuklanadi, qayta yaratilmaydi)." : "");
        Done();
    }

    [RelayCommand]
    private void ForceNewUpload()
    {
        if (MessageBox.Show("YouTube Studio'da bu video YO'QLIGINI tekshirdingizmi? Aks holda ikki marta yuklanadi.\n\nYangi upload boshlansinmi?",
                "YtAuto", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _svc.Secrets.Set(SecretNames.UploadSessionPrefix + JobId, null);
        var st = _svc.Store.GetStage(JobId, StageKind.Upload);
        st.State = StageState.Pending; st.OutputsJson = null; _svc.Store.SaveStage(st);
        _svc.Store.Requeue(JobId, resetAttempts: true);
        Message = "Upload sessiyasi tozalandi; keyingi ishga tushishda yangi upload boshlanadi.";
        Done();
    }

    [RelayCommand]
    private async Task RefreshYouTube()
    {
        if (string.IsNullOrEmpty(Job.YouTubeVideoId)) { Message = "Video hali yuklanmagan."; return; }
        try
        {
            var runner = new JobRunner(_svc.Pipeline());
            var st = await Task.Run(() => runner.RefreshYouTubeStatusAsync(Job, new JobLog(JobId, _svc.Paths.RecordDirFor(JobId), _svc.Store, _svc.Log), CancellationToken.None));
            Message = $"YouTube holati: {JobStateMachine.Describe(st)}";
        }
        catch (Exception e) { Message = "Tekshirib bo'lmadi: " + e.Message; }
        Done();
    }

    private void Done() { Load(); _changed(); }
}
