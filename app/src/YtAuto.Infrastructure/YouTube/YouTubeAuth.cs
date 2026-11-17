using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.YouTube;

public sealed class OAuthTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string Scope { get; set; } = "";
}

/// <summary>
/// Google OAuth 2.0 (o'rnatilgan ilova, loopback redirect + PKCE). Tokenlar DPAPI secret store'da.
/// Scope'lar minimal: youtube.upload (yuklash, thumbnail) + youtube.readonly (holatni tekshirish).
/// </summary>
public sealed class YouTubeAuth
{
    public static readonly string[] Scopes =
    {
        "https://www.googleapis.com/auth/youtube.upload",
        "https://www.googleapis.com/auth/youtube.readonly",
    };
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly ISecretStore _secrets;
    private readonly Func<YouTubeSettings> _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public YouTubeAuth(ISecretStore secrets, Func<YouTubeSettings> settings, HttpClient http)
    { _secrets = secrets; _settings = settings; _http = http; }

    public bool HasTokens => Load() is { RefreshToken.Length: > 0 };

    /// <summary>Mehmon hisoblari kanallari tokenlari: faqat xotirada (kompyuter egasining lokal tokeni ustiga yozilmaydi).</summary>
    private readonly Dictionary<string, OAuthTokens> _remoteOnly = new();

    private bool RemoteOnly() { try { return _settings().RemoteOnly; } catch { return false; } }

    private OAuthTokens? Load()
    {
        if (RemoteOnly())
        {
            // Mehmon vazifasi: faqat uning bot orqali ulagan kanali. Ulanmagan bo'lsa - "ulanmagan" (egasining kanaliga yuklanmaydi).
            string rt = "";
            try { rt = _settings().RemoteRefreshToken?.Trim() ?? ""; } catch { }
            if (rt.Length == 0) return null;
            lock (_remoteOnly) return _remoteOnly.TryGetValue(rt, out var t) ? t : new OAuthTokens { RefreshToken = rt };
        }
        OAuthTokens? local = null;
        var json = _secrets.Get(SecretNames.YouTubeTokens);
        if (!string.IsNullOrEmpty(json))
            try { local = JsonSerializer.Deserialize<OAuthTokens>(json); } catch { }
        // Bot orqali ulangan kanal (online bazadagi refresh token) lokal tokendan ustun: qayta ulansa yangisi olinadi.
        string remote = "";
        try { remote = _settings().RemoteRefreshToken ?? ""; } catch { }
        if (remote.Length > 0 && (local is null || local.RefreshToken != remote))
            return new OAuthTokens { RefreshToken = remote.Trim() };
        return local;
    }

    private void Save(OAuthTokens t, string? remoteOnlyKey = null)
    {
        if (remoteOnlyKey is not null) { lock (_remoteOnly) _remoteOnly[remoteOnlyKey] = t; return; }
        _secrets.Set(SecretNames.YouTubeTokens, JsonSerializer.Serialize(t));
    }

    public void Disconnect() => _secrets.Set(SecretNames.YouTubeTokens, null);

