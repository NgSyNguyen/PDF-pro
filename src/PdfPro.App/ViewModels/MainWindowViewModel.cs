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
using PdfPro.App.Services.Documents;
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
    private readonly IDocumentWorkspaceService _workspaces;
    private readonly IDocumentTabFactory _tabFactory;
    private readonly ISettingsService _settings;
    private readonly IFileDialogService _fileDialogs;
    private readonly IMessageService _messages;
    private readonly IPrintDialogService _printDialogs;
    private readonly IDocumentPrinter _printer;
    private readonly IDocumentSaver _saver;
    private readonly IRecoveryService _recovery;
    private readonly IContentChanger _contentChanger;
    private readonly IPageDialogService _pageDialogs;
    private readonly IMergeService _merge;
    private readonly IBatesService? _bates;
    private readonly Services.Security.SigningServices? _signing;
    private readonly ILogger<MainWindowViewModel> _logger;
    private ActivePrint? _activePrint;
    private DocumentTabViewModel? _trackedDocument;

    public MainWindowViewModel(
        IDialogService dialogs,
        IAppLifetime lifetime,
        IShellLauncher shell,
        AppPaths paths,
        AppBuildInfo buildInfo,
        IDocumentWorkspaceService workspaces,
        IDocumentTabFactory tabFactory,
        ISettingsService settings,
        IFileDialogService fileDialogs,
        IMessageService messages,
        IPrintDialogService printDialogs,
        IDocumentPrinter printer,
        IDocumentSaver saver,
        IRecoveryService recovery,
        IContentChanger contentChanger,
        IPageDialogService pageDialogs,
        IMergeService merge,
        ILogger<MainWindowViewModel> logger,
        IBatesService? bates = null,
        Services.Security.SigningServices? signing = null)
    {
        ArgumentNullException.ThrowIfNull(buildInfo);
        _dialogs = dialogs;
        _lifetime = lifetime;
        _shell = shell;
        _paths = paths;
        _workspaces = workspaces;
        _tabFactory = tabFactory;
        _settings = settings;
        _fileDialogs = fileDialogs;
        _messages = messages;
        _printDialogs = printDialogs;
        _printer = printer;
        _saver = saver;
        _recovery = recovery;
        _contentChanger = contentChanger;
        _pageDialogs = pageDialogs;
        _merge = merge;
        _bates = bates;
        _signing = signing;
        _logger = logger;

        UndoCommand = new ActiveDocumentCommand(d => d.UndoCommand);
        RedoCommand = new ActiveDocumentCommand(d => d.RedoCommand);
        RotateLeftCommand = new ActiveDocumentCommand(d => d.RotateLeftCommand);
        RotateRightCommand = new ActiveDocumentCommand(d => d.RotateRightCommand);
        DeletePagesCommand = new ActiveDocumentCommand(d => d.DeletePagesCommand);
        DuplicatePagesCommand = new ActiveDocumentCommand(d => d.DuplicatePagesCommand);
        OrganizeModeCommand = new ActiveDocumentCommand(d => d.ToggleOrganizeModeCommand);
        InsertBlankPagesCommand = new ActiveDocumentCommand(d => d.InsertBlankPagesCommand);
        InsertPagesFromFileCommand = new ActiveDocumentCommand(d => d.InsertPagesFromFileCommand);
        ExtractPagesCommand = new ActiveDocumentCommand(d => d.ExtractPagesCommand);
        CropPagesCommand = new ActiveDocumentCommand(d => d.CropPagesCommand);
        ChangePageSizeCommand = new ActiveDocumentCommand(d => d.ChangePageSizeCommand);
        MarkSplitCommand = new ActiveDocumentCommand(d => d.MarkSplitCommand);
        ClearSplitMarksCommand = new ActiveDocumentCommand(d => d.ClearSplitMarksCommand);
        SplitCommand = new ActiveDocumentCommand(d => d.SplitCommand);
        InterleaveCommand = new ActiveDocumentCommand(d => d.InterleaveCommand);
        NUpCommand = new ActiveDocumentCommand(d => d.NUpCommand);
        BookletCommand = new ActiveDocumentCommand(d => d.BookletCommand);
        TileCommand = new ActiveDocumentCommand(d => d.TileCommand);
        AddTextCommand = new ActiveDocumentCommand(d => d.AddTextCommand);
        ToggleContentEditingCommand = new ActiveDocumentCommand(d => d.ToggleContentEditingCommand);
        ToggleRedactionCommand = new ActiveDocumentCommand(d => d.ToggleRedactionCommand);
        SanitizeCommand = new ActiveDocumentCommand(d => d.SanitizeCommand);
        ToggleFormDesignCommand = new ActiveDocumentCommand(d => d.ToggleFormDesignCommand);
        ExportFormDataCommand = new ActiveDocumentCommand(d => d.ExportFormDataCommand);
        ImportFormDataCommand = new ActiveDocumentCommand(d => d.ImportFormDataCommand);
        MergeFormDataCommand = new ActiveDocumentCommand(d => d.MergeFormDataCommand);
        FlattenFormCommand = new ActiveDocumentCommand(d => d.FlattenFormCommand);
        ResetFormCommand = new ActiveDocumentCommand(d => d.ResetFormCommand);
        AddShapeCommand = new ActiveDocumentCommand(d => d.AddShapeCommand);
        AddImageCommand = new ActiveDocumentCommand(d => d.AddImageCommand);
        AddSignatureCommand = new ActiveDocumentCommand(d => d.AddSignatureCommand);
        StampWatermarkCommand = new ActiveDocumentCommand(d => d.StampWatermarkCommand);
        StampBackgroundCommand = new ActiveDocumentCommand(d => d.StampBackgroundCommand);
        StampHeaderFooterCommand = new ActiveDocumentCommand(d => d.StampHeaderFooterCommand);
        StampPageNumbersCommand = new ActiveDocumentCommand(d => d.StampPageNumbersCommand);
        RemoveStampsCommand = new ActiveDocumentCommand(d => d.RemoveStampsCommand);
        HighlightCommand = new ActiveDocumentCommand(d => d.HighlightCommand);
        UnderlineCommand = new ActiveDocumentCommand(d => d.UnderlineCommand);
        StrikeOutCommand = new ActiveDocumentCommand(d => d.StrikeOutCommand);
        SquigglyCommand = new ActiveDocumentCommand(d => d.SquigglyCommand);
        StickyNoteCommand = new ActiveDocumentCommand(d => d.StickyNoteCommand);
        TextBoxCommand = new ActiveDocumentCommand(d => d.TextBoxCommand);
        InkCommand = new ActiveDocumentCommand(d => d.InkCommand);
        ShapesCommand = new ActiveDocumentCommand(d => d.ShapesCommand);
        StampAnnotationCommand = new ActiveDocumentCommand(d => d.StampAnnotationCommand);
        FlattenAnnotationsCommand = new ActiveDocumentCommand(d => d.FlattenAnnotationsCommand);
        FindNextCommand = new ActiveDocumentCommand(d => d.Search.NextCommand);
        FindPreviousCommand = new ActiveDocumentCommand(d => d.Search.PreviousCommand);
        Home = new HomeTabViewModel(OpenCommand, OpenRecentFileCommand, MergeCommand);
        ShowRecentFiles(settings.Current.RecentFiles);
        RibbonTabs = BuildRibbon(buildInfo.IsDebugBuild);
        Tabs = [Home];
        SelectedTab = Home;
        StatusBar.PropertyChanged += OnStatusBarPropertyChanged;
        Sidebar.PropertyChanged += OnSidebarPropertyChanged;
    }

    public IReadOnlyList<RibbonTabViewModel> RibbonTabs { get; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; }

    public HomeTabViewModel Home { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDocument), nameof(HasActiveDocument), nameof(IsOrganizing), nameof(ShowsViewerStatus))]
    [NotifyCanExecuteChangedFor(nameof(CloseActiveDocumentCommand), nameof(ShowPropertiesCommand), nameof(PrintCommand), nameof(SaveCommand), nameof(SaveAsCommand), nameof(ShowSearchCommand), nameof(ShowCommentsCommand), nameof(ShowSignaturesCommand), nameof(ProtectCommand), nameof(SignCommand))]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    /// <summary>The selected document tab, or null when the home tab is selected.</summary>
    public DocumentTabViewModel? ActiveDocument => SelectedTab as DocumentTabViewModel;

    public bool HasActiveDocument => ActiveDocument is not null;

    public IEnumerable<DocumentTabViewModel> Documents => Tabs.OfType<DocumentTabViewModel>();

    public SidebarViewModel Sidebar { get; } = new();

    /// <summary>Ctrl+Z and the ribbon's "Hoàn tác": undoes the last change of the active document.</summary>
    public ActiveDocumentCommand UndoCommand { get; }

    /// <summary>Ctrl+Y / Ctrl+Shift+Z.</summary>
    public ActiveDocumentCommand RedoCommand { get; }

    /// <summary>Ctrl+Shift+R: turns the current page left.</summary>
    public ActiveDocumentCommand RotateLeftCommand { get; }

    /// <summary>Ctrl+R: turns the current page right.</summary>
    public ActiveDocumentCommand RotateRightCommand { get; }

    public ActiveDocumentCommand DeletePagesCommand { get; }

    public ActiveDocumentCommand DuplicatePagesCommand { get; }

    /// <summary>"Sắp xếp trang": switches the active document between the viewer and the organizer grid.</summary>
    public ActiveDocumentCommand OrganizeModeCommand { get; }

    public ActiveDocumentCommand InsertBlankPagesCommand { get; }

    public ActiveDocumentCommand InsertPagesFromFileCommand { get; }

    public ActiveDocumentCommand ExtractPagesCommand { get; }

    public ActiveDocumentCommand CropPagesCommand { get; }

    public ActiveDocumentCommand ChangePageSizeCommand { get; }

    /// <summary>Ctrl+M, "Trang đầu tệp": marks (or unmarks) the target pages as first pages of split files.</summary>
    public ActiveDocumentCommand MarkSplitCommand { get; }

    public ActiveDocumentCommand ClearSplitMarksCommand { get; }

    /// <summary>"Tách": the split dialog of the active document.</summary>
    public ActiveDocumentCommand SplitCommand { get; }

    public ActiveDocumentCommand InterleaveCommand { get; }

    public ActiveDocumentCommand NUpCommand { get; }

    public ActiveDocumentCommand BookletCommand { get; }

    public ActiveDocumentCommand TileCommand { get; }

    public ActiveDocumentCommand AddTextCommand { get; }

    public ActiveDocumentCommand ToggleContentEditingCommand { get; }

    /// <summary>"Xóa thông tin": the redaction mode of the active document.</summary>
    public ActiveDocumentCommand ToggleRedactionCommand { get; }

    public ActiveDocumentCommand SanitizeCommand { get; }

    public ActiveDocumentCommand ToggleFormDesignCommand { get; }

    public ActiveDocumentCommand ExportFormDataCommand { get; }

    public ActiveDocumentCommand ImportFormDataCommand { get; }

    public ActiveDocumentCommand MergeFormDataCommand { get; }

    public ActiveDocumentCommand FlattenFormCommand { get; }

    public ActiveDocumentCommand ResetFormCommand { get; }

    public ActiveDocumentCommand AddShapeCommand { get; }

    public ActiveDocumentCommand AddImageCommand { get; }

    public ActiveDocumentCommand AddSignatureCommand { get; }

    public ActiveDocumentCommand StampWatermarkCommand { get; }

    public ActiveDocumentCommand StampBackgroundCommand { get; }

    public ActiveDocumentCommand StampHeaderFooterCommand { get; }

    public ActiveDocumentCommand StampPageNumbersCommand { get; }

    public ActiveDocumentCommand RemoveStampsCommand { get; }

    public ActiveDocumentCommand HighlightCommand { get; }

    public ActiveDocumentCommand UnderlineCommand { get; }

    public ActiveDocumentCommand StrikeOutCommand { get; }

    public ActiveDocumentCommand SquigglyCommand { get; }

    public ActiveDocumentCommand StickyNoteCommand { get; }

    public ActiveDocumentCommand TextBoxCommand { get; }

    public ActiveDocumentCommand InkCommand { get; }

    public ActiveDocumentCommand ShapesCommand { get; }

    public ActiveDocumentCommand StampAnnotationCommand { get; }

    public ActiveDocumentCommand FlattenAnnotationsCommand { get; }

    /// <summary>F3: the next match of the search.</summary>
    public ActiveDocumentCommand FindNextCommand { get; }

    /// <summary>Shift+F3: the previous match of the search.</summary>
    public ActiveDocumentCommand FindPreviousCommand { get; }

    /// <summary>The search panel was asked for (Ctrl+F, "Tìm"): the window puts the keyboard in its query box.</summary>
    public event EventHandler? SearchRequested;

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

            var workspace = await _workspaces.OpenAsync(fullPath);
            if (workspace is null)
            {
                if (!File.Exists(fullPath))
                {
                    await UpdateRecentFilesAsync(list => RecentFileList.Remove(list, fullPath));
                }

                continue;
            }

            var tab = _tabFactory.Create(workspace);
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

        // Objects placed in the page editor and not applied yet are applied first (then asked about like any change).
        if (!await ApplyPendingEditsAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        if (!await ConfirmDropRedactionMarksAsync(document) || !Tabs.Contains(document))
        {
            return;
        }

        // Unsaved changes: Lưu / Không lưu / Hủy. A save that fails keeps the tab open.
        if (document.IsDirty && !document.DiscardChanges)
        {
            var choice = await AskToSaveAsync(document, exiting: false);
            if (choice == UnsavedChoice.Cancel || !Tabs.Contains(document))
            {
                return;
            }

            document.DiscardChanges = choice == UnsavedChoice.Discard;
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

        // A save or extraction may still read its files.
        if (_activeSave is { } save)
        {
            await Task.WhenAny(save);
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

        // What is printed is what is shown: objects waiting in the page editor are applied first.
        if (!await ApplyPendingEditsAsync(document))
        {
            return;
        }

        if (!document.Document.Features.CanPrint)
        {
            await _messages.ShowInfoAsync(Strings.Print_Error_Title, Strings.Print_NotAllowed);
            return;
        }

        var pages = document.Workspace.Pages;
        var job = _printDialogs.ShowPrintDialog(pages.PageCount, document.Viewer.CurrentPageIndex);
        if (job is null || job.PageIndices.Count == 0)
        {
            return;
        }

        var total = job.PageIndices.Count;
        var task = StatusBar.StartTask(PrintProgressText(0, total));
        var progress = new Progress<int>(done => task.Report((double)done / total, PrintProgressText(done, total)));
        var printing = _printer.PrintAsync(pages, job, document.FileName, progress, task.Token);
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

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value)
    {
        if (_trackedDocument is not null)
        {
            _trackedDocument.Comments.IsActive = false;
            _trackedDocument.Signatures.IsActive = false;
            _trackedDocument.PropertyChanged -= OnActiveDocumentPropertyChanged;
            _trackedDocument.ExtractRequested -= OnExtractRequested;
            _trackedDocument.ContentChangeRequested -= OnContentChangeRequested;
            _trackedDocument.SplitRequested -= OnSplitRequested;
            _trackedDocument.LayoutRequested -= OnLayoutRequested;
            _trackedDocument.FormMergeRequested -= OnFormMergeRequested;
        }

        _trackedDocument = value as DocumentTabViewModel;
        if (_trackedDocument is not null)
        {
            _trackedDocument.PropertyChanged += OnActiveDocumentPropertyChanged;
            _trackedDocument.ExtractRequested += OnExtractRequested;
            _trackedDocument.ContentChangeRequested += OnContentChangeRequested;
            _trackedDocument.SplitRequested += OnSplitRequested;
            _trackedDocument.LayoutRequested += OnLayoutRequested;
            _trackedDocument.FormMergeRequested += OnFormMergeRequested;
        }

        foreach (var command in new[]
        {
            UndoCommand, RedoCommand, RotateLeftCommand, RotateRightCommand, DeletePagesCommand, DuplicatePagesCommand,
            OrganizeModeCommand, InsertBlankPagesCommand, InsertPagesFromFileCommand, ExtractPagesCommand, CropPagesCommand, ChangePageSizeCommand,
            MarkSplitCommand, ClearSplitMarksCommand, SplitCommand, InterleaveCommand, NUpCommand, BookletCommand, TileCommand,
            AddTextCommand, AddShapeCommand, AddImageCommand, AddSignatureCommand, ToggleContentEditingCommand, ToggleFormDesignCommand, ToggleRedactionCommand, SanitizeCommand,
            ExportFormDataCommand, ImportFormDataCommand, MergeFormDataCommand, FlattenFormCommand, ResetFormCommand,
            StampWatermarkCommand, StampBackgroundCommand, StampHeaderFooterCommand, StampPageNumbersCommand, RemoveStampsCommand,
            FindNextCommand, FindPreviousCommand,
            HighlightCommand, UnderlineCommand, StrikeOutCommand, SquigglyCommand, StickyNoteCommand,
            TextBoxCommand, InkCommand, ShapesCommand, StampAnnotationCommand, FlattenAnnotationsCommand,
        })
        {
            command.Attach(_trackedDocument);
        }

        UpdateHistoryToolTips();
        UpdateCommentsPanel();
    }

    /// <summary>
    /// The comments of the active document are read while the "Bình luận" panel shows them, its signatures checked
    /// while the "Chữ ký" panel does.
    /// </summary>
    private void UpdateCommentsPanel()
    {
        if (_trackedDocument is { } document)
        {
            document.Comments.IsActive = Sidebar.SelectedPanel == SidebarPanel.Comments;
            document.Signatures.IsActive = Sidebar.SelectedPanel == SidebarPanel.Signatures;
        }
    }

    private void OnSidebarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.SelectedPanel))
        {
            UpdateCommentsPanel();
        }
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentTabViewModel.UndoToolTip) or nameof(DocumentTabViewModel.RedoToolTip))
        {
            UpdateHistoryToolTips();
        }

        if (e.PropertyName is nameof(DocumentTabViewModel.IsDirty) or nameof(DocumentTabViewModel.HasPendingEdits))
        {
            NotifySaveCommands();
        }

        if (e.PropertyName == nameof(DocumentTabViewModel.IsOrganizing))
        {
            OnPropertyChanged(nameof(IsOrganizing));
            OnPropertyChanged(nameof(ShowsViewerStatus));
        }
    }

    private void UpdateHistoryToolTips()
    {
        var commands = RibbonTabs.SelectMany(t => t.Groups).SelectMany(g => g.Commands).ToList();
        commands.FirstOrDefault(c => c.Id == "edit.undo")?.SetToolTip(_trackedDocument?.UndoToolTip ?? Strings.Cmd_Undo);
        commands.FirstOrDefault(c => c.Id == "edit.redo")?.SetToolTip(_trackedDocument?.RedoToolTip ?? Strings.Cmd_Redo);
    }

    private void OnStatusBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusBarViewModel.IsTaskRunning))
        {
            PrintCommand.NotifyCanExecuteChanged();
            NotifySaveCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private Task CloseActiveDocumentAsync() => CloseTabAsync(ActiveDocument);

    /// <summary>"Tìm" (Ctrl+F): shows the search panel of the sidebar (the organizer gives way to the viewer).</summary>
    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ShowSearch()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsOrganizing)
        {
            document.IsOrganizing = false;
        }

        Sidebar.SelectedPanel = SidebarPanel.Search;
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Bình luận": shows the comments panel of the sidebar (the organizer gives way to the viewer).</summary>
    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ShowComments()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsOrganizing)
        {
            document.IsOrganizing = false;
        }

        Sidebar.SelectedPanel = SidebarPanel.Comments;
    }

    /// <summary>"Kiểm chữ ký": shows the signatures panel of the sidebar (the organizer gives way to the viewer).</summary>
    [RelayCommand(CanExecute = nameof(HasActiveDocument))]
    private void ShowSignatures()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        if (document.IsOrganizing)
        {
            document.IsOrganizing = false;
        }

        Sidebar.SelectedPanel = SidebarPanel.Signatures;
    }

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

    /// <summary>"Thoát" and the window's X: asks about unsaved documents, then closes the application.</summary>
    [RelayCommand]
    private Task ExitAsync() => _lifetime.ShutdownAsync();

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
                    new("file.save", Strings.Cmd_Save, SymbolRegular.Save24, SaveCommand),
                    new("file.saveAs", Strings.Cmd_SaveAs, SymbolRegular.SaveEdit24, SaveAsCommand),
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
                    new("organize.mode", Strings.Cmd_OrganizePages, SymbolRegular.GridDots24, OrganizeModeCommand),
                    new("organize.rotateLeft", Strings.Cmd_RotateLeft, SymbolRegular.ArrowRotateCounterclockwise24, RotateLeftCommand),
                    new("organize.rotateRight", Strings.Cmd_RotateRight, SymbolRegular.ArrowRotateClockwise24, RotateRightCommand),
                    new("organize.delete", Strings.Cmd_DeletePages, SymbolRegular.Delete24, DeletePagesCommand),
                    new("organize.duplicate", Strings.Cmd_DuplicatePages, SymbolRegular.DocumentCopy24, DuplicatePagesCommand),
                ]),
                new(Strings.Ribbon_Group_Insert,
                [
                    new("organize.insertBlank", Strings.Cmd_InsertBlank, SymbolRegular.DocumentAdd24, InsertBlankPagesCommand),
                    new("organize.insertFromFile", Strings.Cmd_InsertFromFile, SymbolRegular.DocumentArrowLeft24, InsertPagesFromFileCommand),
                    new("organize.extract", Strings.Cmd_ExtractPages, SymbolRegular.DocumentArrowRight24, ExtractPagesCommand),
                ]),
                new(Strings.Ribbon_Group_Layout,
                [
                    new("organize.crop", Strings.Cmd_Crop, SymbolRegular.Crop24, CropPagesCommand),
                    new("organize.pageSize", Strings.Cmd_PageSize, SymbolRegular.DocumentLandscape24, ChangePageSizeCommand),
                    new("layout.nup", Strings.Cmd_NUp, SymbolRegular.Grid24, NUpCommand, Strings.Cmd_NUp_ToolTip),
                    new("layout.booklet", Strings.Cmd_Booklet, SymbolRegular.Book24, BookletCommand, Strings.Cmd_Booklet_ToolTip),
                    new("layout.tile", Strings.Cmd_Tile, SymbolRegular.TableSimple24, TileCommand, Strings.Cmd_Tile_ToolTip),
                ]),
                new(Strings.Ribbon_Group_MergeSplit,
                [
                    new("organize.merge", Strings.Cmd_Merge, SymbolRegular.Merge24, MergeCommand),
                    new("organize.split", Strings.Cmd_Split, SymbolRegular.SplitVertical24, SplitCommand),
                    new("organize.markSplit", Strings.Cmd_MarkSplit, SymbolRegular.DocumentSplitHint24, MarkSplitCommand, Strings.Cmd_MarkSplit_ToolTip),
                    new("organize.clearSplitMarks", Strings.Cmd_ClearSplitMarks, SymbolRegular.DocumentDismiss24, ClearSplitMarksCommand, Strings.Cmd_ClearSplitMarks_ToolTip),
                    new("organize.interleave", Strings.Cmd_Interleave, SymbolRegular.ArrowSwap24, InterleaveCommand, Strings.Cmd_Interleave_ToolTip),
                ]),
            ]),
            new("edit", Strings.Ribbon_Tab_Edit,
            [
                new(Strings.Ribbon_Group_History,
                [
                    new("edit.undo", Strings.Cmd_Undo, SymbolRegular.ArrowUndo24, UndoCommand),
                    new("edit.redo", Strings.Cmd_Redo, SymbolRegular.ArrowRedo24, RedoCommand),
                ]),
                new(Strings.Ribbon_Group_Add,
                [
                    new("edit.addText", Strings.Cmd_AddText, SymbolRegular.TextAddT24, AddTextCommand),
                    new("edit.addImage", Strings.Cmd_AddImage, SymbolRegular.ImageAdd24, AddImageCommand),
                    new("edit.addShape", Strings.Cmd_AddShape, SymbolRegular.Shapes24, AddShapeCommand),
                    new("edit.handSignature", Strings.Cmd_HandSignature, SymbolRegular.Signature24, AddSignatureCommand),
                ]),
                new(Strings.Ribbon_Group_Stamp,
                [
                    new("stamp.watermark", Strings.Cmd_Watermark, SymbolRegular.Water24, StampWatermarkCommand),
                    new("stamp.background", Strings.Cmd_Background, SymbolRegular.PaintBucket24, StampBackgroundCommand),
                    new("stamp.headerFooter", Strings.Cmd_HeaderFooter, SymbolRegular.DocumentHeader24, StampHeaderFooterCommand),
                    new("stamp.pageNumbers", Strings.Cmd_PageNumbers, SymbolRegular.NumberSymbol24, StampPageNumbersCommand),
                    new("stamp.remove", Strings.Cmd_RemoveStamps, SymbolRegular.EraserTool24, RemoveStampsCommand, Strings.Cmd_RemoveStamps_ToolTip),
                    new("stamp.bates", Strings.Cmd_Bates, SymbolRegular.NumberRow24, BatesCommand, Strings.Cmd_Bates_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Content,
                [
                    new("edit.content", Strings.Cmd_EditContent, SymbolRegular.DocumentEdit24, ToggleContentEditingCommand, Strings.Cmd_EditContent_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Forms,
                [
                    new("forms.design", Strings.Cmd_DesignForm, SymbolRegular.Form24, ToggleFormDesignCommand, Strings.Cmd_DesignForm_ToolTip),
                    new("forms.exportData", Strings.Cmd_ExportFormData, SymbolRegular.ArrowExport24, ExportFormDataCommand, Strings.Cmd_ExportFormData_ToolTip),
                    new("forms.importData", Strings.Cmd_ImportFormData, SymbolRegular.ArrowImport24, ImportFormDataCommand, Strings.Cmd_ImportFormData_ToolTip),
                    new("forms.merge", Strings.Cmd_MergeFormData, SymbolRegular.DocumentTableArrowRight24, MergeFormDataCommand, Strings.Cmd_MergeFormData_ToolTip),
                    new("forms.reset", Strings.Cmd_ResetForm, SymbolRegular.Eraser24, ResetFormCommand, Strings.Cmd_ResetForm_ToolTip),
                    new("forms.flatten", Strings.Cmd_FlattenForm, SymbolRegular.Layer24, FlattenFormCommand, Strings.Cmd_FlattenForm_ToolTip),
                ]),
            ]),
            new("annotate", Strings.Ribbon_Tab_Annotate,
            [
                new(Strings.Ribbon_Group_Markup,
                [
                    new("annotate.highlight", Strings.Cmd_Highlight, SymbolRegular.Highlight24, HighlightCommand, Strings.Cmd_Highlight_ToolTip),
                    new("annotate.underline", Strings.Cmd_Underline, SymbolRegular.TextUnderline24, UnderlineCommand, Strings.Cmd_Underline_ToolTip),
                    new("annotate.strikeout", Strings.Cmd_Strikeout, SymbolRegular.TextStrikethrough24, StrikeOutCommand, Strings.Cmd_Strikeout_ToolTip),
                    new("annotate.squiggly", Strings.Cmd_Squiggly, SymbolRegular.TextWordCount24, SquigglyCommand, Strings.Cmd_Squiggly_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Notes,
                [
                    new("annotate.note", Strings.Cmd_StickyNote, SymbolRegular.Note24, StickyNoteCommand, Strings.Cmd_StickyNote_ToolTip),
                    new("annotate.textBox", Strings.Cmd_TextBox, SymbolRegular.Textbox24, TextBoxCommand, Strings.Cmd_TextBox_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Draw,
                [
                    new("annotate.ink", Strings.Cmd_Ink, SymbolRegular.InkStroke24, InkCommand, Strings.Cmd_Ink_ToolTip),
                    new("annotate.shapes", Strings.Cmd_Shapes, SymbolRegular.DrawShape24, ShapesCommand, Strings.Cmd_Shapes_ToolTip),
                    new("annotate.stamp", Strings.Cmd_StampAnnotation, SymbolRegular.DocumentCheckmark24, StampAnnotationCommand, Strings.Cmd_StampAnnotation_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Review,
                [
                    new("annotate.comments", Strings.Cmd_Comments, SymbolRegular.CommentMultiple24, ShowCommentsCommand, Strings.Cmd_Comments_ToolTip),
                    new("annotate.flatten", Strings.Cmd_FlattenAnnotations, SymbolRegular.Layer24, FlattenAnnotationsCommand, Strings.Cmd_FlattenAnnotations_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Search,
                [
                    new("search.find", Strings.Cmd_Find, SymbolRegular.DocumentSearch24, ShowSearchCommand, Strings.Cmd_Find_ToolTip),
                ]),
            ]),
            new("security", Strings.Ribbon_Tab_Security,
            [
                new(Strings.Ribbon_Group_Protect,
                [
                    new("security.encrypt", Strings.Cmd_Password, SymbolRegular.LockClosed24, ProtectCommand, Strings.Cmd_Password_ToolTip),
                    new("security.redact", Strings.Cmd_Redact, SymbolRegular.EraserSegment24, ToggleRedactionCommand, Strings.Cmd_Redact_ToolTip),
                    new("security.sanitize", Strings.Cmd_Sanitize, SymbolRegular.Broom24, SanitizeCommand, Strings.Cmd_Sanitize_ToolTip),
                ]),
                new(Strings.Ribbon_Group_Signature,
                [
                    new("security.sign", Strings.Cmd_Sign, SymbolRegular.Certificate24, SignCommand, Strings.Cmd_Sign_ToolTip),
                    new("security.signBatch", Strings.Cmd_SignBatch, SymbolRegular.DocumentMultiple24, SignBatchCommand, Strings.Cmd_SignBatch_ToolTip),
                    new("security.verify", Strings.Cmd_VerifySignatures, SymbolRegular.ShieldCheckmark24, ShowSignaturesCommand, Strings.Cmd_VerifySignatures_ToolTip),
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
