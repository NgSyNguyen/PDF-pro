using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Infrastructure;
using PdfPro.Pdfium;
using Serilog;

namespace PdfPro.App.Hosting;

internal static class AppHost
{
    /// <summary>Builds the generic host. Serilog must already be configured through <see cref="Log.Logger"/>.</summary>
    public static IHost Build(string[] args, AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "PdfPro",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddPdfProInfrastructure(paths);
        builder.Services.AddPdfProApp();
        return builder.Build();
    }

    private static IServiceCollection AddPdfProApp(this IServiceCollection services)
    {
        services.AddSingleton(AppBuildInfo.Current);
        services.AddSingleton<GlobalExceptionHandler>();

        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<IAppThemeService>(sp => sp.GetRequiredService<AppThemeService>());
        services.AddSingleton<IErrorDialogService, ErrorDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAppLifetime, AppLifetime>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IPasswordPrompt, PasswordPrompt>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IDebouncerFactory, DispatcherDebouncerFactory>();

        // PDF engine and viewing
        services.AddSingleton<PdfiumGate>();
        services.AddHostedService<PdfiumGateLifetime>();
        services.AddSingleton<IPdfDocumentLoader, PdfDocumentLoader>();
        services.AddSingleton<IDocumentOpener, DocumentOpener>();
        services.AddSingleton<RenderCache>();
        services.AddSingleton<IPageImageRenderer, PageImageRenderer>();
        services.AddSingleton<IThumbnailProvider, ThumbnailProvider>();
        services.AddHostedService<ThumbnailCacheMaintenance>();
        services.AddSingleton<IDocumentTabFactory, DocumentTabFactory>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }
}
