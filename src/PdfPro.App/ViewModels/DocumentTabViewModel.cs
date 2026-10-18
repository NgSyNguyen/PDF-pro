using System.IO;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Pdfium;
using Wpf.Ui.Controls;

namespace PdfPro.App.ViewModels;

/// <summary>An open document: owns the PDF handle, its viewer and sidebar panels; closing the tab closes the document.</summary>
public sealed class DocumentTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    private readonly RenderCache _cache;
    private int _disposed;

    public DocumentTabViewModel(
        IPdfDocument document,
        string filePath,
        DocumentViewerViewModel viewer,
        ThumbnailListViewModel thumbnails,
        BookmarkTreeViewModel bookmarks,
        RenderCache cache)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Document = document;
        FilePath = filePath;
        Viewer = viewer;
        Thumbnails = thumbnails;
        Bookmarks = bookmarks;
        _cache = cache;
    }

    public IPdfDocument Document { get; }

    public string FilePath { get; }

    public DocumentViewerViewModel Viewer { get; }

    public ThumbnailListViewModel Thumbnails { get; }

    public BookmarkTreeViewModel Bookmarks { get; }

    public override string Title => Path.GetFileName(FilePath);

    public override string ToolTip => FilePath;

    public override SymbolRegular Icon => SymbolRegular.DocumentPdf24;

    public override bool CanClose => true;

    public bool IsSameFile(string path) => string.Equals(Path.GetFullPath(path), FilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops rendering and loading, drops cached images and closes the document.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Thumbnails.Dispose();
        Bookmarks.Dispose();
        Viewer.Dispose();
        _cache.RemoveDocument(Document.Id);
        await Document.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Creates document tabs with their viewers (seam for MainWindowViewModel tests).</summary>
public interface IDocumentTabFactory
{
    DocumentTabViewModel Create(IPdfDocument document, string filePath);
}

internal sealed class DocumentTabFactory(
    IPageImageRenderer renderer,
    RenderCache cache,
    IThumbnailProvider thumbnails,
    IUiDispatcher ui,
    IDebouncerFactory debouncers,
    ILoggerFactory loggers) : IDocumentTabFactory
{
    /// <summary>Builds the tab and starts reading the bookmarks in the background.</summary>
    public DocumentTabViewModel Create(IPdfDocument document, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var viewer = new DocumentViewerViewModel(document, renderer, cache, ui, debouncers, loggers.CreateLogger<DocumentViewerViewModel>());
        var thumbnailList = new ThumbnailListViewModel(document, fullPath, viewer, thumbnails, ui, loggers.CreateLogger<ThumbnailListViewModel>());
        var bookmarks = new BookmarkTreeViewModel(document, viewer, ui, loggers.CreateLogger<BookmarkTreeViewModel>());
        _ = bookmarks.LoadAsync();
        return new DocumentTabViewModel(document, fullPath, viewer, thumbnailList, bookmarks, cache);
    }
}
