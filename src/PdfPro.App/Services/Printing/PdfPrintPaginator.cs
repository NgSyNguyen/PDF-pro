using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using PdfPro.App.Services.Annotating;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Editing;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
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
    private readonly ScreenFonts? _fonts;
    private readonly ScreenImages _images = new();
    private Size _pageSize;

    /// <param name="fonts">Faces for the text of annotations made in the session (null: their text is left out).</param>
    public PdfPrintPaginator(VirtualDocument document, IPageImageRenderer renderer, IReadOnlyList<int> pageIndices, PrintSheet sheet, ILogger logger, CancellationToken cancellation, ScreenFonts? fonts = null)
    {
        _fonts = fonts;
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
            var image = Render(pageIndex, placement);
            using var context = visual.RenderOpen();
            if (image is not null)
            {
                context.DrawImage(image, new Rect(placement.Bounds.X, placement.Bounds.Y, placement.Bounds.Width, placement.Bounds.Height));
            }

            DrawAnnotations(context, pageIndex, placement);
        }

        // After cancellation the writer still asks for the page it is on; a blank sheet ends it quickly.
        var sheet = new Rect(_pageSize);
        return new DocumentPage(visual, _pageSize, sheet, sheet);
    }

    /// <summary>
    /// Annotations made in the session are not in the source file PDFium renders: they are drawn over the page, in the
    /// page's space as written, turned and scaled like the page on the sheet.
    /// </summary>
    private void DrawAnnotations(DrawingContext context, int pageIndex, PrintPlacement placement)
    {
        var page = _document.Pages[pageIndex];
        if (page.Ref.Annotations.Count == 0)
        {
            return;
        }

        PageGeometry? source = null;
        if (page.Source is { } document)
        {
            try
            {
                // Completes on the PDFium thread, never on this one (as rendering does).
                source = document.GetPageGeometryAsync(page.SourcePageIndex, _cancellation).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The annotations of page {PageNumber} could not be placed for printing", pageIndex + 1);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var geometry = MaterializedGeometry.Of(page.Ref, source);
        if (placement.Rotated)
        {
            geometry = new PageGeometry(geometry.Box, geometry.RotationDegrees + 90);
        }

        var mapper = new PageCoordinateMapper(geometry, placement.Scale);
        context.PushTransform(new TranslateTransform(placement.Bounds.X, placement.Bounds.Y));
        foreach (var annotation in page.Ref.Annotations)
        {
            try
            {
                AnnotationDrawing.Draw(context, annotation, mapper, _fonts, _images, pixelsPerDip: 1);
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
            {
                _logger.LogWarning("An annotation of page {PageNumber} could not be printed: {ErrorType}", pageIndex + 1, ex.GetType().Name);
            }
        }

        context.Pop();
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
