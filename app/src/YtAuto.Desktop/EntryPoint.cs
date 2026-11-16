namespace YtAuto.Desktop;

/// <summary>
/// Yagona YtAuto.exe: argumentsiz ochilsa - sozlamalar oynasi (WPF); worker argumenti bilan (Task Scheduler, bot buyruqlari) -
/// oynasiz worker rejimi. Shu tufayli distributiv bitta exe: alohida YtAuto.Worker.exe kerak emas.
/// </summary>
public static class EntryPoint
{
    private static readonly string[] WorkerFlags =
    {
        "--poll", "--scheduled", "--batch-now", "--manual", "--check", "--refresh", "--selftest-local",
        "--publish-catalog", "--import-config", "--schedule-install", "--schedule-remove",
    };

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => WorkerFlags.Contains(a, StringComparer.OrdinalIgnoreCase)))
            return YtAuto.Worker.Program.RunAsync(args).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