    private (string Id, string Secret) Client()
    {
        var id = _settings().ClientId?.Trim() ?? "";
        var secret = !string.IsNullOrWhiteSpace(_settings().RemoteClientSecret) ? _settings().RemoteClientSecret.Trim() : _secrets.Get(SecretNames.YouTubeClientSecret) ?? "";
        if (id.Length == 0 || secret.Length == 0)
            throw PipelineException.Config("youtube_client_missing", "YouTube OAuth Client ID/Secret kiritilmagan (Sozlamalar → YouTube).");
        return (id, secret);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var remoteOnly = RemoteOnly();
            var t = Load() ?? throw PipelineException.Config("youtube_not_connected", remoteOnly
                ? "YouTube kanali ulanmagan: botda Sozlamalar → YouTube'ni ulang (video arxivda saqlandi)."
                : "YouTube kanali ulanmagan.");
            var memKey = remoteOnly ? t.RefreshToken : null;
            if (!forceRefresh && t.AccessToken.Length > 0 && t.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return t.AccessToken;
            var (id, secret) = Client();
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = id, ["client_secret"] = secret, ["refresh_token"] = t.RefreshToken, ["grant_type"] = "refresh_token",
            });
            HttpResponseMessage resp;
            try { resp = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw PipelineException.Transient("youtube_network", $"Google OAuth bilan tarmoq xatosi: {e.Message}", e); }
            using (resp)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (raw.Contains("invalid_grant") || raw.Contains("invalid_client") || raw.Contains("unauthorized_client"))
                        throw PipelineException.Config("youtube_reauth", "YouTube ruxsati bekor qilingan yoki muddati tugagan. Sozlamalarda kanalni qayta ulang.");
                    throw PipelineException.Transient("youtube_token", $"Token yangilanmadi (HTTP {(int)resp.StatusCode}).");
                }
                var j = JsonNode.Parse(raw)!;
                t.AccessToken = j["access_token"]!.GetValue<string>();
                t.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 3600);
                if (j["refresh_token"] is JsonNode rt) t.RefreshToken = rt.GetValue<string>();
                Save(t, memKey);
                Redactor.Register(t.AccessToken);
                return t.AccessToken;
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>Brauzerda Google ruxsat oynasini ochadi va 127.0.0.1 dagi vaqtinchalik port orqali kodni qabul qiladi (UI'dan chaqiriladi).</summary>
    public async Task<string> ConnectInteractiveAsync(CancellationToken ct)
    {
        var (id, secret) = Client();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/";
            var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = B64Url(RandomNumberGenerator.GetBytes(16));
            var url = $"{AuthEndpoint}?response_type=code&client_id={Uri.EscapeDataString(id)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                      $"&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}&code_challenge={challenge}&code_challenge_method=S256" +
                      $"&state={state}&access_type=offline&prompt=consent";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            string? code = null, error = null;
            while (code is null && error is null)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                var buf = new byte[8192]; var n = await stream.ReadAsync(buf, timeout.Token).ConfigureAwait(false);
                var request = Encoding.ASCII.GetString(buf, 0, n);
                var line = request.Split("\r\n")[0];
                var path = line.Split(' ').ElementAtOrDefault(1) ?? "/";
                var q = System.Web.HttpUtility.ParseQueryString(path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "");
                string html;
                if (q["state"] != state) { html = "Noto'g'ri so'rov."; }
                else if (q["error"] is { } err) { error = err; html = "Ruxsat berilmadi. Oynani yopishingiz mumkin."; }
                else if (q["code"] is { } c) { code = c; html = "YouTube kanali ulandi. Bu oynani yopib, ilovaga qayting."; }
                else html = "Kutilmoqda...";
                var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>YtAuto</title><body style='font-family:sans-serif;padding:40px'><h2>{html}</h2></body>");
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
            }
            if (error is not null) throw new InvalidOperationException($"Google ruxsat bermadi: {error}");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code!, ["client_id"] = id, ["client_secret"] = secret, ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
            });
            using var resp = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Token olinmadi (HTTP {(int)resp.StatusCode}): {Redactor.Redact(raw)}");
            var j = JsonNode.Parse(raw)!;
            var t = new OAuthTokens
            {
                AccessToken = j["access_token"]!.GetValue<string>(),
                RefreshToken = j["refresh_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Google refresh_token bermadi."),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 3600),
                Scope = j["scope"]?.GetValue<string>() ?? "",
            };
            Save(t);
            return await ChannelTitleAsync(ct).ConfigureAwait(false);
        }
        finally { listener.Stop(); }
    }

    public async Task<string> ChannelTitleAsync(CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Kanal ma'lumoti olinmadi (HTTP {(int)resp.StatusCode}).");
        return JsonNode.Parse(raw)?["items"]?[0]?["snippet"]?["title"]?.GetValue<string>() ?? "(kanal topilmadi)";
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
