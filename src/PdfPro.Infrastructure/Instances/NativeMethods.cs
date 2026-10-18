using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PdfPro.Infrastructure.Instances;

internal static class NativeMethods
{
    /// <summary>
    /// Windows only lets the process the user just started take the foreground. Before handing over its files, a
    /// second launch passes that right to the running instance so its window can come to the front.
    /// </summary>
    public static void AllowServerToTakeForeground(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) && serverProcessId != 0)
        {
            // Failure only means the window may flash in the taskbar instead of coming to the front.
            _ = AllowSetForegroundWindow(serverProcessId);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
