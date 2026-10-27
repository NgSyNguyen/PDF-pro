using PDFiumCore;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium.Native;

namespace PdfPro.Pdfium;

/// <summary>
/// An open PDF document. Gate-affine: every member must be used on the PDFium thread (inside
/// <see cref="PdfiumGate.RunAsync{T}"/>); <see cref="PdfDocumentHandle"/> is the async, thread-safe facade.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    /// <summary>Largest bitmap one render may allocate (≈ 400 MB in BGRx).</summary>
    public const long MaxBitmapPixels = 100_000_000;

    private const int PageCacheCapacity = 6;
    private const int MaxBookmarks = 50_000;
    private const int MaxBookmarkDepth = 64;
    private const ulong ActionGoTo = 1;

    private static readonly string[] MetadataTags = ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"];

    private readonly PdfiumGate _gate;
    private readonly DocumentSafeHandle _handle;
    private readonly FS_SIZEF_ _sizeBuffer = new();
    private readonly LinkedList<PdfiumPage> _pageCache = new(); // First = most recently used
    private bool _disposed;

    private PdfiumDocument(PdfiumGate gate, DocumentSafeHandle handle)
    {
        _gate = gate;
        _handle = handle;
        PageCount = fpdfview.FPDF_GetPageCount(handle.Native);
    }

    public int PageCount { get; }

    private FpdfDocumentT Native
    {
        get
        {
            _gate.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle.Native;
        }
    }

    /// <summary>Opens a document from a file. Must run on the PDFium thread.</summary>
    /// <exception cref="PdfDocumentException">The file is missing, unreadable, damaged or needs a (different) password.</exception>
    public static PdfiumDocument Open(PdfiumGate gate, string path, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.VerifyAccess();

        PdfFileSource? source = PdfFileSource.Open(path);
        try
        {
            var passwordSupplied = !string.IsNullOrEmpty(password);
            var native = fpdfview.FPDF_LoadCustomDocument(source.Access, passwordSupplied ? password : null);
            if (native is null || native.__Instance == IntPtr.Zero)
            {
                var error = (uint)(fpdfview.FPDF_GetLastError() & 0xFFFFFFFF);
                throw PdfiumErrors.ForLoad(error, passwordSupplied, source.ReadError);
            }

            var handle = new DocumentSafeHandle(gate, native, source);
            source = null; // owned by the handle from now on
            return new PdfiumDocument(gate, handle);
        }
        finally
        {
            source?.Dispose();
        }
    }

    /// <summary>Displayed page size in points without loading the page (after /Rotate, visible box only).</summary>
    public PdfSize GetPageSize(int pageIndex)
    {
        var native = Native;
        CheckPageIndex(pageIndex);
        return fpdfview.FPDF_GetPageSizeByIndexF(native, pageIndex, _sizeBuffer) != 0
            ? new PdfSize(_sizeBuffer.Width, _sizeBuffer.Height)
            : throw new PdfCorruptedFileException(PdfiumErrors.Page);
    }

    public IReadOnlyList<PdfSize> GetAllPageSizes()
    {
        var sizes = new PdfSize[PageCount];
        for (var i = 0; i < sizes.Length; i++)
        {
            sizes[i] = GetPageSize(i);
        }

        return sizes;
    }

    /// <summary>Loads a page the caller owns (dispose it). Rendering uses an internal page cache instead.</summary>
    public PdfiumPage LoadPage(int pageIndex)
    {
        var native = Native;
        CheckPageIndex(pageIndex);
        var page = fpdfview.FPDF_LoadPage(native, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            throw new PdfCorruptedFileException(PdfiumErrors.Page);
        }

        return new PdfiumPage(_gate, new PageSafeHandle(_gate, page, _handle), pageIndex);
    }

    public PageGeometry GetPageGeometry(int pageIndex) => GetCachedPage(pageIndex).Geometry;

    /// <summary>Raw text of a page (not normalized).</summary>
    public string GetPageText(int pageIndex)
    {
        using var text = GetCachedPage(pageIndex).LoadText();
        return text.GetText();
    }

    /// <summary>The characters of a page with their boxes (not normalized).</summary>
    public PageText GetPageTextLayer(int pageIndex)
    {
        using var text = GetCachedPage(pageIndex).LoadText();
        return text.GetLayer();
    }

    public PdfDocumentMetadata GetMetadata()
    {
        var native = Native;
        var values = MetadataTags
            .Select(tag => PdfiumStrings.ReadUtf16((buffer, length) => fpdf_doc.FPDF_GetMetaText(native, tag, buffer, length)))
            .ToArray();
        return new PdfDocumentMetadata(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5],
            PdfDate.TryParse(values[6], out var created) ? created : null,
            PdfDate.TryParse(values[7], out var modified) ? modified : null);
    }

    public PdfDocumentFeatures GetFeatures()
    {
        var native = Native;
        var version = 0;
        var fileVersion = fpdfview.FPDF_GetFileVersion(native, ref version) != 0 && version > 0
            ? $"{version / 10}.{version % 10}"
            : "?";
        return new PdfDocumentFeatures(
            fileVersion,
            fpdfview.FPDF_GetSecurityHandlerRevision(native),
            (uint)(fpdfview.FPDF_GetDocPermissions(native) & 0xFFFFFFFF),
            Math.Max(0, fpdf_signature.FPDF_GetSignatureCount(native)),
            fpdf_formfill.FPDF_GetFormType(native) switch
            {
                1 => PdfFormType.AcroForm,
                2 => PdfFormType.XfaFull,
                3 => PdfFormType.XfaForeground,
                _ => PdfFormType.None,
            },
            fpdf_javascript.FPDFDocGetJavaScriptActionCount(native) > 0);
    }

    /// <summary>The outline tree. Cycles and absurd depths in hostile files are cut off.</summary>
    public IReadOnlyList<PdfBookmark> GetBookmarks()
    {
        var native = Native;
        var visited = new HashSet<IntPtr>();
        var count = 0;
        return ReadBookmarks(native, parent: null, depth: 0, visited, ref count);
    }

    /// <summary>Renders a page (or a region of it) on a white background.</summary>
    public PageBitmap RenderPage(int pageIndex, PageRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.Scale) || options.Scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "The render scale must be a positive number.");
        }

        var page = GetCachedPage(pageIndex);
        var quarterTurns = ((options.ExtraQuarterTurns % 4) + 4) % 4;
        var size = page.DisplaySize;
        if (quarterTurns % 2 == 1)
        {
            size = new PdfSize(size.Height, size.Width);
        }

        var pageWidth = PixelSize.FromPoints(size.Width, options.Scale);
        var pageHeight = PixelSize.FromPoints(size.Height, options.Scale);
        var region = options.Region ?? new PixelRect(0, 0, pageWidth, pageHeight);
        if (region.IsEmpty || !new PixelRect(0, 0, pageWidth, pageHeight).Contains(region))
        {
            throw new ArgumentOutOfRangeException(nameof(options), region, "The render region must lie inside the page bitmap.");
        }

        if (region.Area > MaxBitmapPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(options), region.Area, "The requested bitmap is too large; render a region instead.");
        }

        var bitmap = fpdfview.FPDFBitmapCreate(region.Width, region.Height, 0);
        if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
        {
            throw new InsufficientMemoryException("PDFium could not allocate the page bitmap.");
        }

        var handle = new BitmapSafeHandle(_gate, bitmap);
        try
        {
            fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, region.Width, region.Height, 0xFFFFFFFF);
            fpdfview.FPDF_RenderPageBitmap(bitmap, page.Native, -region.X, -region.Y, pageWidth, pageHeight, quarterTurns, ToNativeFlags(options.Flags));
            return new PageBitmap(
                handle,
                fpdfview.FPDFBitmapGetBuffer(bitmap),
                region.Width,
                region.Height,
                fpdfview.FPDFBitmapGetStride(bitmap),
                pageWidth,
                pageHeight,
                region,
                options.Scale);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a complete copy of the document with a freshly built cross-reference table (no incremental update).
    /// Used to repair files other libraries cannot parse: PDFium reads damaged files and writes clean ones.
    /// </summary>
    /// <exception cref="IOException">Writing failed.</exception>
    public void SaveCopy(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        const ulong noIncremental = 2; // FPDF_NO_INCREMENTAL
        var native = Native;

        // After rebuilding a damaged cross-reference table, PDFium writes the document information dictionary only if
        // it has been loaded; otherwise the trailer's /Info points at nothing and stricter readers reject the copy.
        _ = fpdf_doc.FPDF_GetMetaText(native, "Producer", IntPtr.Zero, 0);
        using var writer = new PdfFileWriter(output);
        if (fpdf_save.FPDF_SaveAsCopy(native, writer.Native, noIncremental) == 0 || writer.Error is not null)
        {
            throw new IOException("PDFium could not write a copy of the document.", writer.Error);
        }
    }

    public void Dispose()
    {
        _gate.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var page in _pageCache)
        {
            page.Dispose();
        }

        _pageCache.Clear();
        _sizeBuffer.Dispose();
        _handle.Dispose();
    }

    private static int ToNativeFlags(PdfRenderFlags flags)
    {
        const int annot = 0x01;
        const int lcdText = 0x02;
        const int grayscale = 0x08;
        const int printing = 0x800;

        var native = 0;
        native |= flags.HasFlag(PdfRenderFlags.Annotations) ? annot : 0;
        native |= flags.HasFlag(PdfRenderFlags.LcdText) ? lcdText : 0;
        native |= flags.HasFlag(PdfRenderFlags.Grayscale) ? grayscale : 0;
        native |= flags.HasFlag(PdfRenderFlags.Printing) ? printing : 0;
        return native;
    }

    private static PdfDestination? ReadDestination(FpdfDocumentT document, FpdfBookmarkT bookmark)
    {
        var destination = fpdf_doc.FPDFBookmarkGetDest(document, bookmark);
        if (destination is null || destination.__Instance == IntPtr.Zero)
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (action is null || action.__Instance == IntPtr.Zero || (fpdf_doc.FPDFActionGetType(action) & 0xFFFFFFFF) != ActionGoTo)
            {
                return null;
            }

            destination = fpdf_doc.FPDFActionGetDest(document, action);
            if (destination is null || destination.__Instance == IntPtr.Zero)
            {
                return null;
            }
        }

        var pageIndex = fpdf_doc.FPDFDestGetDestPageIndex(document, destination);
        if (pageIndex < 0)
        {
            return null;
        }

        int hasX = 0, hasY = 0, hasZoom = 0;
        float x = 0, y = 0, zoom = 0;
        if (fpdf_doc.FPDFDestGetLocationInPage(destination, ref hasX, ref hasY, ref hasZoom, ref x, ref y, ref zoom) == 0)
        {
            return new PdfDestination(pageIndex, null, null, null);
        }

        return new PdfDestination(
            pageIndex,
            hasX != 0 ? x : null,
            hasY != 0 ? y : null,
            hasZoom != 0 && zoom > 0 ? zoom : null);
    }

    private List<PdfBookmark> ReadBookmarks(FpdfDocumentT document, FpdfBookmarkT? parent, int depth, HashSet<IntPtr> visited, ref int count)
    {
        var result = new List<PdfBookmark>();
        if (depth >= MaxBookmarkDepth)
        {
            return result;
        }

        var bookmark = fpdf_doc.FPDFBookmarkGetFirstChild(document, parent!);
        while (bookmark is not null && bookmark.__Instance != IntPtr.Zero && count < MaxBookmarks && visited.Add(bookmark.__Instance))
        {
            count++;
            var current = bookmark;
            var title = PdfiumStrings.ReadUtf16((buffer, length) => fpdf_doc.FPDFBookmarkGetTitle(current, buffer, length)) ?? string.Empty;
            var children = ReadBookmarks(document, current, depth + 1, visited, ref count);
            result.Add(new PdfBookmark(title, ReadDestination(document, current), children));
            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(document, current);
        }

        return result;
    }

    private PdfiumPage GetCachedPage(int pageIndex)
    {
        _ = Native;
        CheckPageIndex(pageIndex);
        for (var node = _pageCache.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == pageIndex)
            {
                _pageCache.Remove(node);
                _pageCache.AddFirst(node);
                return node.Value;
            }
        }

        var page = LoadPage(pageIndex);
        _pageCache.AddFirst(page);
        if (_pageCache.Count > PageCacheCapacity)
        {
            var evicted = _pageCache.Last!.Value;
            _pageCache.RemoveLast();
            evicted.Dispose();
        }

        return page;
    }

    private void CheckPageIndex(int pageIndex)
    {
        if ((uint)pageIndex >= (uint)PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The document has {PageCount} pages.");
        }
    }
}
