using YtAuto.Core.Domain;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Media;

/// <summary>
/// Fon musiqasi keshi: botda tanlangan trek server (Supabase Storage) nusxasidan bir marta yuklab olinadi va
/// %LOCALAPPDATA%\YtAuto\music\&lt;openverse-id&gt;.&lt;ext&gt; da saqlanadi - keyingi videolar qayta yuklamaydi.
/// Xavfsizlik: faqat https va ma'lum manzillar (Supabase Storage, asl Openverse manbalari), 40 MB chegarasi,
/// fayl boshidan audio formati tekshiriladi; fayl nomi faqat GUID'dan (yo'l buzish mumkin emas).
/// </summary>
public sealed class MusicCache : IMusicCache
{
    private const long MaxBytes = 40L * 1024 * 1024;
    private static readonly string[] Hosts = { ".supabase.co", ".jamendo.com", "upload.wikimedia.org", "cdn.freesound.org" };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly string _dir;

    public MusicCache(HttpClient http, string dir) { _http = http; _dir = dir; }

    public async Task<string> GetLocalAsync(MusicTrack track, IJobLog log, CancellationToken ct)
    {
        if (!Guid.TryParse(track.Id, out var id)) throw new InvalidOperationException("Trek identifikatori noto'g'ri.");
        Directory.CreateDirectory(_dir);
        var name = id.ToString("D");
        var existing = Directory.GetFiles(_dir, name + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length > 10_000);
        if (existing is not null) return existing;

        if (!Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !Allowed(uri.Host))
            throw new InvalidOperationException("Musiqa havolasi ishonchsiz (faqat bot serveri).");

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var part = Path.Combine(_dir, name + ".part");
        try
        {
            existing = Directory.GetFiles(_dir, name + ".*").FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
            log.Info($"Fon musiqasi yuklab olinmoqda: «{track.Title}».", "Edit");
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is 400 or 401 or 403)
                throw new InvalidOperationException("Server musiqa havolasini rad etdi (muddati o'tgan bo'lishi mumkin) — botda «Sozlamalar»ni bir marta oching, havola yangilanadi.");
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Musiqa fayli juda katta.");
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[128 * 1024]; long total = 0; int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) throw new InvalidOperationException("Musiqa fayli juda katta.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            var ext = Sniff(part) ?? throw new InvalidOperationException("Yuklangan fayl audio emas.");
            var final = Path.Combine(_dir, name + ext);
            File.Move(part, final, true);
            return final;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            Gate.Release();
        }
    }

    internal static bool Allowed(string host) =>
        Hosts.Any(h => h.StartsWith('.') ? host.EndsWith(h, StringComparison.OrdinalIgnoreCase) : string.Equals(host, h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fayl boshidan format: MP3 (ID3 yoki kadr sinxroni), OGG, WAV, FLAC, M4A. Boshqasi - null.</summary>
    internal static string? Sniff(string path)
    {
        var h = new byte[12];
        using (var f = File.OpenRead(path)) { if (f.Read(h, 0, h.Length) < 12) return null; }
        if (h[0] == 'I' && h[1] == 'D' && h[2] == '3') return ".mp3";
        if (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0) return ".mp3";
        if (h[0] == 'O' && h[1] == 'g' && h[2] == 'g' && h[3] == 'S') return ".ogg";
        if (h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'A' && h[10] == 'V' && h[11] == 'E') return ".wav";
        if (h[0] == 'f' && h[1] == 'L' && h[2] == 'a' && h[3] == 'C') return ".flac";
        if (h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return ".m4a";
        return null;
    }
}
