using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Gemini;

/// <summary>Gemini chaqiruvi turi: router modellar tartibini va qaysi model nimani qila olishini shunga qarab tanlaydi.</summary>
public enum GeminiTask
{
    /// <summary>Ssenariy - eng sifatli model kerak.</summary>
    Script,
    /// <summary>Google Search grounding (fakt varag'i).</summary>
    Grounded,
    /// <summary>Kichik ish (tavsif/taglar, rasm rejasi, moslik bali): avval Lite modellar - kunlik limiti katta (500).</summary>
    Light,
    /// <summary>Rasmlarni ko'rish (shablon uslubi).</summary>
    Vision,
    /// <summary>Videoni ko'rish (shablon harakati).</summary>
    Video,
    /// <summary>Videoni fps va past o'lcham sozlamalari bilan ko'rish (model qabul qilmasa - oddiy Video bilan qayta so'raladi).</summary>
    VideoLowRes,
}

/// <summary>429 javobidan: qaysi limit tugadi va qachon qayta urinish mumkin.</summary>
public sealed class GeminiRateLimitException : PipelineException
{
    /// <summary>Kunlik (RPD) limit tugadi - model Tinch okeani vaqti bilan yarim tungacha ishlatilmaydi.</summary>
    public bool PerDay { get; }
    /// <summary>Google aytgan kutish vaqti (RetryInfo.retryDelay), bo'lmasa null.</summary>
    public TimeSpan? RetryAfter { get; }
    public GeminiRateLimitException(string message, bool perDay, TimeSpan? retryAfter)
        : base(ErrorCategory.Transient, "gemini_rate_limit", message) { PerDay = perDay; RetryAfter = retryAfter; }

