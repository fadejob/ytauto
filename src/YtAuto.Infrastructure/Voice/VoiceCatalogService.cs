using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;

namespace YtAuto.Infrastructure.Voice;

/// <summary>
/// Kompyuterdagi VoiceStudio TTS dvigatellari, o'rnatilgan modellari va tayyor ovozlarini o'qiydi
/// (VoiceStudio 0.5.7 route'lari: GET /engines/tts, GET /models). VoiceStudio yopiq bo'lsa worker kabi vaqtincha
/// ishga tushiradi va o'qib bo'lgach yopadi; foydalanuvchi ochgan nusxaga tegmaydi. Oxirgi natija bazada keshlanadi.
/// </summary>
public sealed class VoiceCatalogService
{
    private const string CacheKey = "voice_catalog";
    private readonly SqliteJobStore _store;
    public VoiceCatalogService(SqliteJobStore store) => _store = store;

    public VoiceCatalog? Cached()
    {
        var json = _store.GetSetting(CacheKey);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<VoiceCatalog>(json); } catch { return null; }
    }

    public async Task<VoiceCatalog> FetchAsync(VoiceStudioSettings settings, IJobLog log, CancellationToken ct)
    {
        await using var session = (VoiceStudioSession)await new VoiceStudioTtsService(_store, "catalog").OpenAsync(settings, log, ct).ConfigureAwait(false);
        // Birinchi chaqiruvda VoiceStudio har dvigatelni tekshiradi (sinovda 60 s dan oshdi): uzunroq kutiladi.
        var engines = await session.GetStringAsync("engines/tts", TimeSpan.FromMinutes(5), ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("VoiceStudio /engines/tts javob bermadi (versiya mos emas bo'lishi mumkin).");
        var models = await session.GetStringAsync("models", TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        var catalog = Parse(engines, models);
        catalog.VoiceStudioVersion = session.Version;
        _store.SetSetting(CacheKey, JsonSerializer.Serialize(catalog));
        return catalog; // session dispose: worker ochgan bo'lsa VoiceStudio yopiladi
    }

    /// <summary>/engines/tts va /models javoblarini birlashtiradi (test qilinadi).</summary>
    public static VoiceCatalog Parse(string enginesJson, string? modelsJson)
    {
        var eng = JsonNode.Parse(enginesJson)!;
        var tts = eng["backends"] is not null ? eng : eng["tts"] ?? eng;
        var active = tts["active"]?.GetValue<string>();
        var modelMap = new Dictionary<string, List<(string Repo, bool Installed)>>(StringComparer.OrdinalIgnoreCase);
        if (modelsJson is not null)
        {
            foreach (var m in JsonNode.Parse(modelsJson)?["models"] as JsonArray ?? new JsonArray())
            {
                if (!string.Equals(m?["role"]?.GetValue<string>(), "TTS", StringComparison.OrdinalIgnoreCase)) continue;
                var repo = m!["repo_id"]?.GetValue<string>() ?? "";
                var installed = m["installed"]?.GetValue<bool>() == true;
                foreach (var e in m["engines"] as JsonArray ?? new JsonArray())
                {
                    var id = e?.GetValue<string>() ?? "";
                    if (!modelMap.TryGetValue(id, out var list)) modelMap[id] = list = new();
                    list.Add((repo, installed));
                }
            }
        }
        var catalog = new VoiceCatalog { FetchedAtUtc = DateTimeOffset.UtcNow };
        foreach (var b in tts["backends"] as JsonArray ?? new JsonArray())
        {
            if (b is null) continue;
            var id = b["id"]?.GetValue<string>() ?? "";
            var known = modelMap.GetValueOrDefault(id) ?? new();
            catalog.Engines.Add(new VoiceEngineInfo
            {
                Id = id,
                DisplayName = b["display_name"]?.GetValue<string>() ?? id,
                Available = b["available"]?.GetValue<bool>() == true,
                Reason = b["reason"]?.GetValue<string>(),
                Voices = (b["preset_voices"] as JsonArray ?? new JsonArray()).Select(v => v?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList(),
                DefaultVoice = b["default_voice"]?.GetValue<string>(),
                SupportsCloning = b["supports_cloning"]?.GetValue<bool>() == true,
                InstalledModels = known.Where(k => k.Installed).Select(k => k.Repo).ToList(),
                KnownModels = known.Select(k => k.Repo).ToList(),
                Active = string.Equals(id, active, StringComparison.OrdinalIgnoreCase),
            });
        }
        // Ishlatish mumkin bo'lganlar birinchi.
        catalog.Engines = catalog.Engines.OrderByDescending(e => e.Usable).ThenByDescending(e => e.Available).ThenBy(e => e.DisplayName).ToList();
        return catalog;
    }
}
