using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.App.ViewModels.Splitting;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Splitting;
using PdfPro.Pdfium;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>An entry of the history menu: undoing or redoing up to this step.</summary>
/// <param name="Description">The step's name ("Xoay 3 trang").</param>
/// <param name="IsRedo">True for steps that can be redone, false for steps that can be undone.</param>
/// <param name="Steps">How many steps to undo or redo to reach this entry.</param>
public sealed record HistoryEntryViewModel(string Description, bool IsRedo, int Steps);

/// <summary>Services a document tab uses for its page commands.</summary>
/// <param name="Operations">The page operations.</param>
/// <param name="Messages">Explains refused edits and failed inserts.</param>
/// <param name="PageDialogs">The insert dialogs.</param>
/// <param name="FileDialogs">Picks the file to insert.</param>
/// <param name="Loader">Opens inserted files.</param>
/// <param name="CreateOrganizer">Builds the organizer grid the first time it is shown.</param>
/// <param name="LoadPreview">Loads a page image for the crop dialog's preview (upright, as displayed), or null.</param>
public sealed record DocumentTabServices(
    OperationCatalog Operations,
    IMessageService Messages,
    IPageDialogService PageDialogs,
    IFileDialogService FileDialogs,
    IPdfDocumentLoader Loader,
    Func<DocumentWorkspace, IPageOrganizerHost, PageOrganizerViewModel> CreateOrganizer,
    Func<VirtualPage, CancellationToken, Task<ImageSource?>> LoadPreview);

/// <summary>A content change the user asked for, run by the window in the background.</summary>
/// <param name="Operation">The content operation.</param>
/// <param name="Parameters">Its parameters.</param>
/// <param name="Description">The step's name ("Đổi khổ giấy 3 trang"), for the status bar.</param>
public sealed record ContentChangeRequest(IContentOperation Operation, object Parameters, string Description);

/// <summary>A split confirmed in the dialog, written by the window in the background.</summary>
/// <param name="Operation">Writes the files.</param>
/// <param name="Document">The document as it was split (plan and sources).</param>
/// <param name="Choice">The files, names, folder and options.</param>
public sealed record SplitRequest(SplitOperation Operation, SplitDocument Document, SplitDialogResult Choice);

/// <summary>
/// An open document tab: its workspace (session, sources), viewer, sidebar panels and page organizer, undo/redo and the
/// page commands. In the viewer the page commands act on the current page; in the organizer, on the selected pages.
/// Closing the tab closes the document and removes its session folder. Must be used on the UI thread.
/// </summary>
public sealed partial class DocumentTabViewModel : WorkspaceTabViewModel, IAsyncDisposable, IPageOrganizerHost
{
    private readonly RenderCache _cache;
    private readonly DocumentTabServices _services;
    private readonly OperationCatalog _operations;
    private readonly IMessageService _messages;
    private readonly ILogger _logger;
    private bool _inserting;
    private int _disposed;

    public DocumentTabViewModel(
        DocumentWorkspace workspace,
        DocumentViewerViewModel viewer,
        ThumbnailListViewModel thumbnails,
        BookmarkTreeViewModel bookmarks,
        RenderCache cache,
        DocumentTabServices services,
        ILogger<DocumentTabViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(services);
        Workspace = workspace;
        Viewer = viewer;
        Thumbnails = thumbnails;
        Bookmarks = bookmarks;
        _cache = cache;
        _services = services;
        _operations = services.Operations;
        _messages = services.Messages;
        _logger = logger;
        HistoryEntries = BuildHistory();
        workspace.PagesChanged += OnPagesChanged;
        workspace.StatusChanged += OnStatusChanged;
        Viewer.PropertyChanged += OnViewerPropertyChanged;
    }

    public DocumentWorkspace Workspace { get; }

    /// <summary>The document whose structure the tab shows (metadata, security, bookmarks): the base source.</summary>
    public IPdfDocument Document => Workspace.BaseDocument;

    /// <summary>Where the document is saved.</summary>
    public string FilePath => Workspace.FilePath ?? string.Empty;

    public string FileName => Workspace.FileName;

