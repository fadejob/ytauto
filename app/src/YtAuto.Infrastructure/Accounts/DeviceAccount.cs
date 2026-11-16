using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using YtAuto.Core.Pipeline;
using YtAuto.Infrastructure.Data;
using YtAuto.Infrastructure.Security;

namespace YtAuto.Infrastructure.Accounts;

/// <summary>Shu kompyuter ulangan hisob (sirlarsiz; lokal bazada saqlanadi).</summary>
public sealed class AccountState
{
    public Guid AccountId { get; set; }
    public string DeviceName { get; set; } = "";
    public string BotUrl { get; set; } = "";
    /// <summary>Telegram bot username (@siz), "Botni ochish" havolasi uchun. Bot bermasa bo'sh.</summary>
    public string BotUsername { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Ko'p foydalanuvchili rejimda shu kompyuterning hisobi:
/// <list type="bullet">
/// <item>Ro'yxatdan o'tish: token va baza paroli SHU YERDA yaratiladi. Botga faqat token XESHI va parol yuboriladi; bot hisob va
/// shu kompyuter uchun alohida Postgres rolini yaratib, ulanish ma'lumotini qaytaradi. Token o'zi tarmoqqa chiqmaydi.</item>
/// <item>Ilova va worker faqat shu rol bilan ulanadi: baza (RLS) ularga faqat o'z hisobi qatorlarini ko'rsatadi.</item>
/// <item>Token foydalanuvchiga ko'rsatiladi (nusxa olish uchun) - u botga birinchi kirishda shu tokenni kiritadi.</item>
/// </list>
/// Sirlar (token, ulanish satri) DPAPI'da; hisob id va bot manzili lokal bazada.
/// </summary>
public static class DeviceAccount
{
    private const string StateKey = "account.state";

    /// <summary>Bot manzili: YTAUTO_BOT_URL muhit o'zgaruvchisi yoki build paytida berilgan (CI: -p:YtAutoBotUrl=...).</summary>
    public static string DefaultBotUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("YTAUTO_BOT_URL");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim().TrimEnd('/');
            var meta = typeof(DeviceAccount).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "YtAutoBotUrl")?.Value;
            return (meta ?? "").Trim().TrimEnd('/');
        }
    }

    public static AccountState? Load(SqliteJobStore local)
    {
        try { return local.GetSetting(StateKey) is { } j ? JsonSerializer.Deserialize<AccountState>(j) : null; }
        catch { return null; }
    }

    private static void Save(SqliteJobStore local, AccountState s) => local.SetSetting(StateKey, JsonSerializer.Serialize(s));

    /// <summary>Hisob sozlangan: qurilma ulanish satri va hisob ma'lumoti bor.</summary>
    public static bool IsConfigured(DpapiSecretStore secrets, SqliteJobStore local) =>
        !string.IsNullOrWhiteSpace(secrets.Get(SecretNames.DeviceConnection)) && Load(local) is not null;

    public static string? Token(DpapiSecretStore secrets) => secrets.Get(SecretNames.AccountToken);

    /// <summary>Yangi token: "yt_" + 40 ta belgi (base62, ~238 bit). Tasodifiy, taxmin qilib bo'lmaydi.</summary>
    public static string NewToken() => "yt_" + Base62(RandomNumberGenerator.GetBytes(30), 40);

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    private static string NewPassword() => Base62(RandomNumberGenerator.GetBytes(32), 43);

    private static string Base62(byte[] bytes, int len)
    {
        const string abc = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var n = new System.Numerics.BigInteger(bytes, isUnsigned: true);
        var sb = new StringBuilder();
        while (sb.Length < len) { sb.Append(abc[(int)(n % 62)]); n /= 62; }
        return sb.ToString();
    }

    public static bool LooksLikeToken(string? t) => t is not null && System.Text.RegularExpressions.Regex.IsMatch(t.Trim(), "^yt_[0-9A-Za-z]{30,60}$");

    private sealed class DbInfo
    {
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; } = 5432;
        [JsonPropertyName("database")] public string Database { get; set; } = "postgres";
        [JsonPropertyName("username")] public string Username { get; set; } = "";
    }

    private sealed class RegisterResponse
    {
        [JsonPropertyName("accountId")] public Guid AccountId { get; set; }
        [JsonPropertyName("db")] public DbInfo? Db { get; set; }
        [JsonPropertyName("botUsername")] public string? BotUsername { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public static string BuildConnectionString(string host, int port, string database, string username, string password)
    {
        var b = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = database, Username = username, Password = password };
        if (!IsLocalHost(host)) b.SslMode = SslMode.Require;
        return b.ConnectionString;
    }

    public static bool IsLocalHost(string? host) =>
        host is "localhost" or "127.0.0.1" or "::1" || (host?.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Bot orqali hisob yaratadi (existingToken bo'sh) yoki mavjud token bilan shu kompyuterni o'sha hisobga ulaydi.
    /// Muvaffaqiyatli bo'lsa ulanish satri va token DPAPI'ga saqlanadi.
    /// </summary>
    public static async Task<AccountState> RegisterAsync(HttpClient http, string botUrl, string deviceName, string? existingToken,
        DpapiSecretStore secrets, SqliteJobStore local, CancellationToken ct)
    {
        botUrl = NormalizeBotUrl(botUrl);
        var login = !string.IsNullOrWhiteSpace(existingToken);
        if (login && !LooksLikeToken(existingToken)) throw new InvalidOperationException("Token noto'g'ri ko'rinishda (yt_ bilan boshlanadi).");
        var token = login ? existingToken!.Trim() : NewToken();
        var password = NewPassword();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{botUrl}/api/device/{(login ? "login" : "register")}")
        {
            Content = JsonContent.Create(new
            {
                deviceName = deviceName.Trim(), machine = Environment.MachineName, tokenHash = Hash(token), password,
                appVersion = typeof(DeviceAccount).Assembly.GetName().Version?.ToString() ?? "",
            }),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, cts.Token); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        { throw new InvalidOperationException($"Botga ulanib bo'lmadi ({botUrl}): {e.Message}. Internetni tekshiring.", e); }
        using (resp)
        {
            RegisterResponse? r = null;
            try { r = await resp.Content.ReadFromJsonAsync<RegisterResponse>(cts.Token); } catch { }
            if (!resp.IsSuccessStatusCode || r?.Db is null || r.AccountId == Guid.Empty)
                throw new InvalidOperationException(r?.Error ?? $"Bot javob bermadi (HTTP {(int)resp.StatusCode}).");
            var cs = BuildConnectionString(r.Db.Host, r.Db.Port, r.Db.Database, r.Db.Username, password);
            await WaitForLoginAsync(cs, ct); // pooler yangi rolni bir necha soniyada ko'radi
            var state = new AccountState
            {
                AccountId = r.AccountId, DeviceName = deviceName.Trim(), BotUrl = botUrl, BotUsername = r.BotUsername ?? "", CreatedAtUtc = DateTimeOffset.UtcNow,
            };
            Persist(secrets, local, cs, token, state);
            return state;
        }
    }

    /// <summary>
    /// v2 dan oldingi o'rnatma: kompyuterda bazaning egasi ulanish satri (postgres) bor. Eski ma'lumotlar (mavzular, promptlar, sozlamalar)
    /// shu kompyuter hisobiga o'tkaziladi, keyin ilova faqat o'z qurilma roli bilan ishlaydi va egasi ulanish satri o'chiriladi.
    /// </summary>
    public static async Task<AccountState> ClaimLegacyAsync(string masterConnection, string deviceName, string botUrl,
        DpapiSecretStore secrets, SqliteJobStore local, CancellationToken ct)
    {
        var token = NewToken();
        var password = NewPassword();
        var mb = new NpgsqlConnectionStringBuilder(masterConnection) { Pooling = false, Timeout = 20 };
        if (mb.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer && !IsLocalHost(mb.Host)) mb.SslMode = SslMode.Require;
        Guid acc; string role;
        await using (var c = new NpgsqlConnection(mb.ConnectionString))
        {
            await c.OpenAsync(ct);
            SchemaMigrator.EnsureMigrated(c);
            await using var cmd = new NpgsqlCommand("SELECT account_id, role_name FROM ytauto.claim_legacy_account(@d, @m, @h, @p)", c);
            cmd.Parameters.AddWithValue("d", deviceName.Trim()); cmd.Parameters.AddWithValue("m", Environment.MachineName);
            cmd.Parameters.AddWithValue("h", Hash(token)); cmd.Parameters.AddWithValue("p", password);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Hisob yaratilmadi.");
            acc = r.GetGuid(0); role = r.GetString(1);
        }
        // Supabase pooler: foydalanuvchi "postgres.<loyiha>" bo'lsa, rol ham "<rol>.<loyiha>" ko'rinishida ulanadi.
        var user = mb.Username ?? "";
        var dot = user.IndexOf('.');
        var deviceUser = dot > 0 ? role + user[dot..] : role;
        var cs = BuildConnectionString(mb.Host!, mb.Port, mb.Database ?? "postgres", deviceUser, password);
        await WaitForLoginAsync(cs, ct);
        var state = new AccountState
        {
            AccountId = acc, DeviceName = deviceName.Trim(), BotUrl = string.IsNullOrWhiteSpace(botUrl) ? "" : NormalizeBotUrl(botUrl), CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        Persist(secrets, local, cs, token, state);
        secrets.Set(SecretNames.PostgresConnection, null); // egasi paroli endi bu kompyuterda kerak emas
        return state;
    }

    /// <summary>Tokenni almashtiradi: yangi token qaytariladi (foydalanuvchi uni botga qayta kiritadi).</summary>
    public static string RotateToken(PostgresJobStore store, DpapiSecretStore secrets)
    {
        var token = NewToken();
        store.RotateToken(Hash(token));
        secrets.Set(SecretNames.AccountToken, token);
        return token;
    }

    public static void UpdateBotUrl(SqliteJobStore local, string botUrl)
    {
        var s = Load(local);
        if (s is null) return;
        s.BotUrl = NormalizeBotUrl(botUrl);
        Save(local, s);
    }

    public static string NormalizeBotUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || !(uri.Scheme == "https" || (uri.Scheme == "http" && IsLocalHost(uri.Host))))
            throw new InvalidOperationException("Bot manzili https://... ko'rinishida bo'lsin.");
        return u;
    }

    private static void Persist(DpapiSecretStore secrets, SqliteJobStore local, string cs, string token, AccountState state)
    {
        secrets.Set(SecretNames.DeviceConnection, cs);
        secrets.Set(SecretNames.AccountToken, token);
        Save(local, state);
    }

    private static async Task WaitForLoginAsync(string cs, CancellationToken ct)
    {
        string last = "";
        for (int i = 0; i < 8; i++)
        {
            var (ok, msg) = PostgresJobStore.Check(cs);
            if (ok) return;
            last = msg;
            await Task.Delay(TimeSpan.FromSeconds(2 + i), ct);
        }
        throw new InvalidOperationException("Yangi hisob bilan bazaga ulanib bo'lmadi: " + last);
    }
}