    /// <summary>Google 429 tanasi: error.details[] ichida QuotaFailure.violations[].quotaId ("...PerDay...") va RetryInfo.retryDelay ("37s").</summary>
    public static GeminiRateLimitException Parse(string raw, string message)
    {
        bool perDay = false; TimeSpan? retry = null;
        try
        {
            var details = JsonNode.Parse(raw)?["error"]?["details"] as JsonArray;
            foreach (var d in details ?? new JsonArray())
            {
                foreach (var v in (d?["violations"] as JsonArray) ?? new JsonArray())
                {
                    var id = (v?["quotaId"]?.GetValue<string>() ?? "") + " " + (v?["quotaMetric"]?.GetValue<string>() ?? "");
                    if (id.Contains("PerDay", StringComparison.OrdinalIgnoreCase) || id.Contains("per_day", StringComparison.OrdinalIgnoreCase)) perDay = true;
                }
                if (d?["retryDelay"]?.GetValue<string>() is { } rd && double.TryParse(rd.TrimEnd('s'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    retry = TimeSpan.FromSeconds(sec);
            }
        }
        catch { }
        if (!perDay && Regex.IsMatch(message, @"(?i)per[ _-]?day|daily|RPD")) perDay = true;
        if (retry is null && Regex.Match(message, @"(?i)retry in ([0-9.]+)\s*s") is { Success: true } m &&
            double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s2))
            retry = TimeSpan.FromSeconds(s2);
        return new GeminiRateLimitException(message, perDay, retry);
    }
}

/// <summary>Bitta modelning bugungi (Tinch okeani sanasi) sarfi va holati: botda egasiga ko'rsatiladi.</summary>
public sealed class GeminiModelUsage
{
    public string Model { get; set; } = "";
    public string Display { get; set; } = "";
    public int Requests { get; set; }
    public long Tokens { get; set; }
    /// <summary>Bugun nechta 429 olindi.</summary>
    public int RateLimited { get; set; }
    /// <summary>Oxirgi so'rovlar vaqti (unix s) - daqiqadagi so'rovlar (RPM) shundan hisoblanadi; 60 s dan eskisi tashlanadi.</summary>
    public List<long> Recent { get; set; } = new();
    /// <summary>Barcha kalitlarda kunlik limit tugagan bo'lsa - qachon tiklanadi.</summary>
    public DateTimeOffset? DayLimitedUntil { get; set; }
    public DateTimeOffset? MinuteLimitedUntil { get; set; }
    /// <summary>Model ishlamayapti (topilmadi / server xatosi) - sabab.</summary>
    public string Unavailable { get; set; } = "";
    public DateTimeOffset? LastUsedUtc { get; set; }
}

/// <summary>Sarf hujjati (shared_settings['gemini.usage.worker']): bir kun, zanjir tartibi va har model.</summary>
public sealed class GeminiUsageSnapshot
{
    public string Day { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Keys { get; set; }
    public List<string> Chain { get; set; } = new();
    public Dictionary<string, GeminiModelUsage> Models { get; set; } = new();
}

/// <summary>
/// Model router: sozlangan zanjirdagi modellarni (asosiy, keyin fallback'lar) tartib bilan sinaydi. Limit (429 - kunlik yoki daqiqalik),
/// mavjudlik (404, 5xx) va vazifa turi (model shu ishni qila olmasa - 400) bo'yicha keyingisiga o'tadi. Kalit+model juftligi limitga
/// tushsa sovutiladi: kunlik limit - Tinch okeani yarim tunigacha, daqiqalik - Google aytgan muddat (yoki 60 s). Holat faylga yoziladi:
/// worker qayta ishga tushsa ham limiti tugagan modelni qayta urmaydi. Kalitlar faylga ochiq yozilmaydi (sha256 boshi).
/// </summary>
public sealed class GeminiModelRouter
{
    /// <summary>Bepul kalitda matn yozadigan, limiti 0 bo'lmagan barcha modellar (Google AI Studio nomlari), sifat tartibida.</summary>
    public static readonly string[] DefaultChain =
    {
        "Gemini 3.8 Flash", "Gemini 3 Flash", "Gemini 3.5 Flash Lite", "Gemini 3.6 Flash", "Gemini 4 Hydrogen", "Gemini 2.5 Flash",
        "Gemini 3.1 Flash Lite", "Gemini 2.5 Flash Lite", "Gemma 4 31B", "Gemma 4 26B", "Gemini Robotics ER 2 Preview",
    };

    private sealed class State
    {
        public Dictionary<string, DateTimeOffset> Cooldown { get; set; } = new();
        public Dictionary<string, DateTimeOffset> Down { get; set; } = new();
        public Dictionary<string, string> DownReason { get; set; } = new();
        /// <summary>model|vazifa -> shu vaqtgacha bu vazifaga urinilmaydi (400: model buni qila olmaydi). Vaqtli: noto'g'ri so'rov tufayli abadiy o'chib qolmasin.</summary>
        public Dictionary<string, DateTimeOffset> Unsupported { get; set; } = new();
        public GeminiUsageSnapshot Usage { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string? _statePath;
    private State _s = new();
    private List<(string Id, string Display)>? _catalog;
    private DateTimeOffset _catalogAt;
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>Sarf o'zgarganda (har chaqiruvdan keyin): AppServices uni botga (egasining hisobiga) yozadi.</summary>
    public Action<GeminiUsageSnapshot>? UsageChanged { get; set; }
    /// <summary>false bo'lsa (qo'shimcha foydalanuvchi vazifasi, o'z kalitlari) limit holati yuritiladi, lekin egasining sarfiga qo'shilmaydi.</summary>
    public Func<bool> Counting { get; set; } = () => true;
    private bool Counts() { try { return Counting(); } catch { return true; } }

    public GeminiModelRouter(string? statePath = null)
    {
        _statePath = statePath;
        try { if (statePath is not null && File.Exists(statePath)) _s = JsonSerializer.Deserialize<State>(File.ReadAllText(statePath)) ?? new(); }
        catch { _s = new(); }
    }

    public static string KeyId(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];

    private static readonly TimeZoneInfo Pacific = FindPacific();
    private static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Gemini kunlik limiti Tinch okeani vaqti bilan yarim tunda yangilanadi.</summary>
    public static string PacificDay(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Pacific).ToString("yyyy-MM-dd");
    public static DateTimeOffset NextPacificMidnight(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, Pacific);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, Pacific.GetUtcOffset(midnight));
    }

    // ------------------------------------------------------------ model nomlari

    public bool CatalogFresh { get { lock (_gate) return _catalog is not null && Now() - _catalogAt < TimeSpan.FromHours(6); } }
    public void SetCatalog(IEnumerable<(string Id, string Display)> models)
    { lock (_gate) { _catalog = models.ToList(); _catalogAt = Now(); } }

    /// <summary>
    /// Sozlamadagi nom (AI Studio nomi "Gemini 3.8 Flash" yoki API id "gemini-3.8-flash") → API id. Ro'yxat bo'lsa: aniq id, keyin aniq
    /// displayName, keyin id + -preview/-latest/-sana qo'shimchasi (eng qisqasi). Ro'yxat bo'lmasa: nomdan id yasaladi.
    /// Topilmasa null (bu kalitda shunday model yo'q).
    /// </summary>
    public static string? Match(string name, IReadOnlyList<(string Id, string Display)>? catalog)
    {
        var n = (name ?? "").Trim();
        if (n.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        if (n.Length == 0) return null;
        var slug = Regex.Replace(n.ToLowerInvariant(), @"\s+", "-");
        if (catalog is null || catalog.Count == 0) return slug;
        var exact = catalog.FirstOrDefault(c => c.Id.Equals(n, StringComparison.OrdinalIgnoreCase) || c.Id.Equals(slug, StringComparison.OrdinalIgnoreCase));
        if (exact.Id is not null) return exact.Id;
        var byName = catalog.Where(c => c.Display.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Id.Length).FirstOrDefault();
        if (byName.Id is not null) return byName.Id;
        var rx = new Regex("^" + Regex.Escape(slug) + @"(-preview|-latest|-exp)?(-[0-9][0-9-]*)?$", RegexOptions.IgnoreCase);
        var variant = catalog.Where(c => rx.IsMatch(c.Id)).OrderBy(c => c.Id.Length).FirstOrDefault();
        return variant.Id;
    }

    /// <summary>Zanjir → mavjud API id'lar (takrorlarsiz, tartib saqlanadi) va topilmagan nomlar.</summary>
    public (List<(string Id, string Display)> Models, List<string> Missing) Resolve(IEnumerable<string> names)
    {
        List<(string Id, string Display)>? cat;
        lock (_gate) cat = _catalog;
        var list = new List<(string, string)>(); var missing = new List<string>();
        foreach (var name in names)
        {
            var id = Match(name, cat);
            if (id is null) { missing.Add(name.Trim()); continue; }
            if (list.Any(x => x.Item1.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            var display = cat?.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Display;
            list.Add((id, string.IsNullOrWhiteSpace(display) ? name.Trim() : display!));
        }
        return (list, missing);
    }

    /// <summary>Vazifa turiga qarab tartib: kichik ishlar (Light) avval Lite modellarda - asosiy Flash modellarning kunlik 20 so'rovi ssenariyga qoladi.</summary>
    public static List<(string Id, string Display)> Order(IReadOnlyList<(string Id, string Display)> chain, GeminiTask task)
    {
        if (task != GeminiTask.Light) return chain.ToList();
        static bool Lite(string s) => s.Contains("lite", StringComparison.OrdinalIgnoreCase);
        return chain.Where(m => Lite(m.Id) || Lite(m.Display)).Concat(chain.Where(m => !(Lite(m.Id) || Lite(m.Display)))).ToList();
    }

    // ------------------------------------------------------------ holat

    private static string Pair(string keyId, string model) => keyId + "|" + model.ToLowerInvariant();
    private static string TaskKey(string model, GeminiTask task) => model.ToLowerInvariant() + "|" + task;

    /// <summary>Shu model shu vazifani bajara oladimi va ishlayaptimi (kalitdan qat'i nazar). why - nega yo'q.</summary>
    public bool ModelUsable(string model, GeminiTask task, out string why)
    {
        lock (_gate)
        {
            if (_s.Unsupported.TryGetValue(TaskKey(model, task), out var no) && no > Now()) { why = $"{task} vazifasini qila olmaydi"; return false; }
            if (_s.Down.TryGetValue(model.ToLowerInvariant(), out var until) && until > Now())
            { why = _s.DownReason.GetValueOrDefault(model.ToLowerInvariant(), "ishlamayapti"); return false; }
            why = ""; return true;
        }
    }

    /// <summary>Kalit+model juftligi limitda emasmi.</summary>
    public bool PairReady(string keyId, string model, out DateTimeOffset until)
    {
        lock (_gate)
        {
            until = _s.Cooldown.GetValueOrDefault(Pair(keyId, model));
            return until <= Now();
        }
    }

    public void Success(string keyId, string model, string display, long tokens, int keys)
    {
        var counts = Counts();
        lock (_gate)
        {
            _s.Cooldown.Remove(Pair(keyId, model));
            if (counts)
            {
                var u = UsageFor(model, display, keys);
                u.Requests++; u.Tokens += tokens; u.LastUsedUtc = Now();
                u.Recent.Add(Now().ToUnixTimeSeconds());
                u.Unavailable = ""; u.MinuteLimitedUntil = null;
                Trim(u);
            }
        }
        Save(counts);
    }

    public void RateLimited(string keyId, string model, string display, GeminiRateLimitException e, IReadOnlyList<string> allKeyIds)
    {
        var counts = Counts();
        lock (_gate)
        {
            var until = e.PerDay ? NextPacificMidnight(Now()) : Now() + (e.RetryAfter is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(60));
            _s.Cooldown[Pair(keyId, model)] = until;
            if (!counts) goto done;
            var u = UsageFor(model, display, allKeyIds.Count);
            u.RateLimited++;
            // Barcha kalitlar shu modelda limitda bo'lsa - botda "limit tugadi" deb ko'rsatiladi.
            var all = allKeyIds.Select(k => _s.Cooldown.GetValueOrDefault(Pair(k, model))).ToList();
            if (all.All(t => t > Now()))
            {
                var max = all.Max();
                if (e.PerDay || max - Now() > TimeSpan.FromMinutes(10)) u.DayLimitedUntil = max; else u.MinuteLimitedUntil = max;
            }
        done:;
        }
        Save(counts);
    }

    public void MarkDown(string model, string display, TimeSpan forHow, string reason, int keys)
    {
        var counts = Counts();
        lock (_gate)
        {
            _s.Down[model.ToLowerInvariant()] = Now() + forHow;
            _s.DownReason[model.ToLowerInvariant()] = reason;
            if (counts) UsageFor(model, display, keys).Unavailable = reason;
        }
        Save(counts);
    }

    public void MarkUnsupported(string model, GeminiTask task)
    { lock (_gate) _s.Unsupported[TaskKey(model, task)] = Now() + TimeSpan.FromHours(12); Save(false); }

    /// <summary>Eng yaqin tiklanish vaqti (hamma limitda bo'lganda xabar uchun).</summary>
    public DateTimeOffset? EarliestReady(IEnumerable<string> keyIds, IEnumerable<string> models)
    {
        lock (_gate)
        {
            var times = keyIds.SelectMany(k => models.Select(m => _s.Cooldown.GetValueOrDefault(Pair(k, m)))).Where(t => t > Now()).ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }

    public void SetChain(IEnumerable<(string Id, string Display)> chain, int keys)
    {
        lock (_gate)
        {
            Roll();
            _s.Usage.Chain = chain.Select(c => c.Id).ToList();
            _s.Usage.Keys = keys;
            foreach (var (id, d) in chain) UsageFor(id, d, keys);
        }
    }

    public GeminiUsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            Roll();
            foreach (var u in _s.Usage.Models.Values) Trim(u);
            return JsonSerializer.Deserialize<GeminiUsageSnapshot>(JsonSerializer.Serialize(_s.Usage))!;
        }
    }

    private GeminiModelUsage UsageFor(string model, string display, int keys)
    {
        Roll();
        if (keys > 0) _s.Usage.Keys = keys;
        if (!_s.Usage.Models.TryGetValue(model, out var u)) _s.Usage.Models[model] = u = new GeminiModelUsage { Model = model };
        if (!string.IsNullOrWhiteSpace(display)) u.Display = display;
        return u;
    }

    /// <summary>Yangi Tinch okeani kuni: sarf nolga tushadi (kunlik limitlar ham yangilangan).</summary>
    private void Roll()
    {
        var day = PacificDay(Now());
        if (_s.Usage.Day == day) return;
        var chain = _s.Usage.Chain; var keys = _s.Usage.Keys;
        _s.Usage = new GeminiUsageSnapshot { Day = day, Chain = chain, Keys = keys };
        foreach (var k in _s.Cooldown.Where(kv => kv.Value <= Now()).Select(kv => kv.Key).ToList()) _s.Cooldown.Remove(k);
    }

    private void Trim(GeminiModelUsage u)
    {
        var cut = Now().AddSeconds(-60).ToUnixTimeSeconds();
        u.Recent.RemoveAll(t => t < cut);
        if (u.DayLimitedUntil <= Now()) u.DayLimitedUntil = null;
        if (u.MinuteLimitedUntil <= Now()) u.MinuteLimitedUntil = null;
    }

    private void Save(bool notify = true)
    {
        GeminiUsageSnapshot snap;
        lock (_gate)
        {
            _s.Usage.UpdatedAtUtc = Now();
            if (_statePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                    var tmp = _statePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(_s));
                    File.Move(tmp, _statePath, true);
                }
                catch { }
            }
        }
        if (!notify) return;
        snap = Snapshot();
        try { UsageChanged?.Invoke(snap); } catch { }
    }
}