    public DocumentViewerViewModel Viewer { get; }

    public ThumbnailListViewModel Thumbnails { get; }

    public BookmarkTreeViewModel Bookmarks { get; }

    /// <summary>The page organizer grid, created the first time the organizer is shown.</summary>
    [ObservableProperty]
    public partial PageOrganizerViewModel? Organizer { get; private set; }

    /// <summary>The organizer grid replaces the viewer (and the sidebar) while true.</summary>
    [ObservableProperty]
    public partial bool IsOrganizing { get; set; }

    /// <summary>
    /// The pages the page commands act on: the selection in the organizer, the current page in the viewer (0-based, in
    /// document order).
    /// </summary>
    public IReadOnlyList<int> TargetPageIndices
        => IsOrganizing && Organizer is { } organizer
            ? organizer.SelectedIndices
            : Viewer.PageCount > 0 ? [Math.Clamp(Viewer.CurrentPageIndex, 0, Viewer.PageCount - 1)] : [];

    public bool HasTargetPages => TargetPageIndices.Count > 0;

    /// <summary>The document can be edited now (not read-only, not saving).</summary>
    public bool CanEditPages => Workspace.CanEdit && Viewer.PageCount > 0;

    /// <summary>The file name, with " *" while there are unsaved changes.</summary>
    public override string Title => IsDirty ? FileName + " *" : FileName;

    public override string ToolTip => FilePath;

    public override SymbolRegular Icon => SymbolRegular.DocumentPdf24;

    public override bool CanClose => true;

    public bool IsDirty => Workspace.IsDirty;

    /// <summary>Password protected documents can be viewed and printed but not edited or saved yet.</summary>
    public bool IsReadOnly => Workspace.IsReadOnly;

    /// <summary>
    /// The user chose "Không lưu": closing removes the session. Otherwise a tab closed with unsaved changes (Windows
    /// shutting down) keeps its session for recovery.
    /// </summary>
    public bool DiscardChanges { get; set; }

