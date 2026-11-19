using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YtAuto.Core.Domain;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;
using YtAuto.Desktop.Ui;
using YtAuto.Infrastructure;
using YtAuto.Infrastructure.Voice;
using YtAuto.Infrastructure.Scheduling;

namespace YtAuto.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _svc;
    [ObservableProperty] private AppSettings _s = new();
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _geminiKeyState = "";
    [ObservableProperty] private string _youTubeState = "";
    [ObservableProperty] private bool _youTubeConnected;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _deepClaudeCheck;
    public ObservableCollection<VoiceProfile> Voices { get; } = new();
    public ObservableCollection<TemplateDefinition> Templates { get; } = new();
    public ObservableCollection<CheckResult> Checks { get; } = new();
    public List<VideoFormat> Formats { get; } = Enum.GetValues<VideoFormat>().ToList();
    public List<TemplateKind> TemplateKinds { get; } = Enum.GetValues<TemplateKind>().ToList();
    public List<string> Languages { get; } = new() { "en", "uz", "ru", "tr", "kk" };

    /// <summary>PasswordBox'lardan (code-behind) keladi; bo'sh bo'lsa mavjud sir o'zgarmaydi.</summary>
    public string ClientSecretInput { get; set; } = "";

    // ---- Kerakli dasturlarni yuklab o'rnatish (OpenFilm, VoiceStudio)
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private double _installFraction;
    [ObservableProperty] private bool _installIndeterminate;
    [ObservableProperty] private string _installText = "";
    [ObservableProperty] private string _openFilmState = "";
    [ObservableProperty] private string _voiceStudioState = "";
    [ObservableProperty] private bool _openFilmMissing;
    [ObservableProperty] private bool _voiceStudioMissing;

    [ObservableProperty] private bool _claudeMissing;
    [ObservableProperty] private bool _whisperMissing;
    [ObservableProperty] private string _whisperState = "";

    [RelayCommand]
    private Task InstallWhisperModel() => RunInstall(async p =>
    {
        var model = S.VoiceStudio.WhisperModel;
        var dir = S.VoiceStudio.HfCacheDir;
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "hf_cache");
        await AppInstaller.DownloadHfModelAsync(model, dir, p, CancellationToken.None);
        var s = _svc.Settings.Load(); s.VoiceStudio.HfCacheDir = dir; _svc.Settings.Save(s);
        return dir;
    });

    // Ovoz (TTS): faqat MOSS-TTS-Nano. VoiceStudio uni o'z Python muhiti bilan o'rnatadi (~8 GB, OMNIVOICE_ENGINES_DIR papkasiga).
    // Ayol/erkak profillari sozlamalar yuklanganda doim bor (TtsVoices.Normalize); ovoz namunasi VoiceStudio profillarida.
    [ObservableProperty] private bool _ttsMissing;
    [ObservableProperty] private string _ttsState = "";

    [RelayCommand]
    private async Task InstallTtsVoices()
    {
        if (TtsMissing)
        {
            await RunInstall(async p =>
            {
                await MossEngine.InstallAsync(S.VoiceStudio, _svc.LocalDb, _svc.Log,
                    new Progress<string>(text => p.Report(new InstallProgress("tts", -1, text))), CancellationToken.None);
                return MossEngine.CheckoutDir();
            });
            if (TtsMissing) return; // o'rnatilmadi (xato RunInstall'da ko'rsatilgan)
        }
        var s = _svc.Settings.Load(); // MOSS ayol/erkak profillari shu yerda qo'yiladi
        _svc.Settings.Save(s);
        try { _svc.Settings.PushVoiceProfiles(s.VoiceProfiles, s.Defaults.VoiceProfile); } catch { } // bot ro'yxati ham (asosiy manba)
        Load();
        Message = $"Ovozlar tayyor: {string.Join(", ", S.VoiceProfiles.Select(v => v.Name))}. Botda mavzu yaratishda tanlanadi.";
        await RefreshCatalogCommand.ExecuteAsync(null);
    }

    private void RefreshToolState()
    {
        TtsMissing = !MossEngine.IsInstalled();
        TtsState = TtsMissing
            ? $"Ovoz modeli ({TtsVoices.DisplayName}): kompyuterda yo'q. VoiceStudio orqali o'rnatiladi (~8 GB, {MossEngine.EnginesDir()})."
            : $"Ovoz modeli: {TtsVoices.DisplayName}. Ovozlar: {string.Join(", ", S.VoiceProfiles.Select(v => v.Name))} (namuna audio VoiceStudio'dagi «{TtsVoices.FemaleRef}» / «{TtsVoices.MaleRef}» profillarida)";
        ClaudeMissing = !File.Exists(S.Claude.ExecutablePath);
        var modelDir = "models--" + S.VoiceStudio.WhisperModel.Replace("/", "--");
        WhisperMissing = string.IsNullOrWhiteSpace(S.VoiceStudio.HfCacheDir) || !Directory.Exists(Path.Combine(S.VoiceStudio.HfCacheDir, modelDir));
        WhisperState = WhisperMissing ? $"Subtitr modeli ({S.VoiceStudio.WhisperModel}): kompyuterda topilmadi" : $"Subtitr modeli ({S.VoiceStudio.WhisperModel}): {S.VoiceStudio.HfCacheDir}";
        OpenFilmMissing = !File.Exists(S.OpenFilm.ExePath);
        VoiceStudioMissing = !File.Exists(S.VoiceStudio.ExePath);
        OpenFilmState = OpenFilmMissing ? "OpenFilm: kompyuterda topilmadi" : $"OpenFilm: {S.OpenFilm.ExePath}";
        VoiceStudioState = VoiceStudioMissing ? "VoiceStudio: kompyuterda topilmadi" : $"VoiceStudio: {S.VoiceStudio.ExePath}";
    }

    private async Task RunInstall(Func<IProgress<InstallProgress>, Task<string>> install)
    {
        Installing = true; Busy = true; InstallFraction = 0; InstallIndeterminate = true; InstallText = "Boshlanmoqda…";
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                InstallText = p.Text;
                InstallIndeterminate = p.Fraction < 0;
                if (p.Fraction >= 0) InstallFraction = p.Fraction * 100;
            });
            var exe = await Task.Run(() => install(progress));
            Load(); // yo'l avtomatik topilib, sozlamalarda saqlanadi
            Message = $"O'rnatildi va yo'li saqlandi: {exe}";
        }
        catch (Exception e) { InstallText = "Xato: " + e.Message; Message = "O'rnatib bo'lmadi: " + e.Message; }
        finally { Installing = false; Busy = false; InstallIndeterminate = false; }
    }

    [RelayCommand] private Task InstallOpenFilm() => RunInstall(p => AppInstaller.InstallOpenFilmAsync(p, CancellationToken.None));
    [RelayCommand] private Task InstallVoiceStudio() => RunInstall(p => AppInstaller.InstallVoiceStudioAsync(p, CancellationToken.None));

    // ---- VoiceStudio katalogi (kompyuterdagi barcha TTS dvigatellari/modellari/ovozlari)
    private List<VoiceEngineInfo> _allEngines = new();
    public ObservableCollection<VoiceEngineInfo> Engines { get; } = new();
    public ObservableCollection<string> EngineVoices { get; } = new();
    [ObservableProperty] private VoiceEngineInfo? _selectedEngine;
    [ObservableProperty] private string _selectedVoice = "";
    [ObservableProperty] private string _newVoiceLanguage = "en";
    [ObservableProperty] private string _newVoiceName = "";
    [ObservableProperty] private bool _showAllEngines;
    private bool _autoCatalogTried;
    [ObservableProperty] private string _catalogInfo = "";

    // ---- YouTube video sozlamalari
    public ObservableCollection<CategoryItem> Categories { get; } = new();
    [ObservableProperty] private CategoryItem? _selectedCategory;
    [ObservableProperty] private string _categoryInfo = "";
    public List<CategoryItem> Licenses { get; } = new()
    {
        new() { Id = "youtube", Title = "Standart YouTube litsenziyasi" },
        new() { Id = "creativeCommon", Title = "Creative Commons — Attribution (CC BY)" },
    };
    [ObservableProperty] private CategoryItem? _selectedLicense;
    public bool NotForKids { get => !S.YouTube.MadeForKids; set { S.YouTube.MadeForKids = !value; OnPropertyChanged(); OnPropertyChanged(nameof(ForKids)); } }
    public bool ForKids { get => S.YouTube.MadeForKids; set { S.YouTube.MadeForKids = value; OnPropertyChanged(); OnPropertyChanged(nameof(NotForKids)); } }
    public bool AiYes { get => S.YouTube.ContainsSyntheticMedia; set { S.YouTube.ContainsSyntheticMedia = value; OnPropertyChanged(); OnPropertyChanged(nameof(AiNo)); } }
    public bool AiNo { get => !S.YouTube.ContainsSyntheticMedia; set { S.YouTube.ContainsSyntheticMedia = !value; OnPropertyChanged(); OnPropertyChanged(nameof(AiYes)); } }

    partial void OnSelectedCategoryChanged(CategoryItem? value) { if (value is not null) S.YouTube.CategoryId = value.Id; }
    partial void OnSelectedLicenseChanged(CategoryItem? value) { if (value is not null) S.YouTube.License = value.Id; }

    private void ApplyCategories()
    {
        var list = S.YouTube.CategoryCache.Count > 0 ? S.YouTube.CategoryCache : Infrastructure.YouTube.YouTubeCategories.Fallback.ToList();
        Categories.Clear(); foreach (var c in list) Categories.Add(c);
        var current = Categories.FirstOrDefault(c => c.Id == S.YouTube.CategoryId);
        if (current is null && !string.IsNullOrEmpty(S.YouTube.CategoryId))
        {
            current = new CategoryItem { Id = S.YouTube.CategoryId, Title = $"Kategoriya {S.YouTube.CategoryId}" };
            Categories.Add(current);
        }
        SelectedCategory = current;
        SelectedLicense = Licenses.FirstOrDefault(l => l.Id == S.YouTube.License) ?? Licenses[0];
        CategoryInfo = S.YouTube.CategoryCache.Count > 0
            ? $"Ro'yxat YouTube'dan olingan ({S.YouTube.RegionCode})."
            : "Standart ro'yxat. Aniq ro'yxat uchun 'Kategoriyalarni YouTube'dan olish' tugmasini bosing.";
        OnPropertyChanged(nameof(NotForKids)); OnPropertyChanged(nameof(ForKids)); OnPropertyChanged(nameof(AiYes)); OnPropertyChanged(nameof(AiNo));
    }

    [RelayCommand]
    private async Task RefreshCategories()
    {
        if (!_svc.Uploader.IsConnected) { Message = "Avval YouTube kanalini ulang."; return; }
        Busy = true;
        try
        {
            var region = string.IsNullOrWhiteSpace(S.YouTube.RegionCode) ? "UZ" : S.YouTube.RegionCode.Trim().ToUpperInvariant();
            var list = await Task.Run(() => _svc.Uploader.GetCategoriesAsync(region, "uz", CancellationToken.None));
            if (list.Count == 0) { Message = "YouTube bu mamlakat uchun kategoriya qaytarmadi."; return; }
            S.YouTube.RegionCode = region;
            S.YouTube.CategoryCache = list;
            ApplyCategories();
            Save();
            Message = $"YouTube'dan {list.Count} ta kategoriya olindi.";
        }
        catch (Exception e) { Message = "Kategoriyalarni olib bo'lmadi: " + e.Message; }
        finally { Busy = false; }
    }

    public SettingsViewModel(AppServices svc) => _svc = svc;

    /// <summary>Oyna ochilgandagi (oxirgi Load) holat: saqlashda faqat foydalanuvchi o'zgartirgan maydonlar botga yoziladi.</summary>
    private AppSettings? _loaded;

    public SettingsViewModel Load()
    {
        S = _svc.Settings.Load();
        _loaded = AppSettings.FromJson(S.ToJson());
        Voices.Clear(); foreach (var v in S.VoiceProfiles) Voices.Add(v);
        Templates.Clear(); foreach (var t in S.Templates) Templates.Add(t);
        var cached = new Infrastructure.Voice.VoiceCatalogService(_svc.LocalDb).Cached();
        ApplyCatalog(cached);
        ApplyCategories();
        RefreshToolState();
        // Ovoz ro'yxati bo'sh bo'lmasligi uchun: VoiceStudio bor-u, modellar hali o'qilmagan bo'lsa, o'zi bir marta o'qiydi.
        if ((cached is null || cached.Engines.Count == 0) && !_autoCatalogTried && File.Exists(S.VoiceStudio.ExePath))
        {
            _autoCatalogTried = true;
            _ = RefreshCatalogCommand.ExecuteAsync(null);
        }
        YouTubeConnected = _svc.Uploader.IsConnected;
        _ = Task.Run(() => Infrastructure.Heartbeat.ToolStatus.Publish(_svc)); // holat bazaga (bot ko'rsatadi)
        YouTubeState = YouTubeConnected ? $"ulangan{(string.IsNullOrWhiteSpace(S.YouTube.ChannelTitle) ? "" : ": " + S.YouTube.ChannelTitle)}" : "ulanmagan";
        Message = "";
        return this;
    }

    private List<string> Validate(AppSettings s)
    {
        var e = new List<string>();
        foreach (var (n, p) in new[] { ("Ish papkasi", s.Paths.WorkDir), ("Video arxiv", s.Paths.VideoArchiveDir), ("Ovoz arxiv", s.Paths.AudioArchiveDir) })
        {
            if (string.IsNullOrWhiteSpace(p)) { e.Add($"{n} bo'sh."); continue; }
            if (!Path.IsPathFullyQualified(p)) e.Add($"{n} to'liq yo'l bo'lishi kerak: {p}");
            else if (Path.GetPathRoot(p)?.TrimEnd('\\') == PathGuard.Full(p).TrimEnd('\\')) e.Add($"{n} disk ildizi bo'lmasligi kerak.");
        }
        if (e.Count == 0)
        {
            if (PathGuard.IsUnder(s.Paths.WorkDir, s.Paths.VideoArchiveDir) || PathGuard.IsUnder(s.Paths.WorkDir, s.Paths.AudioArchiveDir) ||
                string.Equals(PathGuard.Full(s.Paths.WorkDir), PathGuard.Full(s.Paths.VideoArchiveDir), StringComparison.OrdinalIgnoreCase))
                e.Add("Arxiv papkasi vaqtinchalik ish papkasi ichida yoki u bilan bir xil bo'lmasligi kerak (tozalashda o'chib ketadi).");
            foreach (var t in s.Templates.Where(t => t.Kind == TemplateKind.OpenFilmProject && !string.IsNullOrWhiteSpace(t.Path)))
                if (PathGuard.IsUnder(s.Paths.WorkDir, t.Path)) e.Add($"Shablon '{t.Name}' ish papkasi ichida bo'lmasligi kerak.");
            if (!string.IsNullOrWhiteSpace(s.Paths.TemplatesDir) && (PathGuard.IsUnder(s.Paths.WorkDir, s.Paths.TemplatesDir)
                || string.Equals(PathGuard.Full(s.Paths.WorkDir), PathGuard.Full(s.Paths.TemplatesDir), StringComparison.OrdinalIgnoreCase)))
                e.Add("Shablonlar papkasi vaqtinchalik ish papkasi ichida bo'lmasligi kerak (tozalashda o'chib ketadi).");
        }
        if (s.VoiceProfiles.Count == 0) e.Add("Kamida bitta ovoz profili kerak.");
        if (s.VoiceProfiles.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) e.Add("Ovoz profillari nomlari takrorlanmasin.");
        if (s.Templates.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) e.Add("Shablon nomlari takrorlanmasin.");
        if (!TimeOnly.TryParseExact(s.YouTube.PublishTime, "HH:mm", out _)) e.Add("Public soati HH:mm formatida bo'lsin.");
        try { Core.Scheduling.TimeZones.Find(s.YouTube.TimeZoneId); } catch { e.Add("Vaqt zonasi noto'g'ri (masalan, Asia/Tashkent)."); }
        if (s.OpenFilm.RenderWorkers is < 1 or > 4) e.Add("Parallel renderer soni 1-4 bo'lsin.");
        return e;
    }

    [RelayCommand]
    private void Save()
    {
        S.VoiceProfiles = Voices.Where(v => !string.IsNullOrWhiteSpace(v.Name)).ToList();
        S.Templates = Templates.Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList();
        var errors = Validate(S);
        if (errors.Count > 0) { Message = "Saqlanmadi: " + string.Join(" ", errors); return; }
        // Bot boshqaradigan maydonlar (Gemini, Claude, YouTube, ovoz, render, limitlar) bot konfigiga ham yoziladi: aks holda keyingi
        // Load() bot qiymatini ustiga qo'yib, o'zgarish qaytib qolardi. Faqat o'zgargan maydonlar yoziladi.
        var before = _loaded ?? _svc.Settings.Load();
        _svc.Settings.Save(S);
        try { _svc.Settings.PushBotConfig(S, before); }
        catch (Exception ex) { Message = "Lokal saqlandi, lekin botga yozilmadi (internetni tekshiring): " + ex.Message; Load(); return; }
        if (ClientSecretInput.Length > 0) { _svc.Secrets.Set(SecretNames.YouTubeClientSecret, ClientSecretInput.Trim()); ClientSecretInput = ""; }
        var msg = "Saqlandi. Ishlayotgan/boshlangan vazifalar o'z snapshot'idagi sozlamalar bilan davom etadi.";
        Load();
        Message = msg;
    }

    // ------------------------------------------------------------------ ovoz katalogi
    private void ApplyCatalog(VoiceCatalog? c)
    {
        _allEngines = c?.Engines ?? new();
        FilterEngines();
        CatalogInfo = c is null
            ? "Modellar hali o'qilmagan. \"Kompyuterdagi modellarni o'qish\" tugmasini bosing."
            : $"VoiceStudio v{c.VoiceStudioVersion} dan o'qilgan: {c.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. " +
              $"Ishlatish mumkin: {_allEngines.Count(e => e.Usable)}, jami dvigatellar: {_allEngines.Count}.";
    }

    partial void OnShowAllEnginesChanged(bool value) => FilterEngines();

    private void FilterEngines()
    {
        var keep = SelectedEngine?.Id;
        Engines.Clear();
        foreach (var e in _allEngines.Where(e => ShowAllEngines || e.Usable)) Engines.Add(e);
        SelectedEngine = Engines.FirstOrDefault(e => e.Id == keep) ?? Engines.FirstOrDefault();
    }

    partial void OnSelectedEngineChanged(VoiceEngineInfo? value)
    {
        EngineVoices.Clear();
        if (value is null) return;
        foreach (var v in value.Voices) EngineVoices.Add(v);
        SelectedVoice = value.DefaultVoice ?? value.Voices.FirstOrDefault() ?? "";
        NewVoiceName = string.IsNullOrEmpty(SelectedVoice) ? value.Id : $"{value.Id} / {SelectedVoice}";
    }

    partial void OnSelectedVoiceChanged(string value)
    {
        if (SelectedEngine is not null) NewVoiceName = string.IsNullOrEmpty(value) ? SelectedEngine.Id : $"{SelectedEngine.Id} / {value}";
    }

    [RelayCommand]
    private async Task RefreshCatalog()
    {
        Busy = true;
        Message = "VoiceStudio'dan modellar o'qilmoqda… Yopiq bo'lsa vaqtincha ochiladi (≈2 daqiqa) va keyin yopiladi.";
        try
        {
            var vs = _svc.Settings.Load().VoiceStudio;
            var catalog = await Task.Run(() => new Infrastructure.Voice.VoiceCatalogService(_svc.LocalDb).FetchAsync(vs, _svc.Log, CancellationToken.None));
            ApplyCatalog(catalog);
            try { _svc.Settings.PublishCatalog(); } catch { } // ovoz ma'lumotlari online bazaga (bot ko'rsatadi)
            Message = $"Topildi: {catalog.Engines.Count(e => e.Usable)} ta ishlatish mumkin bo'lgan dvigatel.";
        }
        catch (Exception e) { Message = "Modellarni o'qib bo'lmadi: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void AddVoiceFromCatalog()
    {
        if (SelectedEngine is null) { Message = "Avval dvigatelni tanlang."; return; }
        if (!string.Equals(SelectedEngine.Id, TtsVoices.Model, StringComparison.OrdinalIgnoreCase)) { Message = $"Faqat {TtsVoices.DisplayName} ishlatiladi."; return; }
        if (!SelectedEngine.Usable) { Message = $"'{SelectedEngine.DisplayName}' bu kompyuterda ishlatib bo'lmaydi: {SelectedEngine.Reason ?? "modeli o'rnatilmagan"}"; return; }
        var name = string.IsNullOrWhiteSpace(NewVoiceName) ? SelectedEngine.Id : NewVoiceName.Trim();
        if (Voices.Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))) { Message = $"'{name}' nomli profil allaqachon bor."; return; }
        Voices.Add(new VoiceProfile { Name = name, Model = SelectedEngine.Id, Voice = SelectedVoice ?? "", Language = NewVoiceLanguage, Speed = 1.0 });
        if (string.IsNullOrEmpty(S.Defaults.VoiceProfile)) S.Defaults.VoiceProfile = name;
        Save();
        Message = $"Ovoz profili qo'shildi va saqlandi: {name}.";
    }

    [RelayCommand]
    private void SetDefaultVoice(VoiceProfile? v)
    {
        if (v is null) return;
        S.Defaults.VoiceProfile = v.Name;
        Save();
        Message = $"Standart ovoz: {v.Name}.";
    }

    // ------------------------------------------------------------------ shablonlar (lokal saqlanadi)
    [RelayCommand]
    private async Task ImportTemplateVideo()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Shablon videosini tanlang",
            Filter = "Video (*.mp4;*.mov;*.webm;*.mkv;*.m4v)|*.mp4;*.mov;*.webm;*.mkv;*.m4v",
        };
        if (dlg.ShowDialog() != true) return;
        await ImportTemplate(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName));
    }

    [RelayCommand]
    private async Task ImportTemplateFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "OpenFilm loyiha papkasini tanlang (ichida film.html)" };
        if (dlg.ShowDialog() != true) return;
        await ImportTemplate(dlg.FolderName, Path.GetFileName(dlg.FolderName.TrimEnd('\\')));
    }

    private async Task ImportTemplate(string source, string baseName)
    {
        var dir = S.Paths.TemplatesDir;
        var name = baseName; int n = 2;
        while (Templates.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} ({n++})";
        Busy = true; Message = $"Shablon nusxalanmoqda: {source} → {dir}";
        try
        {
            var film = _svc.FilmTool();
            var store = new Infrastructure.Templates.TemplateStore(_svc.Media(film));
            var def = await Task.Run(() => store.ImportAsync(dir, source, name, "", CancellationToken.None));
            Templates.Add(def);
            if (string.IsNullOrEmpty(S.Defaults.Template)) S.Defaults.Template = def.Name;
            Save();
            Message = $"Shablon saqlandi: '{def.Name}' ({def.SizeBytes / 1048576.0:0.0} MB) — {def.Path}. Asl fayl endi kerak emas.";
        }
        catch (Exception e) { Message = "Shablon yuklanmadi: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void SetDefaultTemplate(TemplateDefinition? t)
    {
        if (t is null) return;
        S.Defaults.Template = t.Name;
        Save();
        Message = $"Standart shablon: {t.Name}.";
    }

    [RelayCommand] private void AddVoice() => Voices.Add(new VoiceProfile { Name = "Yangi ovoz", Model = TtsVoices.Model, Voice = TtsVoices.FemaleRef, Language = "en" });
    [RelayCommand] private void RemoveVoice(VoiceProfile? v) { if (v is null) return; Voices.Remove(v); Save(); }

    [RelayCommand]
    private void RemoveTemplate(TemplateDefinition? t)
    {
        if (t is null) return;
        var inUse = _svc.Store.ListByStatus(JobStatus.Queued, JobStatus.Failed, JobStatus.NeedsAttention, JobStatus.Scripting, JobStatus.GeneratingVoice,
                JobStatus.Aligning, JobStatus.Editing, JobStatus.Rendering, JobStatus.Validating)
            .Count(j => string.Equals(j.TemplateName, t.Name, StringComparison.OrdinalIgnoreCase));
        var text = $"'{t.Name}' shabloni o'chirilsinmi?" + (t.Managed ? "\nIlovaning shablonlar papkasidagi nusxasi ham o'chiriladi." : "") +
                   (inUse > 0 ? $"\n\nDiqqat: {inUse} ta tugallanmagan vazifa shu shablonni ishlatadi — ular 'E'tibor kerak' holatiga tushadi." : "");
        if (System.Windows.MessageBox.Show(text, "YtAuto", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        try { Infrastructure.Templates.TemplateStore.DeleteFiles(S.Paths.TemplatesDir, t); }
        catch (Exception e) { Message = "Fayllarni o'chirib bo'lmadi: " + e.Message; }
        Templates.Remove(t);
        if (string.Equals(S.Defaults.Template, t.Name, StringComparison.OrdinalIgnoreCase)) S.Defaults.Template = "";
        Save();
    }

    [RelayCommand]
    private async Task ConnectYouTube()
    {
        Save();
        if (Message.StartsWith("Saqlanmadi")) return;
        Busy = true; Message = "Brauzerda Google ruxsat oynasi ochildi…";
        try
        {
            var title = await Task.Run(() => _svc.YouTubeAuth.ConnectInteractiveAsync(CancellationToken.None));
            var s = _svc.Settings.Load(); s.YouTube.ChannelTitle = title; _svc.Settings.Save(s);
            Load();
            Message = $"YouTube kanali ulandi: {title}";
        }
        catch (Exception e) { Message = "Ulanmadi: " + e.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void DisconnectYouTube()
    {
        if (System.Windows.MessageBox.Show("YouTube kanali uziladimi? Barcha kompyuterlarda (va botda) yuklash to'xtaydi; qayta ulash botdan qilinadi.",
                "YtAuto", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        _svc.YouTubeAuth.Disconnect();
        // Bot orqali ulangan (umumiy) token ham bazadan olib tashlanadi - aks holda ilova uni yana ishlatib qoladi.
        if (_svc.IsOnline) try { _svc.Store.SetSetting("youtube.oauth", ""); } catch (Exception e) { _svc.Log.Warn("Umumiy YouTube tokenini o'chirib bo'lmadi: " + e.Message); }
        var s = _svc.Settings.Load(); s.YouTube.ChannelTitle = ""; _svc.Settings.Save(s);
        Load();
        Message = "YouTube kanali uzildi.";
    }

    [RelayCommand]
    private async Task TestIntegrations()
    {
        Busy = true; Checks.Clear(); Message = "Tekshirilmoqda… (og'ir dasturlar ishga tushirilmaydi)";
        try
        {
            var progress = new Progress<CheckResult>(r => Checks.Add(r));
            var exe = Shell.FindWorkerExe();
            var deep = DeepClaudeCheck;
            await Task.Run(() => new IntegrationChecker(_svc).RunAsync(deep, exe, CancellationToken.None, progress));
            Message = Checks.Any(c => c.Level == CheckLevel.Failed) ? "Ba'zi integratsiyalar ishlamayapti - ro'yxatni ko'ring." : "Tekshiruv tugadi.";
        }
        catch (Exception e) { Message = "Tekshiruvda xato: " + e.Message; }
        finally { Busy = false; }
    }
}
