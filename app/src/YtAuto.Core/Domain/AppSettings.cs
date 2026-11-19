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
    /// <summary>Bot kanal tahlilidan tayyorlagan kontekst (online baza: shared_settings['channel.context']); job snapshot'iga tushadi.</summary>
    public ChannelContextSettings ChannelContext { get; set; } = new();
    /// <summary>Video sifati: shablon uslubi tahlili, ochiq manbalardan real rasm/ikonlar va shablonga moslik tekshiruvi.</summary>
    public VisualSettings Visuals { get; set; } = new();
    /// <summary>Hisobning o'z ovoz namunasi («Mening ovozim», bot: shared_settings['voice.clone']); job snapshot'iga tushadi.</summary>
    public VoiceCloneSettings VoiceClone { get; set; } = new();
    /// <summary>Fon musiqasi kutubxonasi (bot: shared_settings['music.library']); job snapshot'iga tushadi - qayta urinishda o'sha trek.</summary>
    public MusicSettings Music { get; set; } = new();

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
        s.VoiceProfiles.Add(TtsVoices.Female());
        s.VoiceProfiles.Add(TtsVoices.Male());
        s.Defaults.VoiceProfile = TtsVoices.FemaleName;
        s.Gemini.ScriptPrompt = DefaultPrompts.Script;
        s.Claude.MontagePrompt = DefaultPrompts.Montage;
        return s;
    }
}

/// <summary>
/// Kanal konteksti: bot ulangan YouTube kanalining oxirgi 3 videosini (montaj, ovoz, subtitr, ssenariy) o'rganib yozgan ixcham matnlar.
/// Script - Gemini ssenariy promptidagi {channel_context} o'rniga; Montage - Claude instructions.md dagi "Channel style" bo'limi.
/// </summary>
/// <summary>
/// «Mening ovozim»: hisob yuklagan ovoz namunasi. Model (MOSS-TTS-Nano) bitta; VoiceStudio'da <see cref="ProfileName"/> nomli klon
/// profil shu namuna bilan yaratiladi va shu nomli ovoz profilidagi vazifalar o'sha ovozda o'qiladi. Version - namuna almashganda o'zgaradi.
/// Deleted - foydalanuvchi namunani o'chirgan: kompyuterdagi nusxalar tozalanadi.
/// </summary>
public sealed class VoiceCloneSettings
{
    public string ProfileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Namunada o'qilgan matn (botdagi tayyor matn): VoiceStudio profiliga ref_text bo'lib beriladi.</summary>
    public string RefText { get; set; } = "";
    public bool Deleted { get; set; }

    public bool Active => !Deleted && ProfileName.Length > 0 && Url.Length > 0 && Version.Length > 0;
    public bool Matches(VoiceProfile? v) => Active && v is not null && string.Equals(v.Voice, ProfileName, StringComparison.OrdinalIgnoreCase);
}

