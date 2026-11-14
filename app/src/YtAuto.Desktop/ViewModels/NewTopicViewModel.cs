using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Domain;
using YtAuto.Core.Scheduling;
using YtAuto.Infrastructure;

namespace YtAuto.Desktop.ViewModels;

public sealed record TemplateOption(string Name, string Title, string? Thumbnail, string KindText);

public sealed partial class NewTopicViewModel : ObservableObject
{
    private readonly AppServices _svc;
    private readonly Action _saved;

    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _language = "en";
    [ObservableProperty] private VideoFormat _format;
    [ObservableProperty] private TemplateOption? _template;
    [ObservableProperty] private string _voiceProfile = "";
    [ObservableProperty] private DateTime _publishDate = DateTime.Today;
    [ObservableProperty] private string _publishTime = "18:00";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _error = "";

    public List<string> Languages { get; } = new() { "en", "uz", "ru", "tr", "kk" };
    public List<VideoFormat> Formats { get; } = Enum.GetValues<VideoFormat>().ToList();
    public List<TemplateOption> Templates { get; private set; } = new();
    public List<string> VoiceProfiles { get; private set; } = new();

    public NewTopicViewModel(AppServices svc, Action saved) { _svc = svc; _saved = saved; }

    /// <summary>Standart qiymatlar sozlamalardan; sana avtomatik taklif qilinadi. Davomiylik so'ralmaydi (dinamik).</summary>
    public NewTopicViewModel Prepare()
    {
        var s = _svc.Settings.Load();
        Templates = new List<TemplateOption> { new("", "Shablonsiz (standart dizayn)", null, "") }
            .Concat(s.Templates.Select(t => new TemplateOption(t.Name, t.Name, t.Thumbnail,
                t.Kind == TemplateKind.OpenFilmProject ? "OpenFilm loyiha" : t.Kind == TemplateKind.Mp4Reference ? "Video namuna" : "")))
            .ToList();
        VoiceProfiles = s.VoiceProfiles.Select(v => v.Name).ToList();
        OnPropertyChanged(nameof(Templates)); OnPropertyChanged(nameof(VoiceProfiles));
        Topic = ""; Notes = ""; Error = "";
        Language = s.Defaults.Language; Format = s.Defaults.Format;
        Template = Templates.FirstOrDefault(t => string.Equals(t.Name, s.Defaults.Template, StringComparison.OrdinalIgnoreCase)) ?? Templates[0];
        VoiceProfile = string.IsNullOrEmpty(s.Defaults.VoiceProfile) ? VoiceProfiles.FirstOrDefault() ?? "" : s.Defaults.VoiceProfile;
        var tz = TimeZones.Find(s.YouTube.TimeZoneId);
        var utc = PublishDateSuggester.SuggestUtc(_svc.Store.PublishSlots(), DateTimeOffset.UtcNow, s.YouTube);
        var local = TimeZones.ToZone(utc, tz);
        PublishDate = local.Date; PublishTime = local.ToString("HH:mm");
        Hint = $"Taklif: oxirgi rejalashtirilgan sana + {PublishDateSuggester.GapDays} kun (bekor qilinganlar hisobga olinmaydi). Zona: {s.YouTube.TimeZoneId}.";
        if (utc <= DateTimeOffset.UtcNow.AddHours(3))
            Hint += " Diqqat: bu vaqt juda yaqin - worker ulgurmasa, video tayyor bo'lgan paytdan 35 daqiqa keyin chiqariladi.";
        return this;
    }

    [RelayCommand]
    private void Save()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Topic)) { Error = "Mavzu kiritilmagan."; return; }
        var s = _svc.Settings.Load();
        if (s.FindVoice(VoiceProfile) is null) { Error = "Ovoz profili tanlanmagan (Sozlamalar → Ovoz bo'limida qo'shing)."; return; }
        var tplName = Template?.Name ?? "";
        if (tplName.Length > 0 && s.FindTemplate(tplName) is null) { Error = $"Shablon topilmadi: {tplName}"; return; }
        var tz = TimeZones.Find(s.YouTube.TimeZoneId);
        var utc = TimeZones.ToUtc(DateOnly.FromDateTime(PublishDate), TimeZones.ParseTime(PublishTime, new TimeOnly(18, 0)), tz);
        if (utc <= DateTimeOffset.UtcNow) { Error = "Chiqish vaqti o'tib ketgan."; return; }
        var job = new VideoJob
        {
            Topic = Topic.Trim(), Notes = Notes?.Trim() ?? "", Language = Language, TargetDurationSec = 0, Format = Format,
            TemplateName = tplName, VoiceProfileName = VoiceProfile, PublishAtUtc = utc, Status = JobStatus.Queued, CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _svc.Store.Insert(job);
        _svc.Log.Info($"Yangi mavzu #{job.Id} qo'shildi: {job.Topic} (shablon: {(tplName.Length == 0 ? "yo'q" : tplName)})");
        _saved();
    }
}
