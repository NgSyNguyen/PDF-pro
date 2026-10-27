using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>
/// The geometry a page of the plan has once written to PDF: the source page's visible box and /Rotate, cut down to the
/// plan's crop (a /CropBox) and turned by the plan's rotation. Content operations draw in this page's space, so the
/// editor places objects with it, and the materializer writes exactly it.
/// </summary>
public static class MaterializedGeometry
{
    /// <param name="page">The page of the plan.</param>
    /// <param name="source">The source page's own geometry (ignored for blank pages).</param>
    /// <exception cref="ArgumentNullException">A page with a source and no source geometry.</exception>
    public static PageGeometry Of(PageRef page, PageGeometry? source)
    {
        ArgumentNullException.ThrowIfNull(page);
        var start = page.BlankSize is { } size
            ? new PageGeometry(new PdfRect(0, 0, size.Width, size.Height), 0)
            : source ?? throw new ArgumentNullException(nameof(source), "A source page needs its geometry.");
        var box = start.Box;
        if (page.CropBoxOverride is { } crop)
        {
            // The crop is measured on the page as displayed with its own /Rotate (AGENTS rule 6: through the mapper).
            var mapper = new PageCoordinateMapper(start.Box, start.RotationDegrees, scale: 1);
            var area = PdfRect.Intersect(mapper.ScreenRectToPdf(new ViewRect(crop.Left, crop.Top, crop.Width, crop.Height)), start.Box);
            if (!area.IsEmpty)
            {
                box = area;
            }
        }

        return new PageGeometry(box, start.RotationDegrees + page.RotationDelta);
    }
}
