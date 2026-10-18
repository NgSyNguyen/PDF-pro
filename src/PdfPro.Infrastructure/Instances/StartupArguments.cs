using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace PdfPro.Infrastructure.Instances;

/// <summary>Command line of PdfPro.exe: files to open ("PdfPro.exe a.pdf b.pdf") and internal options.</summary>
public sealed record StartupArguments(IReadOnlyList<string> Files, int? WaitForProcessId)
{
    /// <summary>"--wait-for-pid 1234": wait for that process (the instance being restarted) to exit first.</summary>
    public const string WaitForProcessOption = "--wait-for-pid";

    /// <summary>
    /// Files are the arguments that are not options, made absolute against <paramref name="currentDirectory"/>
    /// because the instance that opens them may run in another folder. Unknown "--" options are ignored.
    /// </summary>
    public static StartupArguments Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var files = new List<string>();
        int? waitFor = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(arg, WaitForProcessOption, StringComparison.OrdinalIgnoreCase)
                    && i + 1 < args.Count
                    && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
                    && processId > 0)
                {
                    waitFor = processId;
                    i++;
                }

                continue;
            }

            if (TryGetFullPath(arg.Trim(), currentDirectory) is { } fullPath)
            {
                files.Add(fullPath);
            }
        }

        return new StartupArguments(files, waitFor);
    }

    /// <summary>
    /// Waits until another process has exited (a restart must not hand its files back to the closing instance).
    /// True when it exited (or was already gone) within <paramref name="timeout"/>.
    /// </summary>
    public static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // not running any more
        }
        catch (Win32Exception)
        {
            return false; // not allowed to wait for it
        }
    }

    private static string? TryGetFullPath(string path, string currentDirectory)
    {
        try
        {
            return Path.GetFullPath(path, currentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
