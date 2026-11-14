using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YtAuto.Infrastructure.Processes;

/// <summary>
/// Windows Job Object: worker ishga tushirgan jarayon va uning BARCHA avlodlari (detached child'lar ham) shu job ichida bo'ladi.
/// KILL_ON_JOB_CLOSE: worker kutilmaganda o'lsa ham, uning jarayonlari yetim qolmaydi.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _h;
    public string Name { get; }

    public JobObject(string name)
    {
        Name = name;
        _h = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (_h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | Native.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        var size = Marshal.SizeOf(info);
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            if (!Native.SetInformationJobObject(_h, Native.JobObjectExtendedLimitInformation, p, size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public void Assign(IntPtr processHandle)
    {
        if (!Native.AssignProcessToJobObject(_h, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject");
    }

    public int[] ProcessIds()
    {
        const int max = 1024;
        var size = 8 + IntPtr.Size * max;
        var p = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.QueryInformationJobObject(_h, Native.JobObjectBasicProcessIdList, p, size, out _)) return Array.Empty<int>();
            var count = Marshal.ReadInt32(p, 4);
            var ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(p, 8 + i * IntPtr.Size);
            return ids;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    public (long PeakJobMemory, double CpuSeconds, int TotalProcesses) Accounting()
    {
        long peak = 0; double cpu = 0; int total = 0;
        var ext = Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var p = Marshal.AllocHGlobal(Math.Max(ext, Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>()));
        try
        {
            if (Native.QueryInformationJobObject(_h, Native.JobObjectExtendedLimitInformation, p, ext, out _))
                peak = (long)Marshal.PtrToStructure<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(p).PeakJobMemoryUsed;
            if (Native.QueryInformationJobObject(_h, Native.JobObjectBasicAccountingInformation, p, Marshal.SizeOf<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), out _))
            {
                var a = Marshal.PtrToStructure<Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(p);
                cpu = (a.TotalUserTime + a.TotalKernelTime) / 1e7;
                total = (int)a.TotalProcesses;
            }
        }
        finally { Marshal.FreeHGlobal(p); }
        return (peak, cpu, total);
    }

    /// <summary>Job ichidagi jarayonlarning ko'rinadigan oynalariga WM_CLOSE yuboradi (normal yopish).</summary>
    public int RequestCloseWindows()
    {
        var pids = new HashSet<int>(ProcessIds());
        int sent = 0;
        Native.EnumWindows((hWnd, _) =>
        {
            Native.GetWindowThreadProcessId(hWnd, out var pid);
            if (pids.Contains(pid) && Native.IsWindowVisible(hWnd)) { Native.PostMessageW(hWnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); sent++; }
            return true;
        }, IntPtr.Zero);
        return sent;
    }

    /// <summary>Job ichidagi barcha jarayonlarni to'xtatadi (faqat worker'ga tegishlilar).</summary>
    public int Terminate(uint exitCode = 1)
    {
        var n = ProcessIds().Length;
        if (n > 0) Native.TerminateJobObject(_h, exitCode);
        return n;
    }

    public void Dispose() => _h.Dispose();
}
