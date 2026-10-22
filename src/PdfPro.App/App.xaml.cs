using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Hosting;
using PdfPro.App.Localization;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Instances;
using PdfPro.Infrastructure.Settings;
using Serilog;
using Serilog.Extensions.Logging;

namespace PdfPro.App;

public partial class App : Application
{
    /// <summary>How long a second launch waits for the running window to take its files.</summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a restarted instance waits for the previous one to exit.</summary>
    private static readonly TimeSpan RestartWaitTimeout = TimeSpan.FromSeconds(15);

    private IHost? _host;
    private GlobalExceptionHandler? _exceptionHandler;
    private SingleInstanceChannel? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? logsDirectory = null;
        try
        {
            var paths = new AppPaths();
            logsDirectory = paths.LogsDirectory;
            paths.EnsureCreated();

            Log.Logger = LoggingSetup.CreateLogger(paths.LogsDirectory);
            var startup = StartupArguments.Parse(e.Args, Environment.CurrentDirectory);
            if (TryHandOverToRunningInstance(startup, paths.DataRoot))
            {
                Shutdown(0);
                return;
            }

            Log.ForContext<App>().Information(
                "PDF_Pro {Version} starting on {OS} (.NET {Runtime})",
                AppBuildInfo.Current.Version,
                Environment.OSVersion,
                Environment.Version);

            _host = AppHost.Build(e.Args, paths);
            _exceptionHandler = _host.Services.GetRequiredService<GlobalExceptionHandler>();
            _exceptionHandler.Register(this);
            _host.Start();

            // Language and theme must be set before the first window is created.
            var settings = _host.Services.GetRequiredService<ISettingsService>().Current;
            _host.Services.GetRequiredService<LocalizationService>().ApplyStartupLanguage(settings.Language);
            var theme = _host.Services.GetRequiredService<AppThemeService>();
            theme.Apply(settings.Theme);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            theme.AttachMainWindow(mainWindow);
            mainWindow.Show();

            _ = OpenStartupFilesAsync(_host.Services.GetRequiredService<MainWindowViewModel>(), startup.Files);
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex, logsDirectory);
        }
    }

    /// <summary>
    /// Windows is signing out or shutting down: no questions. Unsaved documents keep their sessions and are offered
    /// for recovery at the next start.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_host?.Services.GetService<MainWindowViewModel>() is { } viewModel)
        {
            viewModel.ExitConfirmed = true;
        }

        Log.ForContext<App>().Information("Windows session ending ({Reason})", e.ReasonSessionEnding);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Later launches must not hand their files to a window that is closing.
        _instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (_host is not null)
        {
            _host.Services.GetService<MainWindowViewModel>()?.CloseAllDocuments();
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                Log.ForContext<App>().Warning(ex, "The host did not stop cleanly");
            }

            _exceptionHandler?.Unregister(this);
            _host.Dispose();
        }

        Log.ForContext<App>().Information("PDF_Pro exited with code {ExitCode}", e.ApplicationExitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Documents left unsaved by a crash first, then "PdfPro.exe a.pdf b.pdf" (also how Explorer's "Open with"
    /// starts the app).
    /// </summary>
    private static async Task OpenStartupFilesAsync(MainWindowViewModel viewModel, IReadOnlyList<string> files)
    {
        await viewModel.RecoverSessionsAsync();
        if (files.Count > 0)
        {
            await viewModel.OpenFilesAsync(files);
        }

        await viewModel.RemoveMissingRecentFilesAsync();
    }

    /// <summary>
    /// Single instance per user session: the first launch listens for later ones; a later launch hands its files
    /// to the running window and returns true so this process exits. If the running instance does not answer
    /// (hung or closing), this launch opens its own window instead.
    /// </summary>
    private bool TryHandOverToRunningInstance(StartupArguments startup, string dataRoot)
    {
        var log = Log.ForContext<App>();
        if (startup.WaitForProcessId is { } previous && !StartupArguments.WaitForProcessExit(previous, RestartWaitTimeout))
        {
            log.Warning("The previous instance did not exit within {Timeout}", RestartWaitTimeout);
        }

        var loggers = new SerilogLoggerFactory(Log.Logger);
        var channel = SingleInstanceChannel.Create(SingleInstanceChannel.DefaultName(dataRoot), loggers.CreateLogger<SingleInstanceChannel>());
        if (channel.IsPrimary)
        {
            _instance = channel;
            channel.StartListening(files => Dispatcher.BeginInvoke(() => OnFilesHandedOver(files)));
            return false;
        }

        try
        {
            if (channel.SendAsync(startup.Files, HandOverTimeout).GetAwaiter().GetResult())
            {
                log.Information("Handed {Count} file(s) over to the running instance", startup.Files.Count);
                return true;
            }

            log.Warning("The running instance did not take the files; opening another window");
            return false;
        }
        finally
        {
            channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Another launch passed its files (or none): bring the window forward and open them.</summary>
    private async void OnFilesHandedOver(IReadOnlyList<string> files)
    {
        if (_host is null || MainWindow is not MainWindow window)
        {
            return; // startup failed or the app is closing
        }

        window.BringToFront();
        if (files.Count > 0)
        {
            await _host.Services.GetRequiredService<MainWindowViewModel>().OpenFilesAsync(files);
        }
    }

    private void HandleStartupFailure(Exception exception, string? logsDirectory)
    {
        Log.ForContext<App>().Fatal(exception, "Startup failed");
        Log.CloseAndFlush();

        MessageBox.Show(
            string.Format(CultureInfo.CurrentCulture, Strings.Error_Startup_Message, logsDirectory ?? @"%LOCALAPPDATA%\PdfPro\logs"),
            Strings.App_Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }
}
