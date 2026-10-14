using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Infrastructure;
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

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }
}
