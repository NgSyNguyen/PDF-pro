using PdfPro.Core.Geometry;

namespace PdfPro.Pdfium;

/// <summary>An open document, as used by viewers and operations (implemented by <see cref="PdfDocumentHandle"/>).</summary>
public interface IPdfDocument : IAsyncDisposable
{
    /// <summary>Unique per opened instance; used in cache keys.</summary>
    Guid Id { get; }

    int PageCount { get; }

    /// <summary>Displayed size of every page in points (after /Rotate, visible box only).</summary>
    IReadOnlyList<PdfSize> PageSizes { get; }

    PdfDocumentFeatures Features { get; }

    bool IsDisposed { get; }

    Task<PageBitmap> RenderPageAsync(int pageIndex, PageRenderOptions options, PdfiumPriority priority = PdfiumPriority.High, CancellationToken cancellationToken = default);

    Task<PageGeometry> GetPageGeometryAsync(int pageIndex, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default);

    Task<PdfDocumentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);

    Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default);
}

/// <summary>Opens documents; the seam that lets view models be tested without PDFium.</summary>
public interface IPdfDocumentLoader
{
    /// <exception cref="PdfDocumentException">The file is missing, unreadable, damaged or needs a (different) password.</exception>
    Task<IPdfDocument> LoadAsync(string path, string? password, CancellationToken cancellationToken = default);
}

/// <summary>Loads documents through the PDFium gate.</summary>
public sealed class PdfDocumentLoader(PdfiumGate gate) : IPdfDocumentLoader
{
    public async Task<IPdfDocument> LoadAsync(string path, string? password, CancellationToken cancellationToken = default)
        => await PdfDocumentHandle.OpenAsync(gate, path, password, cancellationToken).ConfigureAwait(false);
}
