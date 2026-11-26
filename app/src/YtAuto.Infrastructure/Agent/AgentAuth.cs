using System.Security.Cryptography;
using System.Text;
using YtAuto.Infrastructure.Security;

namespace YtAuto.Infrastructure.Agent;

/// <summary>
/// Agent (kompyuterdagi doimiy server) va bot o'rtasidagi imzo. Sir - shu kompyuterda tasodifiy yaratiladi (DPAPI'da) va online bazaga
/// (machines.agent_secret, faqat shu hisob RLS bilan ko'radi) yoziladi. Bot qisqa muddatli token imzolaydi, agent tekshiradi.
/// Token: "{exp}.{scope}.{account}.{hmac}" (exp - unix soniya). Bir xil algoritm: bot/src/lib/agent.ts.
/// </summary>
public static class AgentAuth
{
    public const string SecretName = "agent_secret";
    /// <summary>Token eng ko'pi shuncha muddatga beriladi (uzoqroq exp rad etiladi).</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);
    public static readonly string[] Scopes = { "api", "ws" };

    public static string LoadOrCreateSecret(DpapiSecretStore secrets)
    {
        var v = secrets.Get(SecretName);
        if (!string.IsNullOrEmpty(v) && v.Length >= 32) return v;
        v = B64(RandomNumberGenerator.GetBytes(32));
        secrets.Set(SecretName, v);
        return v;
    }

    public static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Mac(string secret, string data) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data));

    public static string Sign(string secret, string scope, string account, DateTimeOffset exp)
    {
        var body = $"{exp.ToUnixTimeSeconds()}.{scope}.{account}";
        return body + "." + B64(Mac(secret, body));
    }

    /// <summary>Token shu scope uchun, muddati o'tmagan va imzosi to'g'ri bo'lsa true; account - token bergan hisob.</summary>
    public static bool Verify(string secret, string? token, string scope, DateTimeOffset now, out string account)
    {
        account = "";
        if (string.IsNullOrEmpty(token) || token.Length > 300) return false;
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[1] != scope || !long.TryParse(parts[0], out var exp)) return false;
        var expAt = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (expAt < now || expAt > now + MaxTtl + TimeSpan.FromMinutes(1)) return false;
        if (!Guid.TryParse(parts[2], out _)) return false;
        var want = Encoding.ASCII.GetBytes(B64(Mac(secret, $"{parts[0]}.{parts[1]}.{parts[2]}")));
        if (!CryptographicOperations.FixedTimeEquals(want, Encoding.ASCII.GetBytes(parts[3]))) return false;
        account = parts[2].ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Public video havolasi imzosi (muddatsiz: tunnel manzili o'zgarsa havola baribir yangilanadi). Faqat shu hisob va shu fayl uchun:
    /// boshqa vazifa yoki fayl nomini almashtirib bo'lmaydi.
    /// </summary>
    public static string MediaSig(string secret, string account, long jobId, string file) =>
        B64(Mac(secret, $"media|{account.ToLowerInvariant()}|{jobId}|{file}"))[..32];

    public static bool VerifyMedia(string secret, string account, long jobId, string file, string? sig) =>
        sig is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(MediaSig(secret, account, jobId, file)), Encoding.ASCII.GetBytes(sig));
}