    /// <summary>"Hoàn tác: Xoay 1 trang", or just "Hoàn tác" when there is nothing to undo.</summary>
    public string UndoToolTip => Workspace.Session.History.NextUndoDescription is { } step
        ? string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, step)
        : Strings.Cmd_Undo;

    public string RedoToolTip => Workspace.Session.History.NextRedoDescription is { } step
        ? string.Format(CultureInfo.CurrentCulture, Strings.Cmd_RedoWith, step)
        : Strings.Cmd_Redo;

    /// <summary>The history menu: redoable steps (furthest first), then undoable steps (most recent first).</summary>
    public IReadOnlyList<HistoryEntryViewModel> HistoryEntries { get; private set; }

    /// <summary>"12 dấu → 13 tệp" while the document has first-page marks for a split; null otherwise.</summary>
    public string? SplitSummary
    {
        get
        {
            var plan = Workspace.Session.Plan;
            if (!plan.HasSplitMarks)
            {
                return null;
            }

            var starts = plan.GetSplitStarts();
            return string.Format(CultureInfo.CurrentCulture, Strings.Split_Summary, starts.Count - 1, starts.Count);
        }
    }

    /// <summary>"Tệp 3/13": the file of the viewer's current page while the document has first-page marks; null otherwise.</summary>
    public string? CurrentSplitFile
    {
        get
        {
            var plan = Workspace.Session.Plan;
            if (!plan.HasSplitMarks || Viewer.PageCount == 0)
            {
                return null;
            }

            var starts = plan.GetSplitStarts();
            var current = Math.Clamp(Viewer.CurrentPageIndex, 0, plan.Count - 1);
            return string.Format(CultureInfo.CurrentCulture, Strings.Split_CurrentFile, starts.Count(s => s <= current), starts.Count);
        }
    }

    /// <summary>"Trích trang…" was chosen: the pages to write to a new file (0-based, in document order).</summary>
    public event EventHandler<IReadOnlyList<int>>? ExtractRequested;

    /// <summary>A content change ("Đổi khổ giấy") was confirmed: the window runs it with progress and cancel.</summary>
    public event EventHandler<ContentChangeRequest>? ContentChangeRequested;

    /// <summary>A split was confirmed: the window writes the files with progress and cancel.</summary>
    public event EventHandler<SplitRequest>? SplitRequested;

    public bool IsSameFile(string path)
        => FilePath.Length > 0 && string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies a page operation as one undoable step; invalid parameters are explained to the user.</summary>
    public bool Apply(IPlanOperation operation, object parameters)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Workspace.CanEdit)
        {
            return false;
        }

        try
        {
            return Workspace.Apply(operation, parameters);
        }
        catch (OperationValidationException ex)
        {
            _logger.LogWarning("A page operation was refused: {OperationId}, {ErrorCount} error(s)", ex.OperationId, ex.Errors.Count);
            _ = _messages.ShowErrorAsync(Strings.Edit_Error_Title, Strings.Edit_Error_Invalid);
            return false;
        }
    }

    /// <summary>Double-click or Enter on a tile: back to the viewer, at that page.</summary>
    public void OpenPage(int index)
    {
        IsOrganizing = false;
        if (index >= 0 && index < Viewer.PageCount)
        {
            Viewer.NavigateTo(index);
        }
    }

    /// <summary>Dragging tiles: one undoable "Di chuyển" step.</summary>
    public bool MovePages(IReadOnlyList<int> indices, int insertAt)
    {
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count == 0 || !CanEditPages)
        {
            return false;
        }

        var position = Math.Clamp(insertAt, 0, Viewer.PageCount) + 1;
        return Apply(_operations.Get<MovePagesOperation>(), new MovePagesParams(PageRanges.Format(indices), position));
    }

    /// <summary>PDF files dropped on the organizer: every page of each file, in the order dropped, at the drop point.</summary>
    public async Task InsertFilesAsync(IReadOnlyList<string> paths, int insertAt)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var position = insertAt;
        foreach (var path in paths)
        {
            if (!CanInsert())
            {
                return;
            }

            if (await AddSourceAsync(path) is { } source && InsertSource(source, sourcePages: null, position))
            {
                position += source.Document.PageCount;
            }
        }
    }

    /// <summary>Stops rendering and loading, drops cached images, closes the document and removes its session.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Workspace.PagesChanged -= OnPagesChanged;
        Workspace.StatusChanged -= OnStatusChanged;
        Viewer.PropertyChanged -= OnViewerPropertyChanged;
        if (Organizer is { } organizer)
        {
            organizer.SelectionChanged -= OnOrganizerSelectionChanged;
            organizer.Dispose();
        }

        Thumbnails.Dispose();
        Bookmarks.Dispose();
        Viewer.Dispose();
        foreach (var source in Workspace.Sources)
        {
            _cache.RemoveDocument(source.Document.Id);
        }

        // Application exit waits for this on the UI thread: do not resume there.
        await Workspace.CloseAsync(keepSession: IsDirty && !DiscardChanges).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Workspace.Undo();

    private bool CanUndo() => Workspace.CanEdit && Workspace.Session.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Workspace.Redo();

    private bool CanRedo() => Workspace.CanEdit && Workspace.Session.History.CanRedo;

    /// <summary>History menu: jumps back or forward to the chosen step.</summary>
    [RelayCommand]
    private void GoToHistoryEntry(HistoryEntryViewModel? entry)
    {
        if (entry is null || !Workspace.CanEdit)
        {
            return;
        }

        if (entry.IsRedo)
        {
            Workspace.Redo(entry.Steps);
        }
        else
        {
            Workspace.Undo(entry.Steps);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditTargets))]
    private void RotateLeft() => ApplyToTargets(pages => (_operations.Get<RotatePagesOperation>(), new RotatePagesParams(pages, -90)));

    [RelayCommand(CanExecute = nameof(CanEditTargets))]
    private void RotateRight() => ApplyToTargets(pages => (_operations.Get<RotatePagesOperation>(), new RotatePagesParams(pages, 90)));

    [RelayCommand(CanExecute = nameof(CanDeletePages))]
    private void DeletePages() => ApplyToTargets(pages => (_operations.Get<DeletePagesOperation>(), new DeletePagesParams(pages)));

    [RelayCommand(CanExecute = nameof(CanEditTargets))]
    private void DuplicatePages() => ApplyToTargets(pages => (_operations.Get<DuplicatePagesOperation>(), new DuplicatePagesParams(pages)));

    private bool CanEditTargets() => CanEditPages && HasTargetPages && !_inserting;

    /// <summary>A document keeps at least one page.</summary>
    private bool CanDeletePages() => CanEditTargets() && TargetPageIndices.Count < Viewer.PageCount;

    private void ApplyToTargets(Func<string, (IPlanOperation Operation, object Parameters)> build)
    {
        var targets = TargetPageIndices;
        if (targets.Count == 0)
        {
            return;
        }

        var (operation, parameters) = build(PageRanges.Format(targets));
        Apply(operation, parameters);
    }

    /// <summary>"Sắp xếp trang": switches between the viewer and the organizer grid.</summary>
    [RelayCommand]
    private void ToggleOrganizeMode() => IsOrganizing = !IsOrganizing;

    partial void OnIsOrganizingChanged(bool value)
    {
        if (value)
        {
            if (Organizer is null)
            {
                var organizer = _services.CreateOrganizer(Workspace, this);
                organizer.SelectionChanged += OnOrganizerSelectionChanged;
                Organizer = organizer;
            }

            // The organizer opens on the page the viewer shows.
            var current = Math.Clamp(Viewer.CurrentPageIndex, 0, Math.Max(0, Viewer.PageCount - 1));
            Organizer.Select([current], current);
        }
        else if (Organizer?.FocusIndex is { } focus && focus != Viewer.CurrentPageIndex)
        {
            Viewer.NavigateTo(focus);
        }

        OnTargetsChanged();
    }

    /// <summary>"Chèn trang trắng…": the dialog, then one undoable step.</summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private void InsertBlankPages()
    {
        var dialog = new InsertBlankPagesViewModel(Workspace.Pages.PageSizes, AnchorPageNumber());
        if (_services.PageDialogs.ShowInsertBlankPages(dialog) && dialog.Result is { } parameters && CanEditPages)
        {
            Apply(_operations.Get<InsertBlankPagesOperation>(), parameters);
        }
    }

    /// <summary>
    /// "Chèn từ tệp…": picks a PDF, copies it into the session (password protected files are refused), asks which of
    /// its pages go where, then inserts them as one undoable step.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsert))]
    private async Task InsertPagesFromFileAsync()
    {
        if (_services.FileDialogs.PickPdfFile(Strings.Insert_PickFile_Title) is not { } path)
        {
            return;
        }

        if (await AddSourceAsync(path) is not { } source)
        {
            return;
        }

        var dialog = new InsertPagesViewModel(Path.GetFileName(path), source.Document.PageCount, Viewer.PageCount, AnchorPageNumber());
        if (_services.PageDialogs.ShowInsertPages(dialog) && dialog.Result is { } choice)
        {
            InsertSource(source, choice.SourcePages, choice.InsertAt);
        }
    }

    private bool CanInsert() => CanEditPages && !_inserting;

    /// <summary>
    /// "Trích trang…": asks the window to write the target pages to a new file (it owns the file dialog and the status
    /// bar). The document does not change.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExtract))]
    private void ExtractPages() => ExtractRequested?.Invoke(this, TargetPageIndices);

    private bool CanExtract() => !IsReadOnly && HasTargetPages && !_inserting;

    /// <summary>"Cắt lề…": the dialog (with a preview), then one undoable step.</summary>
    [RelayCommand(CanExecute = nameof(CanEditTargetsOrAll))]
    private void CropPages()
    {
        var dialog = new CropPagesViewModel(Workspace.Pages.Pages, TargetPageIndices, _services.LoadPreview);
        if (_services.PageDialogs.ShowCropPages(dialog) && dialog.Result is { } parameters && CanEditPages)
        {
            Apply(_operations.Get<CropPagesOperation>(), parameters);
        }
    }

    /// <summary>"Đổi khổ giấy…": the dialog, then the window rewrites the pages in the background (a new revision).</summary>
    [RelayCommand(CanExecute = nameof(CanEditTargetsOrAll))]
    private void ChangePageSize()
    {
        var targets = TargetPageIndices;
        var current = Workspace.Pages.PageSizes[targets.Count > 0 ? targets[0] : 0];
        var dialog = new PageSizeViewModel(Viewer.PageCount, targets, current);
        if (_services.PageDialogs.ShowPageSize(dialog) && dialog.Result is { } parameters && CanEditPages)
        {
            var description = Workspace.Describe(new Core.Sessions.HistoryRecord(ChangePageSizeOperation.OperationId, parameters, []), Workspace.Session.Plan);
            ContentChangeRequested?.Invoke(this, new ContentChangeRequest(_operations.Get<ChangePageSizeOperation>(), parameters, description));
        }
    }

    /// <summary>
    /// "Trang đầu tệp" (Ctrl+M): marks the target pages as first pages of split files, or unmarks them when all of them
    /// are marked already. The first page always starts the first file, so it is left out. The step can be undone and
    /// leaves the document saved (marks are not written into the PDF).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMarkSplit))]
    private void MarkSplit()
    {
        var targets = TargetPageIndices.Where(i => i > 0).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var plan = Workspace.Session.Plan;
        var unmark = targets.All(i => plan[i].SplitMark is not null);
        Apply(_operations.Get<MarkSplitOperation>(), new MarkSplitParams(PageRanges.Format(targets), Marked: !unmark));
    }

    private bool CanMarkSplit() => CanEditTargets() && TargetPageIndices.Any(i => i > 0);

    /// <summary>"Bỏ mọi dấu": removes every first-page mark (one undoable step).</summary>
    [RelayCommand(CanExecute = nameof(CanClearSplitMarks))]
    private void ClearSplitMarks() => Apply(_operations.Get<SetSplitMarksOperation>(), new SetSplitMarksParams([]));

    private bool CanClearSplitMarks() => CanEditPages && !_inserting && Workspace.Session.Plan.HasSplitMarks;

    /// <summary>
    /// "Tách": the split dialog (mode, preview, names, folder) on the document as displayed. Marks typed or named in the
    /// dialog become the document's marks (one undoable step); "Chuyển thành dấu" shows them in the organizer; a
    /// confirmed split is written by the window in the background. The document itself does not change.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void Split()
    {
        var operation = _operations.Get<SplitOperation>();
        var document = CreateSplitDocument();
        using var dialog = new SplitViewModel(document, operation, DefaultSplitFolder(), FilePath.Length > 0 ? FilePath : null, LoadPagePreviewAsync, _services.FileDialogs);
        if (!_services.PageDialogs.ShowSplit(dialog) || dialog.Result is not { } choice)
        {
            return;
        }

        if (choice.Marks is { } marks && !HasMarks(marks) && CanEditPages)
        {
            Apply(_operations.Get<SetSplitMarksOperation>(), new SetSplitMarksParams(marks));
        }

        if (choice.Action == SplitDialogAction.ConvertToMarks)
        {
            IsOrganizing = true;
            return;
        }

        SplitRequested?.Invoke(this, new SplitRequest(operation, document, choice));
    }

    /// <summary>Password protected documents are view-only until Phase 8 (security).</summary>
    private bool CanSplit() => !IsReadOnly && Viewer.PageCount > 0 && !_inserting;

    /// <summary>The document as displayed, for splitting: its plan and every source.</summary>
    private SplitDocument CreateSplitDocument() => new()
    {
        Plan = Workspace.Session.Plan,
        BaseSourceId = Workspace.Session.BaseSourceId,
        Documents = Workspace.Sources.ToDictionary(s => s.Source.Id, s => s.Document, StringComparer.Ordinal),
        SourcePaths = Workspace.SourcePaths,
        Name = Path.GetFileNameWithoutExtension(FileName),
        WorkingDirectory = Workspace.WorkDirectory,
    };

    /// <summary>"…\Hồ sơ - tách" next to the document's file.</summary>
    private string DefaultSplitFolder()
    {
        var folder = Path.GetDirectoryName(FilePath) is { Length: > 0 } directory
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(folder, string.Format(CultureInfo.CurrentCulture, Strings.Split_Folder_Default, Path.GetFileNameWithoutExtension(FileName)));
    }

    /// <summary>True when the document already has exactly these marks.</summary>
    private bool HasMarks(IReadOnlyList<SplitMarkEntry> marks)
    {
        var plan = Workspace.Session.Plan;
        var current = Enumerable.Range(0, plan.Count)
            .Where(i => plan[i].SplitMark is not null)
            .Select(i => new SplitMarkEntry(i + 1, plan[i].SplitMark!.FileName));
        return current.SequenceEqual(marks);
    }

    private Task<ImageSource?> LoadPagePreviewAsync(int pageIndex, CancellationToken cancellationToken)
        => pageIndex >= 0 && pageIndex < Workspace.Pages.PageCount
            ? _services.LoadPreview(Workspace.Pages.Pages[pageIndex], cancellationToken)
            : Task.FromResult<ImageSource?>(null);

    /// <summary>The crop and page size dialogs can also be used without a selection (they offer every page).</summary>
    private bool CanEditTargetsOrAll() => CanEditPages && !_inserting;

    /// <summary>The page the insert dialogs propose: the last selected page, or the current page (1-based).</summary>
    private int AnchorPageNumber() => (TargetPageIndices.Count > 0 ? TargetPageIndices[^1] : Viewer.CurrentPageIndex) + 1;

    /// <summary>Copies and opens a file to insert; failures are explained to the user and give null.</summary>
    private async Task<OpenSource?> AddSourceAsync(string path)
    {
        var fileName = Path.GetFileName(path);
        _inserting = true;
        NotifyCommands();
        try
        {
            return await Workspace.AddSourceAsync(path, _services.Loader);
        }
        catch (PdfDocumentException ex)
        {
            _logger.LogWarning("A file could not be inserted: {Failure} (cause {Cause})", ex.Failure, ex.InnerException?.GetType().Name);
            var message = ex.Failure is PdfOpenFailure.PasswordRequired or PdfOpenFailure.WrongPassword or PdfOpenFailure.UnsupportedSecurity
                ? Strings.Insert_Error_Encrypted
                : DocumentOpener.MessageFor(ex.Failure);
            await _messages.ShowErrorAsync(Strings.Insert_Error_Title, string.Format(CultureInfo.CurrentCulture, message, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("A file could not be copied for inserting: {ErrorType}", ex.GetType().Name);
            var message = ex switch
            {
                UnauthorizedAccessException => Strings.Open_Error_AccessDenied,
                FileNotFoundException or DirectoryNotFoundException => Strings.Open_Error_FileNotFound,
                _ => Strings.Open_Error_ReadFailed,
            };
            await _messages.ShowErrorAsync(Strings.Insert_Error_Title, string.Format(CultureInfo.CurrentCulture, message, fileName));
        }
        catch (ObjectDisposedException)
        {
            // The tab was closed meanwhile.
        }
        finally
        {
            _inserting = false;
            NotifyCommands();
        }

        return null;
    }

    private bool InsertSource(OpenSource source, string? sourcePages, int insertAt)
    {
        if (!CanEditPages)
        {
            return false;
        }

        var position = Math.Clamp(insertAt, 0, Viewer.PageCount) + 1;
        return Apply(_operations.Get<InsertPagesOperation>(), new InsertPagesParams(source.Source.Id, sourcePages, position));
    }

    private IReadOnlyList<HistoryEntryViewModel> BuildHistory()
    {
        var history = Workspace.Session.History;
        var redo = history.RedoHistory.Select((d, i) => new HistoryEntryViewModel(d, IsRedo: true, Steps: i + 1)).Reverse();
        var undo = history.UndoHistory.Select((d, i) => new HistoryEntryViewModel(d, IsRedo: false, Steps: i + 1));
        return [.. redo, .. undo];
    }

    private void OnPagesChanged(object? sender, EventArgs e)
    {
        Viewer.SetDocument(Workspace.Pages);
        Thumbnails.SetDocument(Workspace.Pages);
        Organizer?.SetDocument(Workspace.Pages);
        OnPropertyChanged(nameof(SplitSummary));
        OnPropertyChanged(nameof(CurrentSplitFile));
        OnTargetsChanged();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        HistoryEntries = BuildHistory();
        OnPropertyChanged(nameof(HistoryEntries));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        OnPropertyChanged(nameof(CanEditPages));
        NotifyCommands();
    }

    private void OnViewerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewerViewModel.PageCount) or nameof(DocumentViewerViewModel.CurrentPageIndex))
        {
            OnPropertyChanged(nameof(CurrentSplitFile));
            if (!IsOrganizing)
            {
                OnTargetsChanged();
            }
        }
    }

    private void OnOrganizerSelectionChanged(object? sender, EventArgs e)
    {
        if (IsOrganizing)
        {
            OnTargetsChanged();
        }
    }

    private void OnTargetsChanged()
    {
        OnPropertyChanged(nameof(TargetPageIndices));
        OnPropertyChanged(nameof(HasTargetPages));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        DeletePagesCommand.NotifyCanExecuteChanged();
        DuplicatePagesCommand.NotifyCanExecuteChanged();
        InsertBlankPagesCommand.NotifyCanExecuteChanged();
        InsertPagesFromFileCommand.NotifyCanExecuteChanged();
        ExtractPagesCommand.NotifyCanExecuteChanged();
        CropPagesCommand.NotifyCanExecuteChanged();
        ChangePageSizeCommand.NotifyCanExecuteChanged();
        MarkSplitCommand.NotifyCanExecuteChanged();
        ClearSplitMarksCommand.NotifyCanExecuteChanged();
        SplitCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Creates document tabs with their viewers (seam for MainWindowViewModel tests).</summary>
public interface IDocumentTabFactory
{
    DocumentTabViewModel Create(DocumentWorkspace workspace);
}

internal sealed class DocumentTabFactory(
    IPageImageRenderer renderer,
    RenderCache cache,
    IThumbnailProvider thumbnails,
    IUiDispatcher ui,
    IDebouncerFactory debouncers,
    OperationCatalog operations,
    IMessageService messages,
    IPageDialogService pageDialogs,
    IFileDialogService fileDialogs,
    IPdfDocumentLoader loader,
    ILoggerFactory loggers) : IDocumentTabFactory
{
    /// <summary>Builds the tab and starts reading the bookmarks in the background.</summary>
    public DocumentTabViewModel Create(DocumentWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var viewer = new DocumentViewerViewModel(workspace.Pages, renderer, cache, ui, debouncers, loggers.CreateLogger<DocumentViewerViewModel>());
        var thumbnailList = new ThumbnailListViewModel(workspace.Pages, workspace.SourcePathOf, viewer, thumbnails, ui, loggers.CreateLogger<ThumbnailListViewModel>());
        var bookmarks = new BookmarkTreeViewModel(workspace.BaseDocument, viewer, ui, loggers.CreateLogger<BookmarkTreeViewModel>());
        _ = bookmarks.LoadAsync();
        var services = new DocumentTabServices(
            operations,
            messages,
            pageDialogs,
            fileDialogs,
            loader,
            (w, host) => new PageOrganizerViewModel(w.Pages, w.SourcePathOf, host, thumbnails, ui, loggers.CreateLogger<PageOrganizerViewModel>()),
            (page, token) => LoadPreviewAsync(workspace, page, token));
        return new DocumentTabViewModel(workspace, viewer, thumbnailList, bookmarks, cache, services, loggers.CreateLogger<DocumentTabViewModel>());
    }

    /// <summary>A page as displayed, sharp enough for the crop preview box.</summary>
    private async Task<ImageSource?> LoadPreviewAsync(DocumentWorkspace workspace, VirtualPage page, CancellationToken cancellationToken)
    {
        if (page.Source is not { } source)
        {
            return null;
        }

        var request = new ThumbnailRequest(page, workspace.SourcePathOf(source) ?? string.Empty, DpiScale: 2, BoxWidth: CropPagesViewModel.PreviewBoxWidth);
        return await thumbnails.GetAsync(request, cancellationToken);
    }
}
