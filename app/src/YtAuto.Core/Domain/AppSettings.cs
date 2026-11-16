using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtAuto.Core.Domain;

/// <summary>
/// Ilova sozlamalari. Sirlar (API kalit, OAuth token, client secret) bu yerda SAQLANMAYDI:
/// ular DPAPI bilan himoyalangan secret store'da. Shu sabab snapshot'ga ham tushmaydi.
/// </summary>
public sealed class AppSettings
{
    public ScheduleSettings Schedule { get; set; } = new();
    public GeminiSettings Gemini { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    public VoiceStudioSettings VoiceStudio { get; set; } = new();
    public OpenFilmSettings OpenFilm { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
    public YouTubeSettings YouTube { get; set; } = new();
    public DefaultJobSettings Defaults { get; set; } = new();
    public LimitSettings Limits { get; set; } = new();
    public List<VoiceProfile> VoiceProfiles { get; set; } = new();
    public List<TemplateDefinition> Templates { get; set; } = new();

    public VoiceProfile? FindVoice(string name) =>
        VoiceProfiles.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? VoiceProfiles.FirstOrDefault();

    public TemplateDefinition? FindTemplate(string name) =>
        Templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AppSettings FromJson(string json) => JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
    public AppSettings Clone() => FromJson(ToJson());

    public static AppSettings CreateDefault(string localAppData, string roamingAppData)
    {
        var s = new AppSettings();
        s.Paths.WorkDir = Path.Combine(localAppData, "YtAuto", "jobs");
        s.Paths.VideoArchiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YtAuto", "video");
        s.Paths.AudioArchiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YtAuto", "audio");
        s.Paths.TemplatesDir = Path.Combine(localAppData, "YtAuto", "templates");
        s.Claude.ExecutablePath = Path.Combine(roamingAppData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        s.VoiceStudio.ExePath = @"D:\AppData\VoiceStudio\VoiceStudio.exe";
        s.VoiceStudio.PythonPath = Path.Combine(roamingAppData, "VoiceStudio", "runtime", "project", ".venv", "Scripts", "python.exe");
        s.VoiceStudio.HfCacheDir = Path.Combine(localAppData, "OmniVoice", "hf_cache");
        s.OpenFilm.ExePath = @"D:\AppData\Local\OpenFilm\OpenFilm.exe";
        s.VoiceProfiles.Add(new VoiceProfile { Name = "Kitten EN ayol", Model = "kittentts", Voice = "expr-voice-2-f", Language = "en" });
        s.Defaults.VoiceProfile = "Kitten EN ayol";
        s.Gemini.ScriptPrompt = DefaultPrompts.Script;
        s.Claude.MontagePrompt = DefaultPrompts.Montage;
        return s;
    }
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    /// <summary>"HH:mm" formatida, <see cref="TimeZoneId"/> bo'yicha.</summary>
    public string StartTime { get; set; } = "01:00";
    /// <summary>Ish oynasi tugashi "HH:mm" (standart 04:00). Shundan keyin yangi video boshlanmaydi; boshlangan oxirgi ish tugaguncha to'xtatilmaydi.</summary>
    public string EndTime { get; set; } = "04:00";
    public string TimeZoneId { get; set; } = "Asia/Tashkent";
    public bool WakeToRun { get; set; } = true;
    public int ExecutionLimitHours { get; set; } = 6;
}

public sealed class GeminiSettings
{
    public string Model { get; set; } = "gemini-2.5-flash";
    public string ScriptPrompt { get; set; } = "";
    /// <summary>Hozirgi faktlar uchun Google Search grounding bilan alohida fakt varag'i olinadi.</summary>
    public bool UseSearchGrounding { get; set; } = true;
    public int TimeoutSec { get; set; } = 180;
    public double Temperature { get; set; } = 0.7;
    /// <summary>Bot orqali online bazaga kiritilgan kalit. Hech qachon saqlanmaydi/snapshotga tushmaydi (JsonIgnore).</summary>
    [JsonIgnore] public string RemoteApiKey { get; set; } = "";
}

public sealed class ClaudeSettings
{
    public string ExecutablePath { get; set; } = "";
    public string MontagePrompt { get; set; } = "";
    /// <summary>Bo'sh bo'lsa Claude Code standart modeli.</summary>
    public string Model { get; set; } = "";
    public int TimeoutMinutes { get; set; } = 45;
    /// <summary>0 - cheklanmagan.</summary>
    public double MaxBudgetUsd { get; set; }
}

public sealed class VoiceStudioSettings
{
    public string ExePath { get; set; } = "";
    public string BaseUrl { get; set; } = "http://127.0.0.1:3900";
    public int StartupTimeoutSec { get; set; } = 300;
    public int RequestTimeoutSec { get; set; } = 900;
    /// <summary>Bitta TTS so'roviga yuboriladigan matn bo'lagi (belgilar).</summary>
    public int ChunkChars { get; set; } = 450;
    public double PauseBetweenChunksSec { get; set; } = 0.25;
    public string PythonPath { get; set; } = "";
    public string HfCacheDir { get; set; } = "";
    public string WhisperModel { get; set; } = "Systran/faster-whisper-base";
    public int AlignTimeoutSec { get; set; } = 1200;
}

public sealed class OpenFilmSettings
{
    public string ExePath { get; set; } = "";
    /// <summary>Parallel renderer soni. ~8 GB RAM uchun 1-2 dan oshirmang.</summary>
    public int RenderWorkers { get; set; } = 1;
    public int Fps { get; set; } = 30;
    public double LoudnessLufs { get; set; } = -14;
    public bool BurnSubtitles { get; set; } = true;
    public bool LookFullAfterRender { get; set; } = true;
    public int RenderTimeoutMinutes { get; set; } = 90;
}

public sealed class PathSettings
{
    public string WorkDir { get; set; } = "";
    public string VideoArchiveDir { get; set; } = "";
    public string AudioArchiveDir { get; set; } = "";
    /// <summary>Yuklangan shablonlar (video yoki OpenFilm loyiha) shu lokal papkada nusxa sifatida saqlanadi.</summary>
    public string TemplatesDir { get; set; } = "";
}

public sealed class YouTubeSettings
{
    public string ClientId { get; set; } = "";
    /// <summary>Bot orqali online bazaga kiritilgan OAuth client secret. Saqlanmaydi/snapshotga tushmaydi (JsonIgnore).</summary>
    [JsonIgnore] public string RemoteClientSecret { get; set; } = "";
    /// <summary>Har videoning (Gemini yozgan) tavsifi oxiriga qo'shiladigan standart matn: kanal havolalari, ogohlantirish va h.k.</summary>
    public string DefaultDescription { get; set; } = "";
    /// <summary>Bot orqali ulangan kanalning refresh token'i (online baza). Saqlanmaydi/snapshotga tushmaydi (JsonIgnore).</summary>
    [JsonIgnore] public string RemoteRefreshToken { get; set; } = "";
    /// <summary>Standart public soati "HH:mm".</summary>
    public string PublishTime { get; set; } = "18:00";
    public string TimeZoneId { get; set; } = "Asia/Tashkent";
    public string CategoryId { get; set; } = "27";
    /// <summary>Kategoriyalar ro'yxati qaysi mamlakat uchun (videoCategories.list regionCode).</summary>
    public string RegionCode { get; set; } = "UZ";
    public bool MadeForKids { get; set; }
    public bool ContainsSyntheticMedia { get; set; } = true;
    /// <summary>"youtube" (standart YouTube litsenziyasi) yoki "creativeCommon" (CC BY).</summary>
    public string License { get; set; } = "youtube";
    public bool Embeddable { get; set; } = true;
    public bool PublicStatsViewable { get; set; } = true;
    public bool NotifySubscribers { get; set; } = true;
    /// <summary>recordingDetails.recordingDate = video tayyorlangan sana.</summary>
    public bool SetRecordingDate { get; set; }
    /// <summary>recordingDetails.locationDescription (bo'sh - yuborilmaydi).</summary>
    public string LocationDescription { get; set; } = "";
    /// <summary>YouTube'dan o'qilgan kategoriyalar keshi (id → nom).</summary>
    public List<CategoryItem> CategoryCache { get; set; } = new();
    public string DefaultTags { get; set; } = "";
    public string ChannelTitle { get; set; } = "";
    /// <summary>Public vaqtidan kamida shuncha daqiqa oldin yuklash tugashi kerak.</summary>
    public int MinLeadMinutes { get; set; } = 30;
}

public sealed class DefaultJobSettings
{
    public string Language { get; set; } = "en";
    /// <summary>Eskirgan: davomiylik endi dinamik (ovoz uzunligi bilan belgilanadi). Moslik uchun qoldirilgan.</summary>
    public int TargetDurationSec { get; set; }
    public VideoFormat Format { get; set; } = VideoFormat.Landscape16x9;
    public string Template { get; set; } = "";
    public string VoiceProfile { get; set; } = "";
}

public sealed class LimitSettings
{
    public int MaxRunAttempts { get; set; } = 3;
    public int MaxQaFixCycles { get; set; } = 2;
    public int MaxTransientRetries { get; set; } = 3;
    public double MinFreeDiskGb { get; set; } = 5;
    public double MinFreeRamGbForTts { get; set; } = 1.0;
}

public sealed class VoiceProfile
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "kittentts";
    public string Voice { get; set; } = "";
    public string Language { get; set; } = "en";
    public double Speed { get; set; } = 1.0;
}

public sealed class TemplateDefinition
{
    public string Name { get; set; } = "";
    public TemplateKind Kind { get; set; }
    /// <summary>MP4 reference fayli yoki OpenFilm loyiha papkasi.</summary>
    public string Path { get; set; } = "";
    /// <summary>Ixtiyoriy: MP4 uslub namunasi (loyiha papkasiga qo'shimcha).</summary>
    public string ReferenceMp4 { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Shablon kartasi uchun kichik rasm (yuklashda videodan ajratiladi).</summary>
    public string Thumbnail { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset? AddedAtUtc { get; set; }
    /// <summary>true - fayl ilovaning shablonlar papkasiga nusxalangan (o'chirishda shu nusxa o'chiriladi).</summary>
    public bool Managed { get; set; }
}

public sealed class CategoryItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public override string ToString() => Title;
}

/// <summary>VoiceStudio'dan o'qilgan TTS dvigateli (kompyuterda bor modellar ro'yxati uchun).</summary>
public sealed class VoiceEngineInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Available { get; set; }
    public string? Reason { get; set; }
    public List<string> Voices { get; set; } = new();
    public string? DefaultVoice { get; set; }
    public bool SupportsCloning { get; set; }
    public List<string> InstalledModels { get; set; } = new();
    public List<string> KnownModels { get; set; } = new();
    public bool Active { get; set; }
    public bool Usable => Available && (InstalledModels.Count > 0 || KnownModels.Count == 0);
}

public sealed class VoiceCatalog
{
    public DateTimeOffset FetchedAtUtc { get; set; }
    public string VoiceStudioVersion { get; set; } = "";
    public List<VoiceEngineInfo> Engines { get; set; } = new();
}

/// <summary>Vazifa boshlanganda saqlanadigan snapshot.</summary>
public sealed class SettingsSnapshot
{
    public DateTimeOffset TakenAtUtc { get; set; }
    public AppSettings Settings { get; set; } = new();
    public string TemplateVersion { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public Dictionary<string, string> IntegrationVersions { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, AppSettings.Json);
    public static SettingsSnapshot FromJson(string json) => JsonSerializer.Deserialize<SettingsSnapshot>(json, AppSettings.Json)!;
}
