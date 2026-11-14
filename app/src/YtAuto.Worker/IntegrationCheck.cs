using YtAuto.Infrastructure;

namespace YtAuto.Worker;

internal static class IntegrationCheck
{
    public static async Task<int> RunAsync(AppServices svc, CancellationToken ct)
    {
        var deep = Environment.GetCommandLineArgs().Contains("--deep");
        var exe = Environment.ProcessPath;
        var results = await new IntegrationChecker(svc).RunAsync(deep, exe, ct,
            new Progress<CheckResult>(r => Console.WriteLine($"[{r.Level,-7}] {r.Name}: {r.Message}")));
        var report = string.Join(Environment.NewLine, results.Select(r => $"[{r.Level}] {r.Name}: {r.Message}"));
        File.WriteAllText(Path.Combine(svc.Paths.LogsDir, "last-check.txt"), $"{DateTimeOffset.Now:u}{Environment.NewLine}{report}");
        return results.Any(r => r.Level == CheckLevel.Failed) ? 1 : 0;
    }
}
