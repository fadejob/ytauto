using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Claude;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Gemini;
using YtAuto.Infrastructure.Media;
using YtAuto.Infrastructure.OpenFilm;
using YtAuto.Infrastructure.Security;
using YtAuto.Infrastructure.Voice;
using YtAuto.Infrastructure.YouTube;

namespace YtAuto.Infrastructure;

/// <summary>UI va worker uchun umumiy kompozitsiya (bir xil Core/Infrastructure kodi).</summary>
public sealed class AppServices
{
    public AppPaths Paths { get; }
    /// <summary>Mavzular va ularning holati: online PostgreSQL (ulangan bo'lsa) yoki lokal SQLite.</summary>
    public IAppStore Store { get; }
    /// <summary>Kompyuterga xos ma'lumotlar (lokal sozlamalar, VoiceStudio katalogi keshi).</summary>
    public SqliteJobStore LocalDb { get; }
    public SqliteSettingsStore Settings { get; }
    public bool IsOnline => Store is PostgresJobStore;
    /// <summary>Hisob sozlangan (ro'yxatdan o'tilgan) va online baza ulangan.</summary>
    public bool IsAccountReady => IsOnline && Accounts.DeviceAccount.Load(LocalDb) is not null;
    public DpapiSecretStore Secrets { get; }
    public HttpClient Http { get; }
    public YouTubeAuth YouTubeAuth { get; }
    public YouTubeUploader Uploader { get; }
    public GeminiScriptGenerator Gemini { get; }
    public ResourceMonitor Resources { get; } = new();
    public RunLog Log { get; }
    public string RunId { get; }

