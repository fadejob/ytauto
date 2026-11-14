namespace YtAuto.Infrastructure.Claude;

/// <summary>
/// Claude Code CLI'ni topadi. Sinovda aniqlangan holat: Claude desktop ilovasi MSIX paket bo'lgani uchun uning ichida
/// o'rnatilgan npm CLI haqiqiy profilda %APPDATA%\npm da emas, balki Packages\Claude_*\LocalCache\Roaming\npm da turadi;
/// ilovaning o'z nusxasi esa %LOCALAPPDATA%\Claude-Data\*\claude-code\&lt;versiya&gt;\&lt;hash&gt;\claude.exe da (yangilanishda yo'li o'zgaradi).
/// Tartib: PATH → native (~/.local/bin) → %APPDATA%\npm → MSIX LocalCache npm → Claude ilovasidagi eng yangi versiya.
/// </summary>
public static class ClaudeLocator
{
    public static IEnumerable<string> Candidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string? p = null;
            try { p = Path.Combine(dir.Trim('"'), "claude.exe"); } catch { }
            if (p is not null) yield return p;
        }
        yield return Path.Combine(home, ".local", "bin", "claude.exe");
        yield return Path.Combine(roaming, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");

        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            foreach (var pkg in SafeDirs(packages, "Claude_*"))
                yield return Path.Combine(pkg, "LocalCache", "Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");

        // Claude desktop ilovasining o'z nusxalari: eng yuqori versiya birinchi.
        var data = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(data))
        {
            var found = new List<(Version V, string Path)>();
            foreach (var acc in SafeDirs(data, "*"))
            foreach (var verDir in SafeDirs(Path.Combine(acc, "claude-code"), "*"))
            {
                if (!Version.TryParse(Path.GetFileName(verDir), out var v)) continue;
                foreach (var hashDir in SafeDirs(verDir, "*"))
                {
                    var exe = Path.Combine(hashDir, "claude.exe");
                    if (File.Exists(exe)) found.Add((v, exe));
                }
            }
            foreach (var f in found.OrderByDescending(x => x.V).ThenByDescending(x => File.GetLastWriteTimeUtc(x.Path)))
                yield return f.Path;
        }
    }

    public static string? Find() => Candidates().FirstOrDefault(File.Exists);

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
