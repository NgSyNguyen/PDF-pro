using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PdfPro.App.Services;

/// <summary>
/// Opens folders in Explorer, and files the app wrote in the program Windows opens them with (Explorer hands them
/// over). This is a fire-and-forget UI hand-off, not a tool run whose output we wait for, so it does not go through
/// IExternalToolRunner; it still avoids the shell and passes the path via ArgumentList.
/// </summary>
internal sealed class ShellLauncher(ILogger<ShellLauncher> logger) : IShellLauncher
{
    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            Directory.CreateDirectory(path);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(path);
            using var _ = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not open a folder in Explorer");
        }
    }

    public void OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
            startInfo.ArgumentList.Add(path);
            using var _ = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not open a file in its program: {ErrorType}", ex.GetType().Name);
        }
    }
}
