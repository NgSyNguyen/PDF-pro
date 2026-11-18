using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PdfPro.Infrastructure.Worker;

/// <summary>
/// A Windows job object holding the worker process: its memory is limited (going over makes allocations fail, and the
/// worker dies instead of the computer slowing down), and it ends with the app — closing the job's last handle kills
/// every process in it. Elsewhere the worker runs without a job (the app still kills it when it closes).
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitProcessMemory = 0x00000100;
    private const uint LimitDieOnUnhandledException = 0x00000400;
    private const uint LimitKillOnJobClose = 0x00002000;

    private readonly SafeFileHandle _handle;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    /// <summary>A job limiting each process in it to <paramref name="memoryLimitBytes"/> (0 = no limit), or null off Windows or when Windows refuses.</summary>
    public static JobObject? Create(long memoryLimitBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimit
        {
            Basic = new JobObjectBasicLimit { LimitFlags = LimitKillOnJobClose | LimitDieOnUnhandledException },
        };
        if (memoryLimitBytes > 0)
        {
            limits.Basic.LimitFlags |= LimitProcessMemory;
            limits.ProcessMemoryLimit = (nuint)memoryLimitBytes;
        }

        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
        {
            handle.Dispose();
            return null;
        }

        return new JobObject(handle);
    }

    /// <summary>Puts a process in the job; false when Windows refuses (the process then runs without limits).</summary>
    public bool Add(Process process) => AssignProcessToJobObject(_handle, process.SafeHandle);

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
