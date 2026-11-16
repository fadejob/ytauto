using System.Diagnostics;
using System.Text.Json;
using YtAuto.Infrastructure.Processes;

namespace YtAuto.Infrastructure.Stats;

public sealed record DiskItem(string Name, string Path, long Bytes);
public sealed record ToolUsage(string Name, double PeakRamMb, double CpuSeconds);

/// <summary>
/// Worker ishlayotganda resurslarni kuzatadi (har ~3 soniyada): worker va u ishlatadigan dasturlar (VoiceStudio, Python, OpenFilm,
/// render, Claude) uchun eng yuqori RAM va CPU vaqti; tizim bo'yicha eng yuqori band RAM. Tugagach disk hajmlari
/// (modellar, arxiv, dasturlar) o'lchanib, hammasi JSON sifatida bazaga yoziladi (worker_runs.stats_json).
/// Eslatma: o'lchov ~3 soniyalik namuna olishga asoslangan, juda qisqa jarayonlar qisman ko'rinmasligi mumkin.
/// </summary>
public sealed class RunStatsSampler : IDisposable
{
    private static readonly (string Group, string[] Names)[] Groups =
    {
        ("Worker (YtAuto)", new[] { "ytauto.worker" }),
        ("VoiceStudio", new[] { "voicestudio" }),
        ("Python (ovoz/subtitr)", new[] { "python", "pythonw" }),
        ("OpenFilm", new[] { "openfilm" }),
        ("Render (ffmpeg/brauzer)", new[] { "ffmpeg", "ffprobe", "headless_shell", "chrome-headless-shell" }),
        ("Claude Code", new[] { "claude" }),
    };

    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _peakRam = new();
    private readonly Dictionary<string, Dictionary<int, double>> _cpu = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly string _diskRoot;
    private readonly double _freeStart;
    private double _peakUsedRam;
    private bool _sampling;

