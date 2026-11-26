using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.YouTube;

/// <summary>
/// Bot orqali (umumiy OAuth client bilan) ulangan kanal uchun access token'ni botdan oladi: refresh token va client secret serverda
/// qoladi, kompyuterga faqat qisqa muddatli access token keladi. Mehmon vazifasi bo'lsa - aynan o'sha mehmon kanalining tokeni.
/// </summary>
public sealed class BotYouTubeTokenBroker
{
    private readonly HttpClient _http;
    private readonly Func<(string BotUrl, string Token)?> _account;
    private readonly Func<Guid?> _guest;

    public BotYouTubeTokenBroker(HttpClient http, Func<(string BotUrl, string Token)?> account, Func<Guid?> guest)
    {
        _http = http; _account = account; _guest = guest;
    }

    private sealed record Reply(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("expiresIn")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);

    /// <param name="channelId">Vazifa bog'langan kanal (bo'sh - hisobning faol kanali).</param>
    public async Task<(string AccessToken, int ExpiresInSec)> GetAsync(string channelId, CancellationToken ct)
    {
        var acc = _account() ?? throw PipelineException.Config("youtube_broker_missing",
            "Kanal bot orqali ulangan, lekin kompyuter botga ulanmagan (hisob tokeni yo'q) - ilovada hisobga qayta kiring.");
        using var req = new HttpRequestMessage(HttpMethod.Post, acc.BotUrl.TrimEnd('/') + "/api/device/youtube-token")
        {
            Content = JsonContent.Create(new { guest = _guest()?.ToString(), channelId }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.Token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw PipelineException.Transient("youtube_broker_network", "YouTube tokeni uchun bot bilan tarmoq xatosi: " + e.Message, e); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw PipelineException.Transient("youtube_broker_timeout", "Bot YouTube tokenini o'z vaqtida bermadi."); }
        using (resp)
        {
            Reply? r;
            try { r = await resp.Content.ReadFromJsonAsync<Reply>(ct).ConfigureAwait(false); } catch { r = null; }
            if ((int)resp.StatusCode >= 500 || (int)resp.StatusCode == 429)
                throw PipelineException.Transient("youtube_broker", $"Bot YouTube tokenini bermadi (HTTP {(int)resp.StatusCode}): {r?.Error}");
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(r?.AccessToken))
                throw PipelineException.Config("youtube_reauth", r?.Error is { Length: > 0 } msg ? msg : $"Bot YouTube tokenini rad etdi (HTTP {(int)resp.StatusCode}) - botda kanalni qayta ulang.");
            return (r.AccessToken, r.ExpiresIn ?? 3000);
        }
    }
}
