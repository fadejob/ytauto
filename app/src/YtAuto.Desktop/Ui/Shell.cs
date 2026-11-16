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

    /// <summary>
    /// Worker - shu exe'ning o'zi: YtAuto.exe worker argumentlari (--poll, --scheduled, --batch-now ...) bilan ishga tushirilsa
    /// oynasiz worker rejimida ishlaydi (<see cref="EntryPoint"/>). Task Scheduler ham, bot buyruqlari ham shu exe'ni chaqiradi.
    /// </summary>
    public static string? FindWorkerExe()
    {
        var self = Environment.ProcessPath;
        return !string.IsNullOrEmpty(self) && File.Exists(self) ? self : null;
    }
}