    private RunStatsSampler(string diskRoot)
    {
        _diskRoot = diskRoot;
        _freeStart = FreeGb(diskRoot);
        Sample();
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public static RunStatsSampler Start(AppServices svc)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(svc.Settings.Load().Paths.VideoArchiveDir)) ?? "C:\\";
        return new RunStatsSampler(root);
    }

    private static double FreeGb(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace / 1073741824.0; } catch { return 0; }
    }

    private void Sample()
    {
        lock (_gate)
        {
            if (_sampling) return;
            _sampling = true;
        }
        try
        {
            var (total, avail) = Native.Memory();
            var used = (total - avail) / 1048576.0;
            var ram = new Dictionary<string, double>();
            // Faqat shu worker ishga tushirgan jarayonlar (avlodlari) hisoblanadi: foydalanuvchining o'zi ochgan
            // VoiceStudio/Claude va boshqa dasturlar statistikaga kirmaydi.
            var mine = Descendants(Environment.ProcessId);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!mine.Contains(p.Id)) continue;
                    var name = p.ProcessName.ToLowerInvariant();
                    var g = Groups.FirstOrDefault(x => x.Names.Contains(name)).Group ?? OtherGroup;
                    ram[g] = ram.GetValueOrDefault(g) + p.WorkingSet64 / 1048576.0;
                    var cpu = p.TotalProcessorTime.TotalSeconds;
                    lock (_gate)
                    {
                        if (!_cpu.TryGetValue(g, out var d)) _cpu[g] = d = new();
                        d[p.Id] = Math.Max(d.GetValueOrDefault(p.Id), cpu);
                    }
                }
                catch { /* jarayon tugab qolgan yoki kirish rad etilgan */ }
                finally { p.Dispose(); }
            }
            lock (_gate)
            {
                _peakUsedRam = Math.Max(_peakUsedRam, used);
                foreach (var (g, mb) in ram) _peakRam[g] = Math.Max(_peakRam.GetValueOrDefault(g), mb);
            }
        }
        catch { }
        finally { lock (_gate) _sampling = false; }
    }

    private const string OtherGroup = "Boshqa ichki jarayonlar";

    /// <summary>rootPid va uning barcha avlodlari (Toolhelp snapshot: ota-ona PID zanjiri).</summary>
    private static HashSet<int> Descendants(int rootPid)
    {
        var parent = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap != IntPtr.Zero && snap != new IntPtr(-1))
        {
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
                for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
            }
            finally { CloseHandle(snap); }
        }
        var set = new HashSet<int> { rootPid };
        bool grew;
        do
        {
            grew = false;
            foreach (var (pid, ppid) in parent)
                if (pid != ppid && !set.Contains(pid) && set.Contains(ppid)) { set.Add(pid); grew = true; }
        } while (grew);
        return set;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32 e);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32 e);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public void Dispose() => _timer.Dispose();

    /// <summary>Kuzatuvni to'xtatadi, disk hajmlarini o'lchaydi va statistikani JSON qilib qaytaradi.</summary>
    public async Task<string> FinishAsync(AppServices svc, CancellationToken ct = default)
    {
        Sample();
        _timer.Dispose();
        var disk = await Task.Run(() => MeasureDisk(svc), ct);
        ToolUsage[] tools;
        double peakUsed, totalGb;
        lock (_gate)
        {
            tools = Groups.Select(g => g.Group).Append(OtherGroup).Select(g => new ToolUsage(g, Math.Round(_peakRam.GetValueOrDefault(g), 1),
                Math.Round(_cpu.TryGetValue(g, out var d) ? d.Values.Sum() : 0, 1))).Where(t => t.PeakRamMb > 0 || t.CpuSeconds > 0).ToArray();
            peakUsed = _peakUsedRam;
        }
        totalGb = Native.Memory().Total / 1073741824.0;
        var obj = new
        {
            machine = Environment.MachineName,
            at = DateTimeOffset.UtcNow,
            durationSec = Math.Round(_sw.Elapsed.TotalSeconds, 0),
            disk,
            diskTotalBytes = disk.Sum(d => d.Bytes),
            tools,
            system = new
            {
                totalRamGb = Math.Round(totalGb, 1), peakUsedRamGb = Math.Round(peakUsed / 1024.0, 2),
                diskRoot = _diskRoot, freeDiskGbStart = Math.Round(_freeStart, 1), freeDiskGbEnd = Math.Round(FreeGb(_diskRoot), 1),
            },
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>Hozirgi disk hajmlari (UI "hozir hisoblash" uchun ham ishlatiladi).</summary>
    public static List<DiskItem> MeasureDisk(AppServices svc)
    {
        var s = svc.Settings.Load();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var items = new List<(string Name, string? Path)>
        {
            ("Whisper modeli (subtitr)", WhisperDir(s)),
            ("Video arxiv", s.Paths.VideoArchiveDir),
            ("Ovoz arxiv", s.Paths.AudioArchiveDir),
            ("Ish papkasi (jobs)", s.Paths.WorkDir),
            ("Shablonlar", s.Paths.TemplatesDir),
            ("VoiceStudio (dastur)", Dir(s.VoiceStudio.ExePath)),
            ("VoiceStudio (ma'lumot va modellar)", Path.Combine(roaming, "VoiceStudio")),
            ("OpenFilm (dastur)", Dir(s.OpenFilm.ExePath)),
            ("OpenFilm (ma'lumot)", Path.Combine(roaming, "OpenFilm")),
            ("Claude Code", ClaudeDir(s.Claude.ExecutablePath)),
            ("Claude ma'lumotlari (~/.claude, barcha sessiyalar)", Path.Combine(home, ".claude")),
            ("YtAuto (baza, loglar)", Path.GetDirectoryName(svc.Paths.DatabasePath)),
        };
        var deadline = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiskItem>();
        foreach (var (name, path) in items)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) continue;
            // Umumiy vaqt chegarasi: juda katta papkalar tizimni ushlab turmasin (qolganlari 0 deb belgilanmaydi - o'tkazib yuboriladi).
            if (deadline.Elapsed > TimeSpan.FromMinutes(3)) break;
            result.Add(new DiskItem(name, full, DirSize(full, TimeSpan.FromSeconds(60))));
        }
        return result;
    }

    private static string? Dir(string? exe) => string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);

    private static string? WhisperDir(YtAuto.Core.Domain.AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.VoiceStudio.HfCacheDir)) return null;
        var d = Path.Combine(s.VoiceStudio.HfCacheDir, "models--" + s.VoiceStudio.WhisperModel.Replace("/", "--"));
        return Directory.Exists(d) ? d : null;
    }

    private static string? ClaudeDir(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var i = exe.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var end = exe.IndexOf(Path.DirectorySeparatorChar, i);
            return end > 0 ? exe[..end] : Path.GetDirectoryName(exe);
        }
        return Path.GetDirectoryName(exe);
    }

    public static long DirSize(string path, TimeSpan budget)
    {
        long total = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opt))
            {
                try { total += f.Length; } catch { }
                if (sw.Elapsed > budget) break;
            }
        }
        catch { }
        return total;
    }
}
