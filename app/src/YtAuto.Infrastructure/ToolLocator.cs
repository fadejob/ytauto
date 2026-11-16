using System.Diagnostics;
using Microsoft.Win32;

namespace YtAuto.Infrastructure;

/// <summary>
/// VoiceStudio, OpenFilm, ularning Python muhiti va Whisper keshini kompyuterdan o'zi topadi (qo'lda yo'l kiritish shart emas).
/// Tartib: ishlayotgan jarayon, Windows "o'rnatilgan dasturlar" ro'yxati (registry), odatiy papkalar,
/// so'ng qattiq disklarni cheklangan chuqurlikda qidirish. Natija jarayon davomida keshlanadi.
/// </summary>
public static class ToolLocator
{
    private static readonly Dictionary<string, string?> Cache = new();
    private static readonly object Gate = new();

    private static string? Memo(string key, Func<string?> find)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var v) && (v is null || File.Exists(v) || Directory.Exists(v))) return v;
            return Cache[key] = find();
        }
    }

    /// <summary>Yangi o'rnatishdan keyin keshni tozalaydi (aks holda "topilmadi" natijasi eslab qolinadi).</summary>
    public static void Reset() { lock (Gate) Cache.Clear(); }

    public static string? FindVoiceStudioExe() => Memo("vs", () => FindExe("VoiceStudio.exe", "VoiceStudio", "Voice Studio"));
    public static string? FindOpenFilmExe() => Memo("of", () => FindExe("OpenFilm.exe", "OpenFilm", "Open Film"));

    /// <summary>VoiceStudio'ning o'z Python muhiti (alignment uchun faster-whisper shu yerda).</summary>
    public static string? FindVoicePython(string? voiceStudioExe) => Memo("py|" + voiceStudioExe, () =>
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string> { Path.Combine(roaming, "VoiceStudio"), Path.Combine(local, "VoiceStudio") };
        if (!string.IsNullOrEmpty(voiceStudioExe)) roots.Add(Path.GetDirectoryName(voiceStudioExe)!);
        foreach (var r in roots)
        {
            var direct = Path.Combine(r, "runtime", "project", ".venv", "Scripts", "python.exe");
            if (File.Exists(direct)) return direct;
        }
        foreach (var r in roots)
            foreach (var venv in Walk(r, 5, d => Path.GetFileName(d).Equals(".venv", StringComparison.OrdinalIgnoreCase)))
            {
                var py = Path.Combine(venv, "Scripts", "python.exe");
                if (File.Exists(py)) return py;
            }
        return null;
    });

    /// <summary>HuggingFace keshi: ichida Whisper modeli (masalan models--Systran--faster-whisper-base) bor papka.</summary>
    public static string? FindHfCache(string whisperModel) => Memo("hf|" + whisperModel, () =>
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modelDir = "models--" + whisperModel.Replace("/", "--");
        var candidates = new List<string>
        {
            Path.Combine(local, "OmniVoice", "hf_cache"), Path.Combine(local, "VoiceStudio", "hf_cache"),
            Path.Combine(home, ".cache", "huggingface", "hub"),
        };
        var env = Environment.GetEnvironmentVariable("HF_HUB_CACHE") ?? Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(env)) { candidates.Insert(0, env); candidates.Insert(1, Path.Combine(env, "hub")); }
        foreach (var c in candidates) if (Directory.Exists(Path.Combine(c, modelDir))) return c;
        foreach (var c in new[] { Path.Combine(local, "OmniVoice"), Path.Combine(local, "VoiceStudio"), Path.Combine(home, ".cache") })
            foreach (var hit in Walk(c, 5, d => Path.GetFileName(d).Equals(modelDir, StringComparison.OrdinalIgnoreCase)))
                return Path.GetDirectoryName(hit);
        return null;
    });

    public static string? FindExe(string exeName, params string[] nameHints)
    {
        var stem = Path.GetFileNameWithoutExtension(exeName);
        // 1) hozir ishlayotgan jarayon
        try
        {
            foreach (var p in Process.GetProcessesByName(stem))
                try { var f = p.MainModule?.FileName; if (f is not null && File.Exists(f)) return f; } catch { } finally { p.Dispose(); }
        }
        catch { }

        // 2) Windows o'rnatilgan dasturlar ro'yxati
        foreach (var path in FromRegistry(exeName, nameHints)) return path;

        // 3) odatiy papkalar
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>
        {
            Path.Combine(local, "Programs"), local, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var r in roots)
            foreach (var h in nameHints)
            {
                var direct = Path.Combine(r, h, exeName);
                if (File.Exists(direct)) return direct;
                foreach (var dir in Walk(r, 1, d => Path.GetFileName(d).Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    var hit = FirstFile(dir, exeName, 3);
                    if (hit is not null) return hit;
                }
            }

        // 4) qattiq disklar: ildiz va 3 daraja ichida nomga mos papkalar (masalan D:\AppData\VoiceStudio, D:\Apps\OpenFilm)
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                foreach (var dir in Walk(drive.RootDirectory.FullName, 3, d => nameHints.Any(h => Path.GetFileName(d).Contains(h, StringComparison.OrdinalIgnoreCase))))
                {
                    var hit = FirstFile(dir, exeName, 3);
                    if (hit is not null) return hit;
                }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> FromRegistry(string exeName, string[] hints)
    {
        var keys = new (RegistryKey Hive, string Path)[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        var found = new List<string>();
        foreach (var (hive, path) in keys)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    var display = k?.GetValue("DisplayName") as string;
                    if (display is null || !hints.Any(h => display.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                    var loc = (k!.GetValue("InstallLocation") as string)?.Trim('"');
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        var hit = FirstFile(loc, exeName, 2);
                        if (hit is not null) found.Add(hit);
                    }
                    var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                    if (!string.IsNullOrWhiteSpace(icon) && icon.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) && File.Exists(icon)) found.Add(icon);
                }
            }
            catch { }
        }
        return found;
    }

    private static string? FirstFile(string dir, string name, int depth)
    {
        try
        {
            var f = Path.Combine(dir, name);
            if (File.Exists(f)) return f;
            if (depth <= 0 || !Directory.Exists(dir)) return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (Skip(sub)) continue;
                var hit = FirstFile(sub, name, depth - 1);
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Papkalar bo'ylab cheklangan chuqurlikda yuradi (kirish rad etilgan va tizim papkalari o'tkazib yuboriladi).</summary>
    private static IEnumerable<string> Walk(string root, int depth, Func<string, bool> match)
    {
        var results = new List<string>();
        void Go(string dir, int d)
        {
            List<string> subs;
            try { subs = Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : new List<string>(); } catch { return; }
            foreach (var s in subs)
            {
                if (Skip(s)) continue;
                if (match(s)) results.Add(s);
                if (d > 1) Go(s, d - 1);
            }
        }
        Go(root, depth);
        return results;
    }

    private static bool Skip(string dir)
    {
        var n = Path.GetFileName(dir);
        return n.StartsWith('$') || n.Equals("Windows", StringComparison.OrdinalIgnoreCase) || n.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || n.Equals("WinSxS", StringComparison.OrdinalIgnoreCase);
    }
}
