using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services;

namespace PdfPro.App.Hosting;

/// <summary>
/// Last line of defence: every unhandled exception is logged with an incident id and the user sees a
/// friendly Vietnamese/English message instead of a silent crash.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IErrorDialogService dialogs)
{
    private int _isDialogOpen;

    public void Register(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public void Unregister(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    /// <summary>Logs the error and shows the dialog unless one is already open. Returns the incident id.</summary>
    internal string HandleRecoverable(Exception exception, string source)
    {
        var incidentId = CreateIncidentId();
        logger.LogError(exception, "Unhandled exception from {Source} (incident {IncidentId})", source, incidentId);

        // A burst of errors (e.g. one per render tick) must not stack dialogs; they are all logged.
        if (Interlocked.CompareExchange(ref _isDialogOpen, 1, 0) == 0)
        {
            _ = ShowDialogAsync(incidentId);
        }

        return incidentId;
    }

    /// <summary>The process is about to terminate: log, flush and tell the user. Returns the incident id.</summary>
    internal string HandleFatal(Exception? exception)
    {
        var incidentId = CreateIncidentId();
        logger.LogCritical(exception, "Fatal unhandled exception, the application will close (incident {IncidentId})", incidentId);
        Serilog.Log.CloseAndFlush();

        try
        {
            dialogs.ShowFatalError(incidentId);
        }
        catch (Exception dialogError) when (dialogError is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Nothing more can be done while the process is going down.
        }

        return incidentId;
    }

    private static string CreateIncidentId() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task ShowDialogAsync(string incidentId)
    {
        try
        {
            await dialogs.ShowUnexpectedErrorAsync(incidentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not show the error dialog for incident {IncidentId}", incidentId);
        }
        finally
        {
            Volatile.Write(ref _isDialogOpen, 0);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive: the user may have unsaved work.
        e.Handled = true;
        HandleRecoverable(e.Exception, "UI thread");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        HandleRecoverable(e.Exception, "background task");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        if (e.IsTerminating)
        {
            HandleFatal(exception);
        }
        else if (exception is not null)
        {
            HandleRecoverable(exception, "app domain");
        }
    }
}
