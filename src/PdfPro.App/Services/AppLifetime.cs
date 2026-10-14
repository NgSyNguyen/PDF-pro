using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;

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

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
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
