using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using YtAuto.Core.Pipeline;

namespace YtAuto.Infrastructure.Processes;

public sealed class LaunchSpec
{
    public required string Exe { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }
    /// <summary>Qo'shiladigan yoki almashtiriladigan muhit o'zgaruvchilari (null qiymat - o'chirish).</summary>
    public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>();
    /// <summary>Shu nomlar (yoki prefikslar, '*' bilan) bilan boshlanuvchi o'zgaruvchilar meros olinmaydi.</summary>
    public IReadOnlyList<string> RemoveEnv { get; init; } = Array.Empty<string>();
    public string? StdoutPath { get; init; }
    public string? StderrPath { get; init; }
    public bool HideWindow { get; init; } = true;
    public bool StartMinimized { get; init; }
    public string Tool { get; init; } = "";
}

/// <summary>
/// Jarayonni CREATE_SUSPENDED holatda yaratadi, Job Object'ga biriktiradi va keyin davom ettiradi -
/// shu sabab hech bir child process job'dan "qochib" ulgurmaydi. stdout/stderr to'g'ridan-to'g'ri faylga yoziladi (RAM'da yig'ilmaydi).
/// Argumentlar CommandLineToArgvW qoidalari bo'yicha qo'shtirnoqlanadi; shell (cmd.exe) ishlatilmaydi.
/// </summary>
public static class ProcessLauncher
{
    public static OwnedProcess Start(LaunchSpec spec, JobObject job)
    {
        if (!File.Exists(spec.Exe)) throw new FileNotFoundException("Dastur topilmadi", spec.Exe);
        var cmd = new StringBuilder(Quote(spec.Exe));
        foreach (var a in spec.Args) cmd.Append(' ').Append(Quote(a));
        var cmdChars = (cmd + "\0").ToCharArray();

        var envBlock = BuildEnvironment(spec);
        var envPtr = Marshal.StringToHGlobalUni(envBlock);
        SafeFileHandle? hIn = null, hOut = null, hErr = null;
        IntPtr attrList = IntPtr.Zero, handlesPtr = IntPtr.Zero;
        try
        {
            hIn = OpenInheritable("NUL", FileMode.Open, FileAccess.Read);
            hOut = spec.StdoutPath is null ? OpenInheritable("NUL", FileMode.Open, FileAccess.Write) : OpenInheritable(spec.StdoutPath, FileMode.Create, FileAccess.Write);
            hErr = spec.StderrPath is null ? OpenInheritable("NUL", FileMode.Open, FileAccess.Write)
                : spec.StderrPath == spec.StdoutPath ? hOut : OpenInheritable(spec.StderrPath, FileMode.Create, FileAccess.Write);

            var handles = new List<IntPtr> { hIn.DangerousGetHandle(), hOut.DangerousGetHandle() };
            if (hErr != hOut) handles.Add(hErr.DangerousGetHandle());
            handlesPtr = Marshal.AllocHGlobal(IntPtr.Size * handles.Count);
            for (int i = 0; i < handles.Count; i++) Marshal.WriteIntPtr(handlesPtr, i * IntPtr.Size, handles[i]);

            IntPtr size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            if (!Native.UpdateProcThreadAttribute(attrList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handlesPtr, (IntPtr)(IntPtr.Size * handles.Count), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = hIn.DangerousGetHandle();
            si.StartupInfo.hStdOutput = hOut.DangerousGetHandle();
            si.StartupInfo.hStdError = hErr.DangerousGetHandle();
            if (spec.StartMinimized) { si.StartupInfo.dwFlags |= Native.STARTF_USESHOWWINDOW; si.StartupInfo.wShowWindow = Native.SW_SHOWMINNOACTIVE; }
            si.lpAttributeList = attrList;

            uint flags = Native.CREATE_SUSPENDED | Native.CREATE_UNICODE_ENVIRONMENT | Native.EXTENDED_STARTUPINFO_PRESENT
                         | Native.BELOW_NORMAL_PRIORITY_CLASS | Native.CREATE_NEW_PROCESS_GROUP;
            if (spec.HideWindow) flags |= Native.CREATE_NO_WINDOW;

            var cwd = string.IsNullOrEmpty(spec.WorkingDirectory) ? null : spec.WorkingDirectory;
            if (!Native.CreateProcessW(null, cmdChars, IntPtr.Zero, IntPtr.Zero, true, flags, envPtr, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess: {spec.Exe}");
            var hProcess = new SafeProcessHandle(pi.hProcess, true);
            try
            {
                job.Assign(pi.hProcess);
            }
            catch
            {
                Native.TerminateProcess(hProcess, 1);
                Native.CloseHandle(pi.hThread);
                hProcess.Dispose();
                throw;
            }
            Native.ResumeThread(pi.hThread);
            Native.CloseHandle(pi.hThread);
            Native.GetProcessTimes(hProcess, out var creation, out _, out _, out _);
            return new OwnedProcess(hProcess, pi.dwProcessId, DateTime.FromFileTimeUtc(creation), spec.Exe, job, spec.Tool);
        }
        finally
        {
            if (attrList != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handlesPtr != IntPtr.Zero) Marshal.FreeHGlobal(handlesPtr);
            Marshal.FreeHGlobal(envPtr);
            hIn?.Dispose();
            if (hErr != hOut) hErr?.Dispose();
            hOut?.Dispose();
        }
    }

    private static SafeFileHandle OpenInheritable(string path, FileMode mode, FileAccess access)
    {
        if (path != "NUL") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var h = File.OpenHandle(path, mode, access, FileShare.ReadWrite | FileShare.Delete);
        if (!Native.SetHandleInformation(h, Native.HANDLE_FLAG_INHERIT, Native.HANDLE_FLAG_INHERIT))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation");
        return h;
    }

    private static string BuildEnvironment(LaunchSpec spec)
    {
        var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var k = (string)kv.Key;
            if (spec.RemoveEnv.Any(r => r.EndsWith('*') ? k.StartsWith(r[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(k, r, StringComparison.OrdinalIgnoreCase))) continue;
            env[k] = (string?)kv.Value ?? "";
        }
        foreach (var kv in spec.Env)
        {
            if (kv.Value is null) env.Remove(kv.Key); else env[kv.Key] = kv.Value;
        }
        var sb = new StringBuilder();
        foreach (var kv in env) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    /// <summary>CommandLineToArgvW / MSVCRT qoidalari bo'yicha bitta argumentni qo'shtirnoqlaydi.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int bs = 0;
            while (i < arg.Length && arg[i] == '\\') { bs++; i++; }
            if (i == arg.Length) { sb.Append('\\', bs * 2); break; }
            if (arg[i] == '"') sb.Append('\\', bs * 2 + 1).Append('"');
            else sb.Append('\\', bs).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>Worker ishga tushirgan jarayon: PID + yaratilish vaqti (PID qayta ishlatilishiga qarshi) + Job Object.</summary>
public sealed class OwnedProcess : IDisposable
{
    private readonly SafeProcessHandle _h;
    public int Pid { get; }
    public DateTime StartTimeUtc { get; }
    public string Exe { get; }
    public JobObject Job { get; }
    public string Tool { get; }

    internal OwnedProcess(SafeProcessHandle h, int pid, DateTime start, string exe, JobObject job, string tool)
    { _h = h; Pid = pid; StartTimeUtc = start; Exe = exe; Job = job; Tool = tool; }

    public bool HasExited => Native.GetExitCodeProcess(_h, out var c) && c != Native.STILL_ACTIVE;

    public int ExitCode => Native.GetExitCodeProcess(_h, out var c) ? unchecked((int)c) : -1;

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var wh = new ProcessWaitHandle(_h);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reg = ThreadPool.RegisterWaitForSingleObject(wh, (_, timedOut) => tcs.TrySetResult(!timedOut), null, timeout, true);
        try
        {
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally { reg.Unregister(null); }
    }

    public void Terminate() => Native.TerminateProcess(_h, 1);

    public void Dispose() => _h.Dispose();

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle h)
        {
            // Handle nusxasi shart emas: OwnedProcess undan uzoqroq yashaydi; ownsHandle=false.
            SafeWaitHandle = new SafeWaitHandle(h.DangerousGetHandle(), false);
        }
    }
}
