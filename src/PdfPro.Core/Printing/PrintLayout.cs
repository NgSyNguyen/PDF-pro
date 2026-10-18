using PdfPro.Core.Geometry;

namespace PdfPro.Core.Printing;

/// <summary>Where a page goes on the sheet.</summary>
/// <param name="Bounds">Area covered by the page, in device-independent units (1/96 inch) from the sheet's top-left.</param>
/// <param name="Rotated">The page is turned a quarter clockwise so its orientation matches the printable area's.</param>
/// <param name="Scale">Device-independent units per PDF point.</param>
public readonly record struct PrintPlacement(ViewRect Bounds, bool Rotated, double Scale);

/// <summary>"Fit to paper" placement used by printing.</summary>
public static class PrintLayout
{
    /// <summary>Device-independent units (1/96 inch) per PDF point (1/72 inch).</summary>
    public const double UnitsPerPoint = 96.0 / 72.0;

    /// <summary>
    /// Fits a page into <paramref name="printableArea"/>: turned a quarter when the page is landscape and the area
    /// portrait (or the reverse), scaled up or down to fill the area without distortion, and centred.
    /// </summary>
    /// <param name="page">Displayed page size in points (after its /Rotate).</param>
    public static PrintPlacement Fit(PdfSize page, ViewRect printableArea)
    {
        if (page.Width <= 0 || page.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "The page must have a positive size.");
        }

        if (printableArea.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(printableArea), printableArea, "The printable area must not be empty.");
        }

        var areaIsLandscape = printableArea.Width > printableArea.Height;
        var pageIsLandscape = page.Width > page.Height;
        var rotated = page.Width != page.Height && printableArea.Width != printableArea.Height && pageIsLandscape != areaIsLandscape;
        var placed = rotated ? new PdfSize(page.Height, page.Width) : page;

        var scale = Math.Min(printableArea.Width / placed.Width, printableArea.Height / placed.Height);
        var width = placed.Width * scale;
        var height = placed.Height * scale;
        var bounds = new ViewRect(
            printableArea.X + ((printableArea.Width - width) / 2),
            printableArea.Y + ((printableArea.Height - height) / 2),
            width,
            height);
        return new PrintPlacement(bounds, rotated, scale);
    }
}
