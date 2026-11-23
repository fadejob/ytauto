using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace YtAuto.Infrastructure.Agent;

/// <summary>
/// Cloudflare quick tunnel (cloudflared): lokal agentni internetga https://*.trycloudflare.com manzili bilan chiqaradi (akkaunt, port
/// ochish, admin huquqi kerak emas). Manzil har ishga tushishda yangi - u topilishi bilan <c>onUrl</c> chaqiriladi (agent bazaga yozadi).
/// cloudflared o'lsa yoki Cloudflare'ga ulanishi 3 daqiqa tiklanmasa qayta ishga tushiriladi (yangi manzil yana bazaga yoziladi).
/// cloudflared agent bilan bitta Job Object'da: agent o'lsa u ham yopiladi (yetim jarayon qolmaydi).
/// </summary>
public sealed partial class TunnelSupervisor
{
    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    private readonly int _port;
    private readonly RunLog _log;
    private readonly Action<string?> _onUrl;
    private readonly HttpClient _http;
    private Processes.JobObject? _job;

    public string? Url { get; private set; }
    public string? Error { get; private set; }

    public TunnelSupervisor(int port, RunLog log, HttpClient http, Action<string?> onUrl)
    {
        _port = port; _log = log; _http = http; _onUrl = onUrl;
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    public static string LocalExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "tools", "cloudflared", "cloudflared.exe");

    public static string? Find()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var c in new[] { LocalExe, Path.Combine(pf86, "cloudflared", "cloudflared.exe"), Path.Combine(pf, "cloudflared", "cloudflared.exe") })
            if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var f = Path.Combine(dir.Trim(), "cloudflared.exe"); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    /// <summary>cloudflared yo'q bo'lsa Cloudflare'ning rasmiy GitHub relizidan yuklaydi (~60 MB, bir marta).</summary>
    public static async Task<string> EnsureInstalledAsync(HttpClient http, RunLog log, CancellationToken ct)
    {
        var found = Find();
        if (found is not null) return found;
        Directory.CreateDirectory(Path.GetDirectoryName(LocalExe)!);
        var tmp = LocalExe + ".download";
        log.Info("cloudflared topilmadi — Cloudflare'ning rasmiy relizidan yuklanmoqda (bir marta).");
        using (var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, 256 * 1024, ct);
        }
        var info = new FileInfo(tmp);
        var head = new byte[2];
        await using (var fs = File.OpenRead(tmp)) _ = await fs.ReadAsync(head, ct);
        if (info.Length < 5 * 1024 * 1024 || head[0] != (byte)'M' || head[1] != (byte)'Z')
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("Yuklangan cloudflared fayli yaroqsiz.");
        }
        File.Move(tmp, LocalExe, true);
        log.Info($"cloudflared yuklandi ({info.Length / 1048576.0:0.0} MB): {LocalExe}");
        return LocalExe;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>To'xtatilguncha: cloudflared'ni ishga tushiradi, kuzatadi, kerak bo'lsa qayta ishga tushiradi.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            string exe;
            try { exe = await EnsureInstalledAsync(_http, _log, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Error = "cloudflared o'rnatilmadi: " + e.Message;
                _log.Warn(Error + $" — {backoff.TotalSeconds:0} s dan keyin qayta uriniladi.");
                await Delay(backoff, ct);
                backoff = Next(backoff);
                continue;
            }

            var started = DateTime.UtcNow;
            try { await RunOnceAsync(exe, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) { Error = e.Message; _log.Warn("Tunnel xatosi: " + e.Message); }
            SetUrl(null);
            if (ct.IsCancellationRequested) return;
            // 2 daqiqadan ko'p ishlagan bo'lsa (masalan internet uzilib qolgan) - 3 s da; tez-tez yiqilsa - tobora kechroq.
            backoff = DateTime.UtcNow - started > TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(3) : Next(backoff);
            _log.Info($"Tunnel qayta ishga tushiriladi ({backoff.TotalSeconds:0} s dan keyin).");
            await Delay(backoff, ct);
        }
    }

    private async Task RunOnceAsync(string exe, CancellationToken ct)
    {
        var metrics = FreePort();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        // --http-host-header: lokal server (http.sys) faqat "localhost" nomini qabul qiladi.
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "--loglevel", "info", "--metrics", $"127.0.0.1:{metrics}",
                     "--url", $"http://localhost:{_port}", "--http-host-header", "localhost" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("cloudflared ishga tushmadi.");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        try { _job ??= new Processes.JobObject("YtAuto.Agent.Tunnel"); _job.Assign(p.Handle); } catch (Exception e) { _log.Debug("cloudflared Job Object'ga qo'shilmadi: " + e.Message); }
        _log.Info($"cloudflared ishga tushdi (pid {p.Id}), lokal port {_port}.");

        var urlFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var m = UrlRx().Match(line);
            if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase)) urlFound.TrySetResult(m.Value.ToLowerInvariant());
            if (line.Contains(" ERR ", StringComparison.Ordinal)) _log.Debug("cloudflared: " + line);
        }
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();

        using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } });
        var exited = p.WaitForExitAsync(CancellationToken.None);
        var first = await Task.WhenAny(urlFound.Task, exited, Task.Delay(TimeSpan.FromSeconds(90), ct));
        if (first != urlFound.Task)
        {
            if (!p.HasExited) { try { p.Kill(true); } catch { } }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(p.HasExited ? $"cloudflared to'xtadi (kod {SafeExit(p)}), manzil berilmadi." : "cloudflared 90 soniyada manzil bermadi.");
        }

        var url = urlFound.Task.Result;
        // Ulanish Cloudflare'da ro'yxatdan o'tishini kutamiz (/ready), so'ng manzil e'lon qilinadi.
        for (int i = 0; i < 30 && !p.HasExited && !await ReadyAsync(metrics, ct); i++) await Delay(TimeSpan.FromSeconds(1), ct);
        Error = null;
        SetUrl(url);
        _log.Info($"Tunnel tayyor: {url}");

        // Kuzatuv: har daqiqada cloudflared'ning lokal /ready holati. 3 marta ketma-ket tayyor bo'lmasa - qayta ishga tushirish.
        int notReady = 0;
        while (!p.HasExited)
        {
            var tick = await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(60), ct));
            if (tick == exited) break;
            if (await ReadyAsync(metrics, ct)) { notReady = 0; continue; }
            if (++notReady >= 3)
            {
                _log.Warn("Tunnel 3 daqiqadan beri Cloudflare'ga ulanmagan — qayta ishga tushirilmoqda.");
                try { p.Kill(true); } catch { }
                break;
            }
        }
        await exited;
        if (!ct.IsCancellationRequested) _log.Warn($"cloudflared to'xtadi (kod {SafeExit(p)}).");
    }

    private async Task<bool> ReadyAsync(int metricsPort, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var r = await _http.GetAsync($"http://127.0.0.1:{metricsPort}/ready", cts.Token);
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private void SetUrl(string? url)
    {
        if (Url == url) return;
        Url = url;
        try { _onUrl(url); } catch (Exception e) { _log.Warn("Tunnel manzilini e'lon qilib bo'lmadi: " + e.Message); }
    }

    private static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }
    private static TimeSpan Next(TimeSpan t) => TimeSpan.FromSeconds(Math.Min(t.TotalSeconds * 2, 120));
    private static async Task Delay(TimeSpan t, CancellationToken ct) { try { await Task.Delay(t, ct); } catch (OperationCanceledException) { } }
}
