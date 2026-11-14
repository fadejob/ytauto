using System.Diagnostics;
using YtAuto.Core.Files;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Processes;

/// <summary>Jarayon identifikatsiyasi: PID + yaratilish vaqti + exe yo'li (PID qayta ishlatilsa ham adashmaslik uchun).</summary>
public readonly record struct ProcessIdentity(int Pid, DateTime StartUtc, string Path);

public static class ProcessSnapshot
{
    /// <summary>Exe yo'li berilgan prefiks(lar) ostida bo'lgan jarayonlar.</summary>
    public static List<ProcessIdentity> Under(params string[] pathPrefixes)
    {
        var list = new List<ProcessIdentity>();
        var prefixes = pathPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(PathGuard.Full).ToArray();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var id = Identify(p.Id);
                if (id is { } x && prefixes.Any(pre => x.Path.StartsWith(pre, StringComparison.OrdinalIgnoreCase))) list.Add(x);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static ProcessIdentity? Identify(int pid)
    {
        using var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        var buf = new char[1024]; int size = buf.Length;
        if (!Native.QueryFullProcessImageNameW(h, 0, buf, ref size)) return null;
        if (!Native.GetProcessTimes(h, out var creation, out _, out _, out _)) return null;
        return new ProcessIdentity(pid, DateTime.FromFileTimeUtc(creation), new string(buf, 0, size));
    }

    /// <summary>Faqat aynan shu identifikatsiyadagi jarayonni to'xtatadi (PID boshqa jarayonga o'tgan bo'lsa tegmaydi).</summary>
    public static bool KillExact(ProcessIdentity id)
    {
        using var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_TERMINATE, false, id.Pid);
        if (h.IsInvalid) return false;
        if (!Native.GetProcessTimes(h, out var creation, out _, out _, out _)) return false;
        if (DateTime.FromFileTimeUtc(creation) != id.StartUtc) return false;
        return Native.TerminateProcess(h, 1);
    }
}

/// <summary>Bir martalik CLI vositasini (claude, openfilm, python, ffmpeg) o'z Job Object'ida bajaradi va tozalaydi.</summary>
public static class ToolRunner
{
    /// <param name="sharedJob">Berilsa, jarayon shu (sessiya) job'iga qo'shiladi va qolgan child'lar sessiya yopilganda to'xtatiladi.</param>
    public static async Task<ToolRunResult> RunAsync(LaunchSpec spec, TimeSpan timeout, IJobLog? log, CancellationToken ct,
        Action<OwnedProcess>? onStarted = null, JobObject? sharedJob = null)
    {
        var ownJob = sharedJob is null ? new JobObject(spec.Tool) : null;
        try { return await RunInJobAsync(spec, timeout, log, ct, onStarted, sharedJob ?? ownJob!, ownJob is not null).ConfigureAwait(false); }
        finally { ownJob?.Dispose(); }
    }

    private static async Task<ToolRunResult> RunInJobAsync(LaunchSpec spec, TimeSpan timeout, IJobLog? log, CancellationToken ct,
        Action<OwnedProcess>? onStarted, JobObject job, bool cleanupLeftovers)
    {
        var sw = Stopwatch.StartNew();
        using var proc = ProcessLauncher.Start(spec, job);
        log?.Debug($"{spec.Tool}: pid {proc.Pid} ishga tushdi ({spec.Exe}).");
        onStarted?.Invoke(proc);
        bool exited, timedOut = false;
        try
        {
            exited = await proc.WaitForExitAsync(timeout, ct).ConfigureAwait(false);
            timedOut = !exited;
        }
        catch (OperationCanceledException)
        {
            var killed = job.Terminate();
            log?.Warn($"{spec.Tool}: bekor qilindi, {killed} ta jarayon to'xtatildi.");
            throw;
        }
        var acct = job.Accounting();
        int leftovers = 0;
        if (timedOut)
        {
            leftovers = job.Terminate();
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            log?.Warn($"{spec.Tool}: {timeout.TotalSeconds:0}s timeout, jarayon daraxti ({leftovers} ta) to'xtatildi.");
        }
        else if (cleanupLeftovers)
        {
            // Asosiy jarayon tugadi: unga tegishli qolgan child'lar (masalan, OpenFilm Studio server, headless brauzer) yopiladi.
            var left = job.ProcessIds();
            if (left.Length > 0)
            {
                job.RequestCloseWindows();
                await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
                leftovers = job.Terminate();
                if (leftovers > 0) log?.Debug($"{spec.Tool}: tugagandan keyin qolgan {leftovers} ta child jarayon to'xtatildi.");
            }
        }
        return new ToolRunResult
        {
            ExitCode = timedOut ? -1 : proc.ExitCode,
            TimedOut = timedOut,
            Elapsed = sw.Elapsed,
            StdoutPath = spec.StdoutPath ?? "",
            StderrPath = spec.StderrPath ?? "",
            PeakMemoryBytes = acct.PeakJobMemory,
            CpuSeconds = acct.CpuSeconds,
            LeftoverProcessesKilled = leftovers,
            StdoutTail = spec.StdoutPath is null ? "" : Redactor.Redact(FileOps.ReadTail(spec.StdoutPath, 8 * 1024)),
            StderrTail = spec.StderrPath is null ? "" : Redactor.Redact(FileOps.ReadTail(spec.StderrPath, 8 * 1024)),
        };
    }
}
