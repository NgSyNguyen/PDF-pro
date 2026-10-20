using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Instances;

namespace PdfPro.App.Services;

internal sealed class AppLifetime(IServiceProvider services, ILogger<AppLifetime> logger) : IAppLifetime
{
    // Resolved when used: the guard (the main window's view model) depends on this service itself.
    private IExitGuard Guard => services.GetRequiredService<IExitGuard>();

    public async Task<bool> ShutdownAsync()
    {
        if (!await Guard.ConfirmExitAsync())
        {
            return false;
        }

        Application.Current.Shutdown();
        return true;
    }

    public async Task<bool> RestartAsync()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            logger.LogWarning("Cannot restart: the executable path is unknown");
            return false;
        }

        var guard = Guard;
        if (!await guard.ConfirmExitAsync())
        {
            return false;
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
            guard.CancelExit();
            return false;
        }

        logger.LogInformation("Restarting the application");
        Application.Current.Shutdown();
        return true;
    }
}
