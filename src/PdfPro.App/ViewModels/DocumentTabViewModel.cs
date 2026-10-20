using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>An entry of the history menu: undoing or redoing up to this step.</summary>
/// <param name="Description">The step's name ("Xoay 3 trang").</param>
/// <param name="IsRedo">True for steps that can be redone, false for steps that can be undone.</param>
/// <param name="Steps">How many steps to undo or redo to reach this entry.</param>
public sealed record HistoryEntryViewModel(string Description, bool IsRedo, int Steps);

/// <summary>
/// An open document tab: its workspace (session, sources), viewer and sidebar panels, undo/redo and the page commands.
/// Closing the tab closes the document and removes its session folder. Must be used on the UI thread.
/// </summary>
public sealed partial class DocumentTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    private readonly RenderCache _cache;
    private readonly OperationCatalog _operations;
    private readonly IMessageService _messages;
    private readonly ILogger _logger;
    private int _disposed;

    public DocumentTabViewModel(
        DocumentWorkspace workspace,
        DocumentViewerViewModel viewer,
        ThumbnailListViewModel thumbnails,
        BookmarkTreeViewModel bookmarks,
        RenderCache cache,
        OperationCatalog operations,
        IMessageService messages,
        ILogger<DocumentTabViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
        Viewer = viewer;
        Thumbnails = thumbnails;
        Bookmarks = bookmarks;
        _cache = cache;
        _operations = operations;
        _messages = messages;
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

    /// <summary>The file name, with " *" while there are unsaved changes.</summary>
    public override string Title => IsDirty ? FileName + " *" : FileName;

    public override string ToolTip => FilePath;

    public override SymbolRegular Icon => SymbolRegular.DocumentPdf24;

    public override bool CanClose => true;

    public bool IsDirty => Workspace.IsDirty;

    /// <summary>Password protected documents can be viewed and printed but not edited or saved yet.</summary>
    public bool IsReadOnly => Workspace.IsReadOnly;

    /// <summary>"Hoàn tác: Xoay 1 trang", or just "Hoàn tác" when there is nothing to undo.</summary>
    public string UndoToolTip => Workspace.Session.History.NextUndoDescription is { } step
        ? string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, step)
        : Strings.Cmd_Undo;

    public string RedoToolTip => Workspace.Session.History.NextRedoDescription is { } step
        ? string.Format(CultureInfo.CurrentCulture, Strings.Cmd_RedoWith, step)
        : Strings.Cmd_Redo;

    /// <summary>The history menu: redoable steps (furthest first), then undoable steps (most recent first).</summary>
    public IReadOnlyList<HistoryEntryViewModel> HistoryEntries { get; private set; }

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
        Thumbnails.Dispose();
        Bookmarks.Dispose();
        Viewer.Dispose();
        foreach (var source in Workspace.Sources)
        {
            _cache.RemoveDocument(source.Document.Id);
        }

        // Application exit waits for this on the UI thread: do not resume there.
        await Workspace.DisposeAsync().ConfigureAwait(false);
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

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotateLeft() => ApplyToCurrentPage(pages => (_operations.Get<RotatePagesOperation>(), new RotatePagesParams(pages, -90)));

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void RotateRight() => ApplyToCurrentPage(pages => (_operations.Get<RotatePagesOperation>(), new RotatePagesParams(pages, 90)));

    [RelayCommand(CanExecute = nameof(CanDeletePages))]
    private void DeletePages() => ApplyToCurrentPage(pages => (_operations.Get<DeletePagesOperation>(), new DeletePagesParams(pages)));

    [RelayCommand(CanExecute = nameof(CanEditPages))]
    private void DuplicatePages() => ApplyToCurrentPage(pages => (_operations.Get<DuplicatePagesOperation>(), new DuplicatePagesParams(pages)));

    private bool CanEditPages() => Workspace.CanEdit && Viewer.PageCount > 0;

    private bool CanDeletePages() => CanEditPages() && Viewer.PageCount > 1;

    /// <summary>In the viewer, page commands act on the current page.</summary>
    private void ApplyToCurrentPage(Func<string, (IPlanOperation Operation, object Parameters)> build)
    {
        var (operation, parameters) = build(PageRanges.Format([Viewer.CurrentPageIndex]));
        Apply(operation, parameters);
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
        NotifyCommands();
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        HistoryEntries = BuildHistory();
        OnPropertyChanged(nameof(HistoryEntries));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        NotifyCommands();
    }

    private void OnViewerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewerViewModel.PageCount))
        {
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        DeletePagesCommand.NotifyCanExecuteChanged();
        DuplicatePagesCommand.NotifyCanExecuteChanged();
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
        return new DocumentTabViewModel(workspace, viewer, thumbnailList, bookmarks, cache, operations, messages, loggers.CreateLogger<DocumentTabViewModel>());
    }
}
