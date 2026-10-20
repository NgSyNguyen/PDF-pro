using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Printing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Printing;

/// <summary>
/// Pages for the XPS print path: each page is rendered by PDFium with print flags at the printer's resolution
/// (at most <see cref="MaxDpi"/>), fitted to the printable area and turned to match the sheet's orientation. Pages
/// that cannot be rendered print blank and are counted. Runs on the print thread; <see cref="GetPage"/> blocks
/// while PDFium renders, which is why printing never runs on the UI thread.
/// </summary>
internal sealed class PdfPrintPaginator : DocumentPaginator
{
    /// <summary>Above this, sheets get much larger and slower to spool without a visible gain.</summary>
    public const double MaxDpi = 300;

    private readonly VirtualDocument _document;
    private readonly IPageImageRenderer _renderer;
    private readonly IReadOnlyList<int> _pages;
    private readonly PrintSheet _sheet;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellation;
    private Size _pageSize;

    public PdfPrintPaginator(VirtualDocument document, IPageImageRenderer renderer, IReadOnlyList<int> pageIndices, PrintSheet sheet, ILogger logger, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pageIndices);
        ArgumentNullException.ThrowIfNull(sheet);
        _document = document;
        _renderer = renderer;
        _pages = pageIndices;
        _sheet = sheet;
        _logger = logger;
        _cancellation = cancellation;
        _pageSize = new Size(sheet.Paper.Width, sheet.Paper.Height);
    }

    /// <summary>Pages printed blank because rendering failed (read after the job ended).</summary>
    public int FailedPages { get; private set; }

    public override bool IsPageCountValid => true;

    public override int PageCount => _pages.Count;

    public override Size PageSize
    {
        get => _pageSize;
        set => _pageSize = value;
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        var pageIndex = _pages[pageNumber];
        var visual = new DrawingVisual();
        if (!_cancellation.IsCancellationRequested)
        {
            var placement = PrintLayout.Fit(_document.PageSizes[pageIndex], _sheet.PrintableArea);
            if (Render(pageIndex, placement) is { } image)
            {
                using var context = visual.RenderOpen();
                context.DrawImage(image, new Rect(placement.Bounds.X, placement.Bounds.Y, placement.Bounds.Width, placement.Bounds.Height));
            }
        }

        // After cancellation the writer still asks for the page it is on; a blank sheet ends it quickly.
        var sheet = new Rect(_pageSize);
        return new DocumentPage(visual, _pageSize, sheet, sheet);
    }

    private ImageSource? Render(int pageIndex, PrintPlacement placement)
    {
        var page = _document.Pages[pageIndex];
        if (page.Source is not { } source)
        {
            return null; // a blank page prints as an empty sheet
        }

        var dpi = Math.Clamp(_sheet.Dpi, 72, MaxDpi);
        var options = page.RenderOptions(
            placement.Scale * dpi / 96.0,
            region: null,
            PdfRenderFlags.Printing | PdfRenderFlags.Annotations,
            extraQuarterTurns: placement.Rotated ? 1 : 0);

        try
        {
            // The renderer completes on the PDFium thread, never on this one, so blocking cannot deadlock.
            return _renderer.RenderAsync(source, page.SourcePageIndex, options, PdfiumPriority.Normal, _cancellation).GetAwaiter().GetResult().Image;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailedPages++;
            _logger.LogWarning(ex, "Page {PageNumber} could not be rendered for printing and was left blank", pageIndex + 1);
            return null;
        }
    }
}
