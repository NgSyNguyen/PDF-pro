using PDFiumCore;
using PdfPro.Core.Geometry;

namespace PdfPro.Pdfium;

/// <summary>Cleaning documents (security.sanitize) through PDFium's page object API.</summary>
public sealed partial class PdfiumDocument
{
    /// <summary>How far outside the visible box (points) an object may reach and still count as on the page.</summary>
    private const double OutsideTolerance = 0.5;

    /// <summary>
    /// Removes the top-level objects drawn entirely outside the page's visible box (nothing of them can be seen, but
    /// they are in the file) and rewrites the page when any went. Text drawn as a clipping path is kept: it shapes what
    /// follows. Returns how many objects went.
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium could not rewrite the page.</exception>
    public int RemoveObjectsOutsideVisibleBox(int pageIndex)
    {
        CheckPageIndex(pageIndex);
        var box = GetPageGeometry(pageIndex).Box;
        var visible = new PdfRect(box.Left - OutsideTolerance, box.Bottom - OutsideTolerance, box.Right + OutsideTolerance, box.Top + OutsideTolerance);
        var page = GetCachedPage(pageIndex).Native;
        var removed = 0;
        foreach (var item in TopLevelObjects(page))
        {
            if (Bounds(item) is not { } bounds || RectClipping.Overlaps(bounds, visible) || (bounds.IsEmpty && visible.Contains(new PdfPoint(bounds.Left, bounds.Bottom))))
            {
                continue;
            }

            if (fpdf_edit.FPDFPageObjGetType(item) == 1 && fpdf_edit.FPDFTextObjGetTextRenderMode(item) is >= FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_FILL_CLIP)
            {
                continue;
            }

            Remove(page, item);
            removed++;
        }

        if (removed > 0 && fpdf_edit.FPDFPageGenerateContent(page) == 0)
        {
            throw new InvalidOperationException($"PDFium could not rewrite the content of page {pageIndex + 1}.");
        }

        return removed;
    }

    /// <summary>
    /// Whether a page of two documents looks the same (rendered at 100 DPI without annotations; a handful of pixels may
    /// differ by anti-aliasing).
    /// </summary>
    public static bool PagesLookAlike(PdfiumDocument first, PdfiumDocument second, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.LooksAlike(pageIndex, first.CheckImage(pageIndex), second.CheckImage(pageIndex), [], []);
    }
}