public sealed class ChannelContextSettings
{
    public bool Enabled { get; set; }
    public string Script { get; set; } = "";
    public string Montage { get; set; } = "";
    public string VoiceProfile { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    public string ScriptBlock => Enabled ? Script.Trim() : "";
    public string MontageBlock => Enabled ? Montage.Trim() : "";
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
    /// <summary>
    /// Bot orqali online bazaga kiritilgan kalitlar (tartib bilan). Biri limitga (429) tushsa keyingisi ishlatiladi.
    /// Hech qachon saqlanmaydi/snapshotga tushmaydi (JsonIgnore).
    /// </summary>
    [JsonIgnore] public List<string> RemoteApiKeys { get; set; } = new();
    /// <summary>Mehmon hisobi vazifasi: faqat uning bot orqali kiritgan kalitlari (kompyuter egasining lokal kaliti ishlatilmaydi).</summary>
    [JsonIgnore] public bool RemoteOnly { get; set; }
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

/// <summary>
/// Fon musiqasi: botda Openverse'dan tanlangan CC0 / CC BY treklar (server Storage'ga yuklagan). Har videoga navbat bilan bittasi
/// alohida audio trekda, past ovozda qo'yiladi; muallif YouTube tavsifiga yoziladi (CC BY talabi).
/// </summary>
public sealed class MusicSettings
{
    public bool Enabled { get; set; }
    /// <summary>Musiqa ovozi (0.03–0.2): nutq aniq eshitilishi uchun past.</summary>
    public double Volume { get; set; } = 0.12;
    public List<MusicTrack> Tracks { get; set; } = new();

    /// <summary>Vazifa uchun trek: vazifa raqami bo'yicha navbat bilan (ketma-ket videolar turli musiqa oladi). Yo'q bo'lsa - null.</summary>
    public MusicTrack? PickFor(long jobId) => !Enabled || Tracks.Count == 0 ? null : Tracks[(int)(Math.Abs(jobId) % Tracks.Count)];
}

public sealed class MusicTrack
{
    /// <summary>Openverse identifikatori (GUID).</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Creator { get; set; } = "";
    /// <summary>"cc0" yoki "by".</summary>
    public string License { get; set; } = "";
    public string LicenseVersion { get; set; } = "";
    public string LicenseUrl { get; set; } = "";
    /// <summary>Manba sahifasi (masalan jamendo.com/track/…): tavsifdagi muallif qatorida.</summary>
    public string LandingUrl { get; set; } = "";
    public int DurationSec { get; set; }
    /// <summary>Server (Supabase Storage) nusxasini yuklab olish havolasi.</summary>
    public string Url { get; set; } = "";

    public string LicenseLabel => License == "cc0" ? "CC0" : $"CC {License.ToUpperInvariant()}{(LicenseVersion.Length > 0 ? " " + LicenseVersion : "")}";

    /// <summary>YouTube tavsifi uchun muallif qatori.</summary>
    public string Credit()
    {
        var by = string.IsNullOrWhiteSpace(Creator) ? "" : $" by {Creator.Trim()}";
        var lic = LicenseUrl.Length > 0 ? $"{LicenseLabel} ({LicenseUrl})" : LicenseLabel;
        return $"Music: \"{Title.Trim()}\"{by}, licensed under {lic}" + (LandingUrl.Length > 0 ? $" - {LandingUrl}" : "");
    }
}

/// <summary>Video sifati sozlamalari (bot: config.visuals).</summary>
public sealed class VisualSettings
{
    /// <summary>
    /// Bepul manbalardan real ikon, logo va fotolar qo'shiladi: fotolar avval sifatli stok manbalardan (Pexels, Pixabay -
    /// kalit kiritilgan bo'lsa), topilmasa Openverse va Wikimedia Commons'dan; ikon va logolar Iconify'dan.
    /// </summary>
    public bool RealMedia { get; set; } = true;
    /// <summary>Pexels API kaliti (bepul, bot: config.visuals). Saqlanmaydi/snapshotga tushmaydi (JsonIgnore).</summary>
    [JsonIgnore] public string PexelsApiKey { get; set; } = "";
    /// <summary>Pixabay API kaliti (bepul, bot: config.visuals). Saqlanmaydi/snapshotga tushmaydi (JsonIgnore).</summary>
    [JsonIgnore] public string PixabayApiKey { get; set; } = "";
    /// <summary>Bitta videoga ko'pi bilan nechta rasm/ikon.</summary>
    public int MaxMedia { get; set; } = 16;
    /// <summary>Shablon kadrlaridan Gemini dizayn qo'llanmasi (STYLE_GUIDE.md) yaratiladi va Claude'ga beriladi.</summary>
    public bool TemplateStyleGuide { get; set; } = true;
    /// <summary>Shablon videosining harakati (animatsiya, o'tishlar, temp) Gemini'ga video sifatida beriladi - MOTION_GUIDE.md. Shablon uchun bir marta.</summary>
    public bool TemplateMotionGuide { get; set; } = true;
    /// <summary>Render'dan keyin kadrlar shablon bilan solishtiriladi; ball past bo'lsa Claude tuzatadi.</summary>
    public bool StyleReview { get; set; } = true;
    /// <summary>Shundan past ball (0-10) - tuzatishga qaytariladi.</summary>
    public double MinStyleScore { get; set; } = 6.5;
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
    /// <summary>Mehmon hisobi vazifasi: faqat uning bot orqali ulagan kanali (egasining lokal tokeni bilan yuklanmaydi va u o'zgartirilmaydi).</summary>
    [JsonIgnore] public bool RemoteOnly { get; set; }
    /// <summary>Standart public soati "HH:mm".</summary>
    public string PublishTime { get; set; } = "18:00";
    public string TimeZoneId { get; set; } = "Asia/Tashkent";
    public string CategoryId { get; set; } = "27";
    /// <summary>Kategoriyalar ro'yxati qaysi mamlakat uchun (videoCategories.list regionCode).</summary>
    public string RegionCode { get; set; } = "UZ";
    public bool MadeForKids { get; set; }
    /// <summary>YouTube "AI (sintetik) kontent" belgisi. Standart o'chiq; kerak bo'lsa botda yoqiladi.</summary>
    public bool ContainsSyntheticMedia { get; set; }
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
    public string Model { get; set; } = TtsVoices.Model;
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
