using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PdfPro.App.Hosting;
using PdfPro.App.Localization;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Settings;
using Serilog;

namespace PdfPro.App;

public partial class App : Application
{
    private IHost? _host;
    private GlobalExceptionHandler? _exceptionHandler;

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

            _ = OpenStartupFilesAsync(_host.Services.GetRequiredService<MainWindowViewModel>(), e.Args);
        }
        catch (Exception ex)
        {
            HandleStartupFailure(ex, logsDirectory);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
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

    /// <summary>"PdfPro.exe a.pdf b.pdf" (also how Explorer's "Open with" starts the app).</summary>
    private static async Task OpenStartupFilesAsync(MainWindowViewModel viewModel, string[] args)
    {
        var files = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count > 0)
        {
            await viewModel.OpenFilesAsync(files);
        }

        await viewModel.RemoveMissingRecentFilesAsync();
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
