using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure;

/// <summary>Ilova ma'lumotlari: %LOCALAPPDATA%\YtAuto (baza, loglar, records, sirlar, vositalar).</summary>
public sealed class AppPaths : IAppPaths
{
    public string DataRoot { get; }
    public AppPaths(string? root = null)
    {
        DataRoot = root ?? Environment.GetEnvironmentVariable("YTAUTO_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtAuto");
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RecordsDir);
        Directory.CreateDirectory(ToolsDir);
        Directory.CreateDirectory(SecretsDir);
    }
    public string DatabasePath => Path.Combine(DataRoot, "ytauto.db");
    public string LogsDir => Path.Combine(DataRoot, "logs");
    public string RecordsDir => Path.Combine(DataRoot, "records");
    public string ToolsDir => Path.Combine(DataRoot, "tools");
    public string SecretsDir => Path.Combine(DataRoot, "secrets");
    public string RecordDirFor(long jobId) { var d = Path.Combine(RecordsDir, $"job-{jobId}"); Directory.CreateDirectory(d); return d; }

    /// <summary>Worker exe'ning yonidagi resurslar (align_vtt.py va h.k.).</summary>
    public static string ResourcesDir => Path.Combine(AppContext.BaseDirectory, "Resources");
}
