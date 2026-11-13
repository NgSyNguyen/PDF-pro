using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace PdfPro.Infrastructure.Instances;

/// <summary>Command line of PdfPro.exe: files to open ("PdfPro.exe a.pdf b.pdf") and internal options.</summary>
/// <param name="Background">"--background": started with Windows for the hot folder — the window starts minimized.</param>
/// <param name="Merge">"--merge": the files are to be merged (Explorer's "Gộp bằng PDF_Pro"), not opened.</param>
public sealed record StartupArguments(IReadOnlyList<string> Files, int? WaitForProcessId, bool Background = false, bool Merge = false)
{
    /// <summary>
    /// "--merge": Explorer's "Gộp bằng PDF_Pro" starts PdfPro.exe once per selected file with it; the running window
    /// gathers the files and merges them together. Also the first item of such a hand-over message (a path never
    /// starts with "--": they are made absolute).
    /// </summary>
    public const string MergeOption = "--merge";

    /// <summary>"--background": started with Windows to watch the hot folder.</summary>
    public const string BackgroundOption = "--background";

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
        var background = false;
        var merge = false;
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

                background |= string.Equals(arg, BackgroundOption, StringComparison.OrdinalIgnoreCase);
                merge |= string.Equals(arg, MergeOption, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (TryGetFullPath(arg.Trim(), currentDirectory) is { } fullPath)
            {
                files.Add(fullPath);
            }
        }

        return new StartupArguments(files, waitFor, background, merge);
    }

    /// <summary>
    /// What a later launch hands to the running window: its files, after <see cref="MergeOption"/> when they are to be
    /// merged.
    /// </summary>
    public IReadOnlyList<string> HandOverMessage() => Merge ? [MergeOption, .. Files] : Files;

    /// <summary>Reads a hand-over message: whether its files are to be merged, and the files.</summary>
    public static (bool Merge, IReadOnlyList<string> Files) ReadHandOver(IReadOnlyList<string> message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Count > 0 && string.Equals(message[0], MergeOption, StringComparison.Ordinal)
            ? (true, message.Skip(1).ToList())
            : (false, message);
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
