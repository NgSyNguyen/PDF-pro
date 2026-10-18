using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Instances;

namespace PdfPro.App.Services;

internal sealed class AppLifetime(ILogger<AppLifetime> logger) : IAppLifetime
{
    public void Shutdown() => Application.Current.Shutdown();

    public void Restart()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            logger.LogWarning("Cannot restart: the executable path is unknown");
            return;
        }

        // The new instance waits for this one to exit; otherwise it would find it running and hand over to it.
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add(StartupArguments.WaitForProcessOption);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var _ = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            logger.LogError(ex, "Could not start a new instance for the restart");
            return;
        }

        logger.LogInformation("Restarting the application");
        Application.Current.Shutdown();
    }
}
