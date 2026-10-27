using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Materializing;

/// <summary>Page boxes as PDFium sees them, and the plan's rotation and crop applied to a PDFsharp page.</summary>
internal static class PageBoxes
{
    /// <summary>PDFium's fallback when a page has no usable /MediaBox (US Letter).</summary>
    private static readonly PdfRect DefaultMediaBox = new(0, 0, 612, 792);

    /// <summary>
    /// The visible box exactly as PDFium computes it (CPDF_Page::UpdateDimensions): the /CropBox clipped to the
    /// /MediaBox, or the /MediaBox. <see cref="PageCrop"/> rectangles are measured on this box.
    /// </summary>
    public static PdfRect VisibleBox(PdfPage page)
    {
        var media = ToRect(page.MediaBoxReadOnly);
        if (media.IsEmpty)
        {
            media = DefaultMediaBox;
        }

        if (!page.HasCropBox)
        {
            return media;
        }

        var crop = ToRect(page.CropBoxReadOnly);
        var clipped = PdfRect.Intersect(crop, media);
        return clipped.IsEmpty ? media : clipped;
    }

    /// <summary>The page's own /Rotate, normalized (0 for invalid values, as PDFium does).</summary>
    public static int Rotation(PdfPage page) => page.Rotate % 90 == 0 ? PageGeometry.NormalizeRotation(page.Rotate) : 0;

    /// <summary>Applies the plan's crop (converted to a CropBox) and extra rotation: the page gets <see cref="MaterializedGeometry"/>.</summary>
    public static void ApplyEdits(PdfPage page, PageRef pageRef)
    {
        var current = new PageGeometry(VisibleBox(page), Rotation(page));
        var result = MaterializedGeometry.Of(pageRef, current);
        if (pageRef.CropBoxOverride is not null && result.Box != current.Box)
        {
            page.CropBox = new PdfRectangle(new XPoint(result.Box.Left, result.Box.Bottom), new XPoint(result.Box.Right, result.Box.Top));
        }

        if (pageRef.RotationDelta != 0)
        {
            page.Rotate = result.RotationDegrees;
        }
    }

    /// <summary>A new blank page of <paramref name="size"/> points, not yet in a document.</summary>
    public static PdfPage Blank(PdfSize size)
        => new() { MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height)) };

    private static PdfRect ToRect(PdfRectangle rectangle) => new(rectangle.X1, rectangle.Y1, rectangle.X2, rectangle.Y2);
}
