using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfPro.App.Localization;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Forms;
using PdfPro.App.Services.Optimizing;
using PdfPro.App.Services.Printing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Services.Security;
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
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
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
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IShellIntegration, ShellIntegration>();
        services.AddSingleton<Services.Updates.IUpdateService, Services.Updates.VelopackUpdateService>();
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
        services.AddOperation<ProtectDocumentOperation>();
        services.AddOperation<UnprotectDocumentOperation>();
        services.AddOperation<SetProtectionOperation>();
        services.AddOperation<RedactOperation>();
        services.AddOperation<SanitizeOperation>();
        services.AddSingleton<ITimestampClient>(_ => new HttpTimestampClient(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddSingleton<IRevocationFetcher>(_ => new HttpRevocationFetcher(new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddOperation<SignOperation>();
        services.AddSingleton<OperationCatalog>();
        services.AddSingleton<IDocumentWorkspaceService, DocumentWorkspaceService>();
        services.AddSingleton<IDocumentSaver, DocumentSaver>();
        services.AddSingleton<IContentChanger, ContentChanger>();

        // PdfPro.Worker (SPEC Phase 13): heavy operations run apart from the app, in a process limited in memory.
        services.AddSingleton<Func<PdfPro.Infrastructure.Worker.IWorkerClient>>(sp => () =>
        {
            var settings = sp.GetRequiredService<PdfPro.Infrastructure.Settings.ISettingsService>().Current;
            var program = System.IO.Path.Combine(AppContext.BaseDirectory, "PdfPro.Worker.exe");
            return new PdfPro.Infrastructure.Worker.WorkerClient(
                new PdfPro.Infrastructure.Worker.WorkerOptions(program, (long)settings.WorkerMemoryMegabytes << 20, TimeSpan.FromHours(4)) { DataRoot = sp.GetRequiredService<AppPaths>().DataRoot },
                sp.GetRequiredService<ILogger<PdfPro.Infrastructure.Worker.WorkerClient>>());
        });
        services.AddSingleton(sp => sp.GetRequiredService<Func<PdfPro.Infrastructure.Worker.IWorkerClient>>()());
        services.AddSingleton<IOperationIsolation, WorkerIsolation>();
        services.AddSingleton<Services.Optimizing.IExportRunner, Services.Optimizing.WorkerExportRunner>();
        services.AddSingleton<IMergeService, MergeService>();
        services.AddSingleton<IBatesService, BatesService>();
        services.AddSingleton<ICertificateStore, CertificateStore>();
        services.AddSingleton<ISignatureVerification, SignatureVerification>();
        services.AddSingleton(sp => QpdfOptions.In(sp.GetRequiredService<AppPaths>().ToolsDirectory));
        services.AddSingleton<Qpdf>();
        services.AddOperation<LinearizeOperation>();
        services.AddOperation<RepairOperation>();
        services.AddOperation<CompressOperation>();
        services.AddOperation<GrayscaleOperation>();
        services.AddOperation<Operations.Converting.ToImagesOperation>();
        services.AddOperation<Operations.Converting.FromImagesOperation>();
        services.AddOperation<Operations.Converting.ToTextOperation>();
        services.AddSingleton<Operations.Converting.ILibreOfficeLocator>(_ => new Operations.Converting.LibreOfficeLocator());
        services.AddOperation<Operations.Converting.OfficeToPdfOperation>();
        services.AddOperation<Operations.Converting.ToPdfAOperation>();
        services.AddSingleton<Operations.Converting.IVeraPdfLocator>(_ => new Operations.Converting.VeraPdfLocator());
        services.AddSingleton<Operations.Converting.VeraPdfValidator>();
        services.AddSingleton<PdfPro.Ocr.ITesseractLocator>(sp => new PdfPro.Ocr.TesseractLocator(sp.GetRequiredService<AppPaths>().ToolsDirectory));
        services.AddSingleton<PdfPro.Core.Ocr.IOcrEngine>(sp =>
        {
            var settings = sp.GetRequiredService<PdfPro.Infrastructure.Settings.ISettingsService>();
            return new PdfPro.Ocr.TesseractEngine(
                sp.GetRequiredService<PdfPro.Core.ExternalTools.IExternalToolRunner>(),
                sp.GetRequiredService<PdfPro.Ocr.ITesseractLocator>(),
                () => new PdfPro.Ocr.TesseractSettings(string.IsNullOrWhiteSpace(settings.Current.TesseractPath) ? null : settings.Current.TesseractPath.Trim(), settings.Current.OcrData));
        });
        services.AddOperation<Operations.Ocr.OcrOperation>();
        services.AddOperation<Operations.Scanning.RemoveBlankPagesOperation>();
        services.AddOperation<Operations.Scanning.AutoRotateOperation>();
        services.AddOperation<Operations.Scanning.DeskewOperation>();
        services.AddOperation<Operations.Scanning.CleanBordersOperation>();
        services.AddSingleton(sp => new Services.Scanning.ScanServices(
            sp.GetRequiredService<PdfPro.Pdfium.PdfiumGate>(),
            sp.GetRequiredService<Operations.Scanning.RemoveBlankPagesOperation>(),
            sp.GetRequiredService<Operations.Scanning.AutoRotateOperation>(),
            sp.GetRequiredService<Operations.Scanning.DeskewOperation>(),
            sp.GetRequiredService<Operations.Scanning.CleanBordersOperation>(),
            sp.GetRequiredService<PdfPro.Core.Ocr.IOcrEngine>()));
        services.AddSingleton<Services.Comparing.IDocumentComparison>(sp => new Services.Comparing.DocumentComparison(
            sp.GetRequiredService<PdfPro.Pdfium.PdfiumGate>(),
            sp.GetRequiredService<Services.Optimizing.IExportRunner>()));
        services.AddSingleton(sp => new Services.Comparing.CompareServices(
            sp.GetRequiredService<Services.Comparing.IDocumentComparison>(),
            sp.GetRequiredService<PdfPro.Pdfium.IPdfDocumentLoader>(),
            sp.GetRequiredService<Services.Rendering.IPageImageRenderer>(),
            sp.GetRequiredService<IPasswordPrompt>()));
        services.AddOperation<Operations.Extracting.ExtractTablesOperation>();
        services.AddOperation<Operations.Extracting.ExtractImagesOperation>();
        services.AddOperation<Operations.Extracting.EditAttachmentsOperation>();
        services.AddOperation<Operations.Structure.SetBookmarksOperation>();
        services.AddOperation<Operations.Structure.SetMetadataOperation>();
        services.AddOperation<Operations.Structure.SetPageLabelsOperation>();
        services.AddOperation<Operations.Structure.EditLinksOperation>();
        services.AddSingleton(sp => new Services.Extracting.ExtractingServices(
            sp.GetRequiredService<Operations.Extracting.ExtractTablesOperation>(),
            sp.GetRequiredService<PdfPro.Pdfium.IPdfDocumentLoader>(),
            sp.GetRequiredService<Services.Rendering.IPageImageRenderer>(),
            sp.GetRequiredService<Operations.Extracting.ExtractImagesOperation>(),
            sp.GetRequiredService<Services.Optimizing.IExportRunner>()));
        services.AddSingleton<ICompressPreviews, CompressPreviews>();
        services.AddSingleton(sp => new OptimizingServices(
            sp.GetRequiredService<LinearizeOperation>(),
            sp.GetRequiredService<RepairOperation>(),
            sp.GetRequiredService<CompressOperation>(),
            sp.GetRequiredService<ICompressPreviews>(),
            sp.GetRequiredService<GrayscaleOperation>(),
            sp.GetRequiredService<Operations.Converting.ToImagesOperation>(),
            sp.GetRequiredService<Operations.Converting.FromImagesOperation>(),
            sp.GetRequiredService<Operations.Converting.ToTextOperation>(),
            sp.GetRequiredService<Operations.Converting.OfficeToPdfOperation>(),
            sp.GetRequiredService<Operations.Converting.ILibreOfficeLocator>(),
            sp.GetRequiredService<Operations.Converting.ToPdfAOperation>(),
            sp.GetRequiredService<Operations.Converting.VeraPdfValidator>(),
            sp.GetRequiredService<Operations.Converting.IVeraPdfLocator>()));
        services.AddSingleton(sp => new SigningServices(
            sp.GetRequiredService<ICertificateStore>(),
            sp.GetRequiredService<ITimestampClient>(),
            new PdfFormSource(),
            sp.GetRequiredService<SignatureLibrary>(),
            sp.GetRequiredService<IRevocationFetcher>()));
        services.AddSingleton(sp => new Operations.Automation.WorkflowRunner(sp.GetRequiredService<OperationCatalog>(), sp.GetRequiredService<PdfPro.Pdfium.PdfiumGate>(), sp.GetRequiredService<PdfPro.Core.Ocr.IOcrEngine>()));
        services.AddSingleton(sp => new Services.Automation.WorkerWorkflowRunner(
            sp.GetRequiredService<Operations.Automation.WorkflowRunner>(),
            sp.GetRequiredService<Func<PdfPro.Infrastructure.Worker.IWorkerClient>>(),
            sp.GetRequiredService<PdfPro.Infrastructure.Settings.ISettingsService>(),
            sp.GetRequiredService<ILogger<Services.Automation.WorkerWorkflowRunner>>()));
        services.AddSingleton(sp => new Services.Automation.AutomationServices(
            sp.GetRequiredService<OperationCatalog>(),
            sp.GetRequiredService<Operations.Automation.WorkflowRunner>(),
            new Services.Automation.WorkflowLibrary(sp.GetRequiredService<AppPaths>().WorkflowsDirectory),
            sp.GetRequiredService<Services.Automation.WorkerWorkflowRunner>()));
        services.AddSingleton<Services.Automation.IWorkflowLibrary>(sp => sp.GetRequiredService<Services.Automation.AutomationServices>().Library);
        services.AddSingleton<Services.Automation.HotFolderService>();
        services.AddSingleton<Services.Automation.IHotFolderService>(sp => sp.GetRequiredService<Services.Automation.HotFolderService>());
        services.AddHostedService(sp => sp.GetRequiredService<Services.Automation.HotFolderService>());
        services.AddSingleton<Services.Automation.IStartupRegistration, Services.Automation.WindowsStartupRegistration>();
        services.AddSingleton<IRecoveryDialog, RecoveryDialog>();
        services.AddSingleton<IRecoveryService, RecoveryService>();

        services.AddSingleton<PdfPro.Infrastructure.Instances.IOpenDocumentGuard, PdfPro.Infrastructure.Instances.OpenDocumentGuard>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton(sp => new MergeRequestCollector(
            sp.GetRequiredService<IDebouncerFactory>(),
            files => sp.GetRequiredService<MainWindowViewModel>().MergeFilesAsync(files)));
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
