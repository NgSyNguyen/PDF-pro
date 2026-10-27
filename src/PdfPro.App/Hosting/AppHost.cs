using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Printing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels;
using PdfPro.App.Views;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Infrastructure;
using PdfPro.Operations;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Editing;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Merging;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Stamping;
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
        services.AddSingleton<IPageDialogService, PageDialogService>();
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
        services.AddSingleton<ScreenFonts>();
        services.AddSingleton<ScreenImages>();
        services.AddSingleton<SignatureLibrary>();
        services.AddHostedService<FontLibraryWarmup>();
        services.AddSingleton<IDocumentTabFactory, DocumentTabFactory>();
        services.AddSingleton<IPrintDialogService, PrintDialogService>();
        services.AddSingleton<IDocumentPrinter, DocumentPrinter>();

        // Document sessions, page operations and writing PDF files
        services.AddSingleton<ISourceRepairer, PdfiumSourceRepairer>();
        services.AddSingleton<PdfSharpPlanMaterializer>();
        services.AddSingleton<IPlanMaterializer>(sp => sp.GetRequiredService<PdfSharpPlanMaterializer>());
        services.AddOperation<RotatePagesOperation>();
        services.AddOperation<MovePagesOperation>();
        services.AddOperation<DeletePagesOperation>();
        services.AddOperation<DuplicatePagesOperation>();
        services.AddOperation<InsertBlankPagesOperation>();
        services.AddOperation<InsertPagesOperation>();
        services.AddOperation<CropPagesOperation>();
        services.AddOperation<ExtractPagesOperation>();
        services.AddOperation<ChangePageSizeOperation>();
        services.AddOperation<MarkSplitOperation>();
        services.AddOperation<SetSplitMarksOperation>();
        services.AddOperation<InterleaveOperation>();
        services.AddOperation<SplitOperation>();
        services.AddOperation<MergeOperation>();
        services.AddOperation<NUpOperation>();
        services.AddOperation<BookletOperation>();
        services.AddOperation<TileOperation>();
        services.AddOperation<OverlayOperation>();
        services.AddOperation<WatermarkOperation>();
        services.AddOperation<BackgroundOperation>();
        services.AddOperation<HeaderFooterOperation>();
        services.AddOperation<PageNumbersOperation>();
        services.AddOperation<BatesOperation>();
        services.AddOperation<RemoveStampsOperation>();
        services.AddOperation<AddAnnotationsOperation>();
        services.AddOperation<UpdateAnnotationsOperation>();
        services.AddOperation<DeleteAnnotationsOperation>();
        services.AddOperation<EditFileAnnotationsOperation>();
        services.AddOperation<FlattenAnnotationsOperation>();
        services.AddOperation<EditContentOperation>();
        services.AddOperation<FillFormOperation>();
        services.AddOperation<DesignFormOperation>();
        services.AddOperation<MergeFormDataOperation>();
        services.AddOperation<FlattenFormOperation>();
        services.AddOperation<ResetFormOperation>();
        services.AddSingleton<OperationCatalog>();
        services.AddSingleton<IDocumentWorkspaceService, DocumentWorkspaceService>();
        services.AddSingleton<IDocumentSaver, DocumentSaver>();
        services.AddSingleton<IContentChanger, ContentChanger>();
        services.AddSingleton<IMergeService, MergeService>();
        services.AddSingleton<IBatesService, BatesService>();
        services.AddSingleton<IRecoveryDialog, RecoveryDialog>();
        services.AddSingleton<IRecoveryService, RecoveryService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<IExitGuard>(sp => sp.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }

    /// <summary>Registers an operation once, reachable as itself and through the operation catalog.</summary>
    private static IServiceCollection AddOperation<T>(this IServiceCollection services)
        where T : class, IPdfOperation
    {
        services.AddSingleton<T>();
        services.AddSingleton<IPdfOperation>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