    public AppServices(string component, string? dataRoot = null, bool console = false)
    {
        Paths = new AppPaths(dataRoot);
        var machine = new string(Environment.MachineName.Where(char.IsLetterOrDigit).ToArray());
        RunId = $"{component}-{machine}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}";
        Log = new RunLog(new RollingFileLog(Path.Combine(Paths.LogsDir, $"{component}.log")), console);
        Secrets = new DpapiSecretStore(Paths.SecretsDir);
        LocalDb = new SqliteJobStore(Paths.DatabasePath);
        // Online baza: shu kompyuterning o'z hisobi (qurilma roli) - baza unga faqat o'z ma'lumotlarini ko'rsatadi.
        // Sozlash (hisob yaratish) hali qilinmagan bo'lsa - lokal SQLite; ilova sozlash oynasini, worker esa hech narsa qilmaydi.
        // Self-test (alohida dataRoot) online bazani ishlatmaydi.
        var pg = dataRoot is null ? Secrets.Get(SecretNames.DeviceConnection) : null;
        if (!string.IsNullOrWhiteSpace(pg))
        {
            try
            {
                var remote = new PostgresJobStore(pg);
                var moved = remote.ImportFrom(LocalDb);
                if (moved > 0) Log.Info($"{moved} ta lokal mavzu online bazaga ko'chirildi.");
                Store = remote;
            }
            catch (Exception e)
            {
                Log.Error("Online bazaga (PostgreSQL) ulanib bo'lmadi: " + e.Message);
                throw new InvalidOperationException("Online bazaga ulanib bo'lmadi (internetni tekshiring): " + e.Message.Split('\n')[0], e);
            }
        }
        else Store = LocalDb;
        Settings = new SqliteSettingsStore(LocalDb, DefaultSettings, Store is PostgresJobStore ? Store : null);
        Http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) };
        YouTubeAuth = new YouTubeAuth(Secrets, () => Settings.Load().YouTube, Http);
        Uploader = new YouTubeUploader(YouTubeAuth, Http);
        Gemini = new GeminiScriptGenerator(Secrets) { RemoteKeys = () => Settings.Load().Gemini.RemoteApiKeys, LocalKeyAllowed = () => !Store.InGuestScope };
        // Ma'lum sirlarni log redaktoriga oldindan ro'yxatga olamiz.
        Secrets.Get(SecretNames.GeminiApiKey); Secrets.Get(SecretNames.YouTubeClientSecret); Secrets.Get(SecretNames.YouTubeTokens);
        Secrets.Get(SecretNames.AccountToken); Secrets.Get(SecretNames.DeviceConnection);
    }

    /// <summary>Gemini kaliti bormi (botda kiritilgan yoki lokal). Yo'q bo'lsa mavzular muzlatiladi: worker video tayyorlamaydi.</summary>
    public bool HasGeminiKey()
    {
        try { return Settings.Load().Gemini.RemoteApiKeys.Count > 0 || (!Store.InGuestScope && !string.IsNullOrWhiteSpace(Secrets.Get(SecretNames.GeminiApiKey))); }
        catch { return false; }
    }

    public static AppSettings DefaultSettings()
    {
        var s = AppSettings.CreateDefault(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        s.Claude.ExecutablePath = YtAuto.Infrastructure.Claude.ClaudeLocator.Find() ?? s.Claude.ExecutablePath;
        // Tizim diskida joy kam bo'lsa (sinovdagi kompyuterda C: da ~2.7 GB), ish va arxiv papkalari eng ko'p bo'sh joyli
        // mahalliy diskka qo'yiladi. Foydalanuvchi Sozlamalarda o'zgartirishi mumkin.
        try
        {
            var sys = new DriveInfo(Path.GetPathRoot(s.Paths.WorkDir)!);
            var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
            if (best is not null && sys.AvailableFreeSpace < 20L * 1024 * 1024 * 1024 && best.AvailableFreeSpace > sys.AvailableFreeSpace * 2
                && !string.Equals(best.Name, sys.Name, StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.Combine(best.RootDirectory.FullName, "YtAuto");
                s.Paths.WorkDir = Path.Combine(root, "jobs");
                s.Paths.VideoArchiveDir = Path.Combine(root, "archive", "video");
                s.Paths.AudioArchiveDir = Path.Combine(root, "archive", "audio");
                s.Paths.TemplatesDir = Path.Combine(root, "templates");
            }
        }
        catch { }
        return s;
    }

    public OpenFilmTool FilmTool(Func<OpenFilmSettings>? s = null) => new(s ?? (() => Settings.Load().OpenFilm), Store, RunId);
    public FfmpegMediaProbe Media(OpenFilmTool film) => new(() => film.FfmpegDir);
    public WhisperAligner Aligner(Func<VoiceStudioSettings>? s = null) => new(s ?? (() => Settings.Load().VoiceStudio));
    public ClaudeEditorAgent Claude(Func<ClaudeSettings>? s = null) => new(s ?? (() => Settings.Load().Claude), Store, RunId);

    /// <summary>
    /// Pipeline servislarini yaratadi. Tashqi vositalar (Claude, OpenFilm, Python) yo'llari JORIY sozlamadan olinadi,
    /// prompt/shablon/limitlar esa vazifa snapshot'idan (JobRunner ichida).
    /// </summary>
    public PipelineServices Pipeline(bool stopBeforeUpload = false, IScriptGenerator? scriptOverride = null, TimeSpan? postUploadPoll = null)
    {
        var film = FilmTool();
        var media = Media(film);
        return new PipelineServices
        {
            Store = Store, Settings = Settings, Secrets = Secrets,
            ScriptGenerator = scriptOverride ?? Gemini,
            Tts = new VoiceStudioTtsService(Store, RunId),
            AudioImporter = new RemoteAudioImporter(Http, media),
            Aligner = Aligner(), Editor = Claude(), Film = film, Media = media,
            // Video sifati: Gemini vision (shablon uslubi, rasm rejasi, moslik) va ochiq manbalardan real rasmlar.
            Visuals = new Gemini.GeminiVisualDirector(Gemini, () => Settings.Load().Gemini.Model),
            MediaLibrary = new Media.OpenMediaLibrary(keys: () => { var v = Settings.Load().Visuals; return new Media.StockPhotoKeys(v.PexelsApiKey, v.PixabayApiKey); }),
            // Stok rasm va video parchalari: OpenFilm o'zi qidiradi (alohida OPENFILM_HOME'li Studio, kalitlar faqat uning muhitida).
            Stock = new OpenFilm.OpenFilmStockSearch(film, () => { var v = Settings.Load().Visuals; return new Media.StockPhotoKeys(v.PexelsApiKey, v.PixabayApiKey); },
                Path.Combine(Paths.ToolsDir, "openfilm-stock-home")),
            Uploader = Uploader, Resources = Resources, Paths = Paths, Clock = new SystemClock(),
            JobLogFactory = id => new JobLog(id, Paths.RecordDirFor(id), Store, Log),
            RunLog = Log, AppVersion = typeof(AppServices).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            StopBeforeUpload = stopBeforeUpload,
            PostUploadPoll = postUploadPoll ?? TimeSpan.FromMinutes(8),
            // Video public bo'ldi: bot kanal kontekstini (oxirgi 3 video) qayta o'rganadi. Joriy hisob doirasida yoziladi.
            OnPublished = _ => { if (IsOnline) Store.SetSetting(Data.SqliteSettingsStore.ChannelStaleKey, DateTimeOffset.UtcNow.ToString("o")); },
            // «Mening ovozim» natijasi botga (joriy hisob - vazifa egasi doirasida).
            OnVoiceCloneStatus = (version, status, message) => { if (IsOnline) Data.SqliteSettingsStore.WriteVoiceCloneStatus(Store, version, status, message); },
        };
    }
}
