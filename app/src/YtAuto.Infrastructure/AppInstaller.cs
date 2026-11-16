using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace YtAuto.Infrastructure;

/// <summary>Yuklash/o'rnatish jarayoni holati (UI'da ko'rsatiladi). Fraction: 0..1, belgilanmagan bo'lsa -1.</summary>
public sealed record InstallProgress(string Stage, double Fraction, string Text);

/// <summary>
/// OpenFilm va VoiceStudio kompyuterda yo'q bo'lsa ularni rasmiy manbadan yuklab, jim (silent) o'rnatadi.
/// - OpenFilm: openfilm.dev rasmiy yuklash manzili (ilovaning o'z yangilash sozlamasidagi manzil).
/// - VoiceStudio: GitHub (debpalash/VoiceStudio) oxirgi relizidagi win-x64 o'rnatuvchi; SHA256SUMS.txt bilan tekshiriladi.
/// O'rnatilgach yo'l <see cref="ToolLocator"/> orqali topilib, sozlamalarda saqlanadi.
/// </summary>
public static class AppInstaller
{
    public const string OpenFilmUrl = "https://openfilm.dev/download/desktop/win/x64";
    public const string VoiceStudioRepo = "debpalash/VoiceStudio";

    private static string DownloadDir()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto", "downloads");
        Directory.CreateDirectory(d);
        return d;
    }

    private static HttpClient NewClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("YtAuto-Installer/1.0");
        return c;
    }

    public static async Task<string> InstallOpenFilmAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var http = NewClient();
        var file = Path.Combine(DownloadDir(), "OpenFilm-Setup.exe");
        await DownloadAsync(http, OpenFilmUrl, file, "OpenFilm", progress, ct);
        await RunInstallerAsync(file, "OpenFilm", progress, ct);
        ToolLocator.Reset();
        var exe = ToolLocator.FindOpenFilmExe() ?? throw new InvalidOperationException("OpenFilm o'rnatildi, lekin OpenFilm.exe topilmadi.");
        TryDelete(file);
        progress.Report(new("done", 1, $"OpenFilm tayyor: {exe}"));
        return exe;
    }

    public static async Task<string> InstallVoiceStudioAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var http = NewClient();
        progress.Report(new("find", -1, "VoiceStudio oxirgi versiyasi qidirilmoqda (GitHub)…"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{VoiceStudioRepo}/releases/latest", ct));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string? url = null, name = null, sums = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            var u = a.GetProperty("browser_download_url").GetString();
            if (n.StartsWith("VoiceStudio-", StringComparison.OrdinalIgnoreCase) && n.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)) { url = u; name = n; }
            else if (n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = u;
        }
        if (url is null || name is null) throw new InvalidOperationException($"GitHub relizida ({tag}) Windows x64 o'rnatuvchi topilmadi.");
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Kutilmagan yuklash manzili: " + url);

        var file = Path.Combine(DownloadDir(), name);
        await DownloadAsync(http, url, file, $"VoiceStudio {tag}", progress, ct);

        if (sums is not null)
        {
            progress.Report(new("verify", -1, "Fayl yaxlitligi (SHA-256) tekshirilmoqda…"));
            var expected = (await http.GetStringAsync(sums, ct)).Split('\n')
                .Select(l => l.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length >= 2 && p[^1].Equals(name, StringComparison.OrdinalIgnoreCase))?[0];
            if (expected is not null)
            {
                await using var fs = File.OpenRead(file);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                    throw new InvalidOperationException("Yuklangan fayl SHA-256 si mos kelmadi - o'rnatilmadi.");
                }
            }
        }
        await RunInstallerAsync(file, "VoiceStudio", progress, ct);
        ToolLocator.Reset();
        var exe = ToolLocator.FindVoiceStudioExe() ?? throw new InvalidOperationException("VoiceStudio o'rnatildi, lekin VoiceStudio.exe topilmadi.");
        TryDelete(file);
        progress.Report(new("done", 1, $"VoiceStudio tayyor: {exe}"));
        return exe;
    }

    /// <summary>
    /// Claude Code: rasmiy Windows o'rnatuvchisi (https://claude.ai/install.ps1, foydalanuvchi papkasiga, administrator huquqisiz).
    /// O'rnatilgach claude.exe <see cref="Claude.ClaudeLocator"/> orqali topiladi. Tizimga kirish (login) foydalanuvchining o'zi qiladi.
    /// </summary>
    public static async Task<string> InstallClaudeCodeAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress.Report(new("install", -1, "Claude Code o'rnatilmoqda (rasmiy o'rnatuvchi, bir necha daqiqa)…"));
        var log = Path.Combine(DownloadDir(), "claude-install.log");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "irm https://claude.ai/install.ps1 | iex" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell ishga tushmadi.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new InvalidOperationException("Claude Code o'rnatish 15 daqiqada tugamadi."); }
        }
        await File.WriteAllTextAsync(log, await outTask + Environment.NewLine + await errTask, CancellationToken.None);
        var exe = Claude.ClaudeLocator.Find();
        if (exe is null) throw new InvalidOperationException($"Claude Code o'rnatilmadi (kod {p.ExitCode}). Batafsil: {log}");
        progress.Report(new("done", 1, $"Claude Code tayyor: {exe}"));
        return exe;
    }

    /// <summary>Claude Code'ga kirilganmi (taxminiy: hisob ma'lumotlari fayli bor).</summary>
    public static bool ClaudeLoggedIn()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists(Path.Combine(home, ".claude", ".credentials.json"));
    }

    /// <summary>
    /// HuggingFace modelini (faster-whisper, KittenTTS va h.k.) HuggingFace dan HF kesh tuzilmasida yuklaydi:
    /// models--ORG--NAME/snapshots/&lt;sha&gt;/..., refs/main. VoiceStudio worker'i HF_HUB_OFFLINE=1 bilan aynan shu keshdan o'qiydi.
    /// </summary>
    public static async Task<string> DownloadHfModelAsync(string model, string cacheDir, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var http = NewClient();
        progress.Report(new("find", -1, $"Model ma'lumoti olinmoqda ({model})…"));
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://huggingface.co/api/models/{model}?blobs=true", ct));
        var sha = doc.RootElement.GetProperty("sha").GetString() ?? throw new InvalidOperationException("Model versiyasi (sha) topilmadi.");
        var files = doc.RootElement.GetProperty("siblings").EnumerateArray()
            .Select(s => (Name: s.GetProperty("rfilename").GetString() ?? "", Size: s.TryGetProperty("size", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt64() : 0L))
            .Where(f => f.Name.Length > 0 && !f.Name.StartsWith('.') && !f.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("..")).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Modelda fayllar topilmadi.");

        var repoDir = Path.Combine(cacheDir, "models--" + model.Replace("/", "--"));
        var snap = Path.Combine(repoDir, "snapshots", sha);
        Directory.CreateDirectory(snap); Directory.CreateDirectory(Path.Combine(repoDir, "refs"));
        long total = files.Sum(f => f.Size), done = 0; var sw = Stopwatch.StartNew(); var last = TimeSpan.Zero;
        foreach (var f in files)
        {
            var dest = Path.Combine(snap, f.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) && (f.Size == 0 || new FileInfo(dest).Length == f.Size)) { done += f.Size; continue; }
            using var resp = await http.GetAsync($"https://huggingface.co/{model}/resolve/{sha}/{f.Name}", HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(dest + ".part"))
            {
                var buf = new byte[256 * 1024]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (sw.Elapsed - last < TimeSpan.FromMilliseconds(250)) continue;
                    last = sw.Elapsed;
                    var speed = done / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1048576.0;
                    progress.Report(new("download", total > 0 ? Math.Min(1.0, (double)done / total) : -1,
                        $"Model yuklanmoqda ({f.Name}): {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB, {speed:0.0} MB/s"));
                }
            }
            File.Move(dest + ".part", dest, true);
        }
        await File.WriteAllTextAsync(Path.Combine(repoDir, "refs", "main"), sha, ct);
        progress.Report(new("done", 1, $"Model tayyor: {repoDir}"));
        return cacheDir;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string dest, string label, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress.Report(new("download", 0, $"{label} yuklanmoqda…"));
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[256 * 1024];
        long done = 0; var sw = Stopwatch.StartNew(); var last = TimeSpan.Zero;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (sw.Elapsed - last < TimeSpan.FromMilliseconds(250)) continue;
            last = sw.Elapsed;
            var speed = done / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1048576.0;
            var text = total > 0
                ? $"{label} yuklanmoqda: {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB ({done * 100.0 / total:0}%), {speed:0.0} MB/s"
                : $"{label} yuklanmoqda: {done / 1048576.0:0.0} MB, {speed:0.0} MB/s";
            progress.Report(new("download", total > 0 ? (double)done / total : -1, text));
        }
        progress.Report(new("download", 1, $"{label} yuklandi ({done / 1048576.0:0.0} MB)."));
    }

    private static async Task RunInstallerAsync(string installer, string label, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress.Report(new("install", -1, $"{label} o'rnatilmoqda… (Windows ruxsat so'rashi mumkin)"));
        // UseShellExecute: o'rnatuvchi administrator huquqini so'rasa, UAC oynasi chiqadi. /S - jim o'rnatish (NSIS).
        using var p = Process.Start(new ProcessStartInfo(installer, "/S") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! })
            ?? throw new InvalidOperationException("O'rnatuvchi ishga tushmadi.");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"{label} o'rnatuvchisi xato bilan tugadi (kod {p.ExitCode}).");
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
