using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;

namespace PdfPro.Pdfium;

/// <summary>
/// Thread-safe, async facade over an open <see cref="PdfiumDocument"/>: every call is scheduled on the
/// <see cref="PdfiumGate"/>. Page sizes and document features are read once when opening.
/// </summary>
public sealed class PdfDocumentHandle : IPdfDocument
{
    private readonly PdfiumGate _gate;
    private readonly PdfiumDocument _document;
    private int _disposed;

    private PdfDocumentHandle(PdfiumGate gate, PdfiumDocument document, IReadOnlyList<PdfSize> pageSizes, PdfDocumentFeatures features)
    {
        _gate = gate;
        _document = document;
        PageSizes = pageSizes;
        Features = features;
    }

    /// <summary>Unique per opened instance; used in cache keys.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public int PageCount => PageSizes.Count;

    /// <summary>Displayed size of every page in points (after /Rotate, visible box only).</summary>
    public IReadOnlyList<PdfSize> PageSizes { get; }

    public PdfDocumentFeatures Features { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Opens a document. If the token is cancelled after opening has started, the handle is still returned
    /// and must be disposed by the caller.
    /// </summary>
    /// <exception cref="PdfDocumentException">The file is missing, unreadable, damaged or needs a (different) password.</exception>
    public static Task<PdfDocumentHandle> OpenAsync(PdfiumGate gate, string path, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return gate.RunAsync(
            () =>
            {
                var document = PdfiumDocument.Open(gate, path, password);
                try
                {
                    return new PdfDocumentHandle(gate, document, document.GetAllPageSizes(), document.GetFeatures());
                }
                catch
                {
                    document.Dispose();
                    throw;
                }
            },
            PdfiumPriority.High,
            cancellationToken);
    }

    public Task<PageBitmap> RenderPageAsync(int pageIndex, PageRenderOptions options, PdfiumPriority priority = PdfiumPriority.High, CancellationToken cancellationToken = default)
        => Run(document => document.RenderPage(pageIndex, options), priority, cancellationToken);

    public Task<PageGeometry> GetPageGeometryAsync(int pageIndex, CancellationToken cancellationToken = default)
        => Run(document => document.GetPageGeometry(pageIndex), PdfiumPriority.High, cancellationToken);

    public Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default)
        => Run(document => document.GetBookmarks(), PdfiumPriority.Normal, cancellationToken);

    public Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        => Run(document => document.GetMetadata(), PdfiumPriority.Normal, cancellationToken);

    /// <summary>Raw page text (not normalized).</summary>
    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default)
        => Run(document => document.GetPageText(pageIndex), PdfiumPriority.Normal, cancellationToken);

    /// <summary>The characters of a page with their boxes in its PDF user space (not normalized).</summary>
    public Task<PageText> GetPageTextLayerAsync(int pageIndex, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
        => Run(document => document.GetPageTextLayer(pageIndex), priority, cancellationToken);

    public Task<IReadOnlyList<FileAnnotation>> GetPageAnnotationsAsync(int pageIndex, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
        => Run(document => document.GetPageAnnotations(pageIndex), priority, cancellationToken);

    /// <summary>Runs custom gate-affine work against the document (advanced use and tests).</summary>
    public Task<T> RunAsync<T>(Func<PdfiumDocument, T> work, PdfiumPriority priority = PdfiumPriority.Normal, CancellationToken cancellationToken = default)
        => Run(work, priority, cancellationToken);

    /// <summary>Closes the document. Work queued after this point fails with <see cref="ObjectDisposedException"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _gate.RunAsync(_document.Dispose, PdfiumPriority.High).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The gate is already shut down (application exit): the library is gone, nothing left to free.
        }
    }

    private Task<T> Run<T>(Func<PdfiumDocument, T> work, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _gate.RunAsync(() => work(_document), priority, cancellationToken);
    }
}
