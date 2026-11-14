using System.Diagnostics;
using System.IO;

namespace YtAuto.Desktop.Ui;

public static class Shell
{
    public static void OpenFolderOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Worker exe: distributivda Desktop bilan bir papkada; dev rejimida qo'shni loyiha chiqishi.</summary>
    public static string? FindWorkerExe()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "YtAuto.Worker.exe");
        if (File.Exists(here)) return here;
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "YtAuto.Worker", "bin", "Debug", "net10.0-windows10.0.17763.0", "YtAuto.Worker.exe"));
        return File.Exists(dev) ? dev : null;
    }
}
