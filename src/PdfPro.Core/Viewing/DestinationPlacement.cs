using PdfPro.Core.Geometry;

namespace PdfPro.Core.Viewing;

/// <summary>Where the target of a bookmark or link lands on its page.</summary>
public static class DestinationPlacement
{
    /// <summary>
    /// The point of the displayed page (view units from its top-left at <paramref name="scale"/>) to bring to the
    /// top-left of the viewport for a destination whose top-left corner is (<paramref name="left"/>,
    /// <paramref name="top"/>) in PDF user space. A missing or non-finite coordinate falls back to the displayed
    /// page's top-left corner; the result is clamped to the page.
    /// </summary>
    public static ViewPoint PointInPage(PageGeometry geometry, double? left, double? top, double scale)
    {
        var mapper = new PageCoordinateMapper(geometry, scale);
        var origin = mapper.ScreenToPdf(new ViewPoint(0, 0));
        var target = new PdfPoint(
            left is { } x && double.IsFinite(x) ? x : origin.X,
            top is { } y && double.IsFinite(y) ? y : origin.Y);
        var point = mapper.PdfToScreen(target);
        var size = mapper.ViewSize;
        return new ViewPoint(Math.Clamp(point.X, 0, size.Width), Math.Clamp(point.Y, 0, size.Height));
    }
}
