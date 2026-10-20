using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Printing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure;
using PdfPro.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
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
        services.AddSingleton<IPrintDialogService, PrintDialogService>();
        services.AddSingleton<IDocumentPrinter, DocumentPrinter>();

        // Document sessions, page operations and writing PDF files
        services.AddSingleton<ISourceRepairer, PdfiumSourceRepairer>();
        services.AddSingleton<IPlanMaterializer, PdfSharpPlanMaterializer>();
        services.AddPlanOperation<RotatePagesOperation>();
        services.AddPlanOperation<MovePagesOperation>();
        services.AddPlanOperation<DeletePagesOperation>();
        services.AddPlanOperation<DuplicatePagesOperation>();
        services.AddPlanOperation<InsertBlankPagesOperation>();
        services.AddPlanOperation<InsertPagesOperation>();
        services.AddPlanOperation<CropPagesOperation>();
        services.AddSingleton<OperationCatalog>();
        services.AddSingleton<IDocumentWorkspaceService, DocumentWorkspaceService>();
        services.AddSingleton<IDocumentSaver, DocumentSaver>();
        services.AddSingleton<IRecoveryDialog, RecoveryDialog>();
        services.AddSingleton<IRecoveryService, RecoveryService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IExitGuard>(sp => sp.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }

    /// <summary>Registers a page operation once, reachable as itself and through the operation catalog.</summary>
    private static IServiceCollection AddPlanOperation<T>(this IServiceCollection services)
        where T : class, IPdfOperation
    {
        services.AddSingleton<T>();
        services.AddSingleton<IPdfOperation>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
