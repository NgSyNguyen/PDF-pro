using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Printing;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Settings;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>Main window: ribbon, workspace tabs (home + documents), sidebar and status bar.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IAppLifetime _lifetime;
    private readonly IShellLauncher _shell;
    private readonly AppPaths _paths;
    private readonly IDocumentOpener _opener;
    private readonly IDocumentTabFactory _tabFactory;
    private readonly ISettingsService _settings;
    private readonly IFileDialogService _fileDialogs;
    private readonly IMessageService _messages;
    private readonly IPrintDialogService _printDialogs;
    private readonly IDocumentPrinter _printer;
    private readonly ILogger<MainWindowViewModel> _logger;
    private ActivePrint? _activePrint;

    public MainWindowViewModel(
        IDialogService dialogs,
        IAppLifetime lifetime,
        IShellLauncher shell,
        AppPaths paths,
        AppBuildInfo buildInfo,
        IDocumentOpener opener,
        IDocumentTabFactory tabFactory,
        ISettingsService settings,
        IFileDialogService fileDialogs,
        IMessageService messages,
        IPrintDialogService printDialogs,
        IDocumentPrinter printer,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        _dialogs = dialogs;
        _lifetime = lifetime;
        _shell = shell;
        _paths = paths;
        _opener = opener;
        _tabFactory = tabFactory;
        _settings = settings;
        _fileDialogs = fileDialogs;
        _messages = messages;
        _printDialogs = printDialogs;
        _printer = printer;
        _logger = logger;

        Home = new HomeTabViewModel(OpenCommand, OpenRecentFileCommand);
        ShowRecentFiles(settings.Current.RecentFiles);
        RibbonTabs = BuildRibbon(buildInfo.IsDebugBuild);
        Tabs = [Home];
        SelectedTab = Home;
        StatusBar.PropertyChanged += OnStatusBarPropertyChanged;
    }

    public IReadOnlyList<RibbonTabViewModel> RibbonTabs { get; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    public HomeTabViewModel Home { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocument), nameof(HasActiveDocument))]
    [NotifyCanExecuteChangedFor(nameof(CloseActiveDocumentCommand), nameof(ShowPropertiesCommand), nameof(PrintCommand))]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    /// <summary>The selected document tab, or null when the home tab is selected.</summary>
    public DocumentTabViewModel? ActiveDocument => SelectedTab as DocumentTabViewModel;

    public bool HasActiveDocument => ActiveDocument is not null;

    public IEnumerable<DocumentTabViewModel> Documents => Tabs.OfType<DocumentTabViewModel>();

    public SidebarViewModel Sidebar { get; } = new();

    public StatusBarViewModel StatusBar { get; } = new();

    /// <summary>
    /// Opens files one after the other (each in its own tab). A file that is already open is just selected.
    /// With <paramref name="pdfOnly"/>, files without the .pdf extension are skipped and the user is told.
    /// </summary>
    public async Task OpenFilesAsync(IEnumerable<string> paths, bool pdfOnly = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var candidates = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var files = pdfOnly
            ? candidates.Where(p => string.Equals(Path.GetExtension(p), ".pdf", StringComparison.OrdinalIgnoreCase)).ToList()
            : candidates;
        if (files.Count < candidates.Count)
        {
            await _messages.ShowInfoAsync(Strings.Open_Error_Title, Strings.Open_NotPdf);
        }

        foreach (var path in files)
        {
            var fullPath = Path.GetFullPath(path);
            if (Documents.FirstOrDefault(d => d.IsSameFile(fullPath)) is { } open)
            {
                SelectedTab = open;
                continue;
            }

            var document = await _opener.OpenAsync(fullPath);
            if (document is null)
            {
                if (!File.Exists(fullPath))
                {
                    await UpdateRecentFilesAsync(list => RecentFileList.Remove(list, fullPath));
                }

                continue;
            }

            var tab = _tabFactory.Create(document, fullPath);
            Tabs.Add(tab);
            SelectedTab = tab;
            await UpdateRecentFilesAsync(list => RecentFileList.Add(list, fullPath));
        }
    }

    /// <summary>Drops recent files that no longer exist (runs the file checks off the UI thread).</summary>
    public async Task RemoveMissingRecentFilesAsync()
    {
        var current = _settings.Current.RecentFiles;
        var existing = await Task.Run(() => RecentFileList.RemoveMissing(current, File.Exists));
        if (existing.Count != current.Count)
        {
            await UpdateRecentFilesAsync(list => RecentFileList.RemoveMissing(list, existing.Contains));
        }
    }

    /// <summary>Closes every document at application exit (synchronously, the UI is going away).</summary>
    public void CloseAllDocuments()
    {
        _activePrint?.Status.Cancel();
        var documents = Documents.ToList();
        foreach (var document in documents)
        {
            Tabs.Remove(document);
        }

        var closing = documents.Select(d => d.DisposeAsync().AsTask()).ToArray();
        if (!Task.WaitAll(closing, TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Some documents did not close within 5 seconds");
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var files = _fileDialogs.PickPdfFiles();
        if (files.Count > 0)
        {
            await OpenFilesAsync(files);
        }
    }

    [RelayCommand]
    private Task OpenRecentFileAsync(string? path)
        => string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : OpenFilesAsync([path]);

    [RelayCommand]
    private async Task CloseTabAsync(WorkspaceTabViewModel? tab)
    {
        if (tab is not DocumentTabViewModel document || !Tabs.Contains(document))
        {
            return;
        }

        var index = Tabs.IndexOf(document);
        Tabs.Remove(document);
        if (SelectedTab is null || ReferenceEquals(SelectedTab, document))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        // A document being printed is closed only after its print job has stopped using it.
        if (_activePrint is { } print && ReferenceEquals(print.Document, document))
        {
            print.Status.Cancel();
            await Task.WhenAny(print.Completion);
        }

        await document.DisposeAsync();
    }

    /// <summary>
    /// Ctrl+P: the system print dialog, then printing in the background with progress and cancel in the status bar.
    /// Documents whose permissions forbid printing are refused with an explanation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task PrintAsync()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (!document.Document.Features.CanPrint)
        {
            await _messages.ShowInfoAsync(Strings.Print_Error_Title, Strings.Print_NotAllowed);
            return;
        }

        var job = _printDialogs.ShowPrintDialog(document.Document.PageCount, document.Viewer.CurrentPageIndex);
        if (job is null || job.PageIndices.Count == 0)
        {
            return;
        }

        var total = job.PageIndices.Count;
        var task = StatusBar.StartTask(PrintProgressText(0, total));
        var progress = new Progress<int>(done => task.Report((double)done / total, PrintProgressText(done, total)));
        var printing = _printer.PrintAsync(document.Document, job, document.Title, progress, task.Token);
        _activePrint = new ActivePrint(document, task, printing);
        try
        {
            var outcome = await printing;
            task.Complete(outcome switch
            {
                { Cancelled: true } => Strings.Print_Cancelled,
                { FailedPages: > 0 } => string.Format(CultureInfo.CurrentCulture, Strings.Print_DoneWithFailures, total, outcome.FailedPages),
                _ => string.Format(CultureInfo.CurrentCulture, Strings.Print_Done, total),
            });
        }
        catch (PrintFailedException ex)
        {
            _logger.LogError(ex, "Printing failed");
            task.Complete(Strings.Print_FailedStatus);
            await _messages.ShowErrorAsync(Strings.Print_Error_Title, Strings.Print_FailedMessage);
        }
        finally
        {
            _activePrint = null;
        }
    }

    private bool CanPrint() => HasActiveDocument && !StatusBar.IsTaskRunning;

    private static string PrintProgressText(int done, int total)
        => string.Format(CultureInfo.CurrentCulture, Strings.Print_Progress, Math.Min(done + 1, total), total);

    private void OnStatusBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusBarViewModel.IsTaskRunning))
        {
            PrintCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private Task CloseActiveDocumentAsync() => CloseTabAsync(ActiveDocument);

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ShowProperties()
    {
        if (ActiveDocument is { } document)
        {
            _dialogs.ShowDocumentProperties(document);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _dialogs.ShowSettings();

    [RelayCommand]
    private void Exit() => _lifetime.Shutdown();

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_paths.LogsDirectory);

    /// <summary>Debug builds only: proves that unhandled errors are logged and shown to the user.</summary>
    [RelayCommand]
#pragma warning disable CA1822 // Command methods must be instance members for the MVVM source generator.
    private void ThrowTestException()
#pragma warning restore CA1822
        => throw new InvalidOperationException("Test exception requested from the Diagnostics ribbon group.");

    private async Task UpdateRecentFilesAsync(Func<IReadOnlyList<string>, IReadOnlyList<string>> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { RecentFiles = change(s.RecentFiles) });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The recent files list could not be saved");
        }

        ShowRecentFiles(_settings.Current.RecentFiles);
    }

    private void ShowRecentFiles(IReadOnlyList<string> paths)
    {
        Home.RecentFiles.Clear();
        foreach (var path in paths)
        {
            Home.RecentFiles.Add(new RecentFileViewModel(path));
        }
    }

    private List<RibbonTabViewModel> BuildRibbon(bool isDebugBuild)
    {
        List<RibbonCommandViewModel> diagnostics = [new("tools.openLogFolder", Strings.Cmd_OpenLogFolder, SymbolRegular.DocumentBulletList24, OpenLogFolderCommand)];
        if (isDebugBuild)
        {
            diagnostics.Add(new("tools.throwTestException", Strings.Cmd_ThrowTestException, SymbolRegular.Bug24, ThrowTestExceptionCommand));
        }

        return
        [
            new("file", Strings.Ribbon_Tab_File,
            [
                new(Strings.Ribbon_Group_Document,
                [
                    new("file.open", Strings.Cmd_Open, SymbolRegular.FolderOpen24, OpenCommand),
                    new("file.save", Strings.Cmd_Save, SymbolRegular.Save24),
                    new("file.saveAs", Strings.Cmd_SaveAs, SymbolRegular.SaveEdit24),
                    new("file.print", Strings.Cmd_Print, SymbolRegular.Print24, PrintCommand),
                    new("file.properties", Strings.Cmd_Properties, SymbolRegular.DocumentData24, ShowPropertiesCommand),
                    new("file.close", Strings.Cmd_Close, SymbolRegular.Dismiss24, CloseActiveDocumentCommand),
                ]),
                new(Strings.Ribbon_Group_App,
                [
                    new("app.settings", Strings.Cmd_Settings, SymbolRegular.Settings24, OpenSettingsCommand),
                    new("app.exit", Strings.Cmd_Exit, SymbolRegular.SignOut24, ExitCommand),
                ]),
            ]),
            new("page", Strings.Ribbon_Tab_Page,
            [
                new(Strings.Ribbon_Group_Arrange,
                [
                    new("organize.rotateLeft", Strings.Cmd_RotateLeft, SymbolRegular.ArrowRotateCounterclockwise24),
                    new("organize.rotateRight", Strings.Cmd_RotateRight, SymbolRegular.ArrowRotateClockwise24),
                    new("organize.delete", Strings.Cmd_DeletePages, SymbolRegular.Delete24),
                    new("organize.duplicate", Strings.Cmd_DuplicatePages, SymbolRegular.DocumentCopy24),
                ]),
                new(Strings.Ribbon_Group_Insert,
                [
                    new("organize.insertBlank", Strings.Cmd_InsertBlank, SymbolRegular.DocumentAdd24),
                    new("organize.insertFromFile", Strings.Cmd_InsertFromFile, SymbolRegular.DocumentArrowLeft24),
                    new("organize.extract", Strings.Cmd_ExtractPages, SymbolRegular.DocumentArrowRight24),
                ]),
                new(Strings.Ribbon_Group_Layout,
                [
                    new("organize.crop", Strings.Cmd_Crop, SymbolRegular.Crop24),
                    new("organize.pageSize", Strings.Cmd_PageSize, SymbolRegular.DocumentLandscape24),
                ]),
                new(Strings.Ribbon_Group_MergeSplit,
                [
                    new("organize.merge", Strings.Cmd_Merge, SymbolRegular.Merge24),
                    new("organize.split", Strings.Cmd_Split, SymbolRegular.SplitVertical24),
                ]),
            ]),
            new("edit", Strings.Ribbon_Tab_Edit,
            [
                new(Strings.Ribbon_Group_History,
                [
                    new("edit.undo", Strings.Cmd_Undo, SymbolRegular.ArrowUndo24),
                    new("edit.redo", Strings.Cmd_Redo, SymbolRegular.ArrowRedo24),
                ]),
                new(Strings.Ribbon_Group_Add,
                [
                    new("edit.addText", Strings.Cmd_AddText, SymbolRegular.TextAddT24),
                    new("edit.addImage", Strings.Cmd_AddImage, SymbolRegular.ImageAdd24),
                    new("edit.addShape", Strings.Cmd_AddShape, SymbolRegular.Shapes24),
                    new("edit.handSignature", Strings.Cmd_HandSignature, SymbolRegular.Signature24),
                ]),
                new(Strings.Ribbon_Group_Stamp,
                [
                    new("stamp.watermark", Strings.Cmd_Watermark, SymbolRegular.Water24),
                    new("stamp.headerFooter", Strings.Cmd_HeaderFooter, SymbolRegular.DocumentHeader24),
                    new("stamp.pageNumbers", Strings.Cmd_PageNumbers, SymbolRegular.NumberSymbol24),
                ]),
                new(Strings.Ribbon_Group_Content,
                [
                    new("edit.content", Strings.Cmd_EditContent, SymbolRegular.DocumentEdit24),
                ]),
            ]),
            new("annotate", Strings.Ribbon_Tab_Annotate,
            [
                new(Strings.Ribbon_Group_Markup,
                [
                    new("annotate.highlight", Strings.Cmd_Highlight, SymbolRegular.Highlight24),
                    new("annotate.underline", Strings.Cmd_Underline, SymbolRegular.TextUnderline24),
                    new("annotate.strikeout", Strings.Cmd_Strikeout, SymbolRegular.TextStrikethrough24),
                ]),
                new(Strings.Ribbon_Group_Notes,
                [
                    new("annotate.note", Strings.Cmd_StickyNote, SymbolRegular.Note24),
                    new("annotate.textBox", Strings.Cmd_TextBox, SymbolRegular.Textbox24),
                ]),
                new(Strings.Ribbon_Group_Draw,
                [
                    new("annotate.ink", Strings.Cmd_Ink, SymbolRegular.InkStroke24),
                    new("annotate.shapes", Strings.Cmd_Shapes, SymbolRegular.DrawShape24),
                    new("annotate.stamp", Strings.Cmd_StampAnnotation, SymbolRegular.DocumentCheckmark24),
                ]),
                new(Strings.Ribbon_Group_Search,
                [
                    new("search.find", Strings.Cmd_Find, SymbolRegular.DocumentSearch24),
                ]),
            ]),
            new("security", Strings.Ribbon_Tab_Security,
            [
                new(Strings.Ribbon_Group_Protect,
                [
                    new("security.encrypt", Strings.Cmd_Password, SymbolRegular.LockClosed24),
                    new("security.redact", Strings.Cmd_Redact, SymbolRegular.EraserSegment24),
                    new("security.sanitize", Strings.Cmd_Sanitize, SymbolRegular.Broom24),
                ]),
                new(Strings.Ribbon_Group_Signature,
                [
                    new("security.sign", Strings.Cmd_Sign, SymbolRegular.Certificate24),
                    new("security.verify", Strings.Cmd_VerifySignatures, SymbolRegular.ShieldCheckmark24),
                ]),
            ]),
            new("convert", Strings.Ribbon_Tab_Convert,
            [
                new(Strings.Ribbon_Group_Export,
                [
                    new("convert.toImages", Strings.Cmd_ToImages, SymbolRegular.Image24),
                    new("convert.toText", Strings.Cmd_ToText, SymbolRegular.DocumentText24),
                ]),
                new(Strings.Ribbon_Group_Import,
                [
                    new("convert.fromImages", Strings.Cmd_FromImages, SymbolRegular.ImageMultiple24),
                    new("convert.officeToPdf", Strings.Cmd_FromOffice, SymbolRegular.DocumentArrowDown24),
                ]),
                new(Strings.Ribbon_Group_Optimize,
                [
                    new("optimize.compress", Strings.Cmd_Compress, SymbolRegular.ArrowMinimize24),
                    new("convert.toPdfA", Strings.Cmd_PdfA, SymbolRegular.Archive24),
                ]),
            ]),
            new("tools", Strings.Ribbon_Tab_Tools,
            [
                new(Strings.Ribbon_Group_Tools,
                [
                    new("ocr.run", Strings.Cmd_Ocr, SymbolRegular.ScanText24),
                    new("compare.documents", Strings.Cmd_Compare, SymbolRegular.ArrowSwap24),
                ]),
                new(Strings.Ribbon_Group_Automation,
                [
                    new("workflow.open", Strings.Cmd_Workflow, SymbolRegular.Flow24),
                    new("batch.run", Strings.Cmd_Batch, SymbolRegular.TaskListLtr24),
                ]),
                new(Strings.Ribbon_Group_App,
                [
                    new("tools.settings", Strings.Cmd_Settings, SymbolRegular.Settings24, OpenSettingsCommand),
                ]),
                new(Strings.Ribbon_Group_Diagnostics, diagnostics),
            ]),
        ];
    }

    /// <summary>The print job running in the background, so closing its document can wait for it.</summary>
    private sealed record ActivePrint(DocumentTabViewModel Document, BackgroundTask Status, Task<PrintOutcome> Completion);
}
