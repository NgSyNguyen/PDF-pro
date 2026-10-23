using PdfPro.Core.Geometry;

namespace PdfPro.Core.Stamping;

/// <summary>Nine places on a page; headers and footers use the top and bottom rows.</summary>
public enum StampAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>
/// Where stamps go on a page, in the page's displayed space (points, origin top-left, y down — the view space of a
/// <see cref="PageCoordinateMapper"/> at scale 1).
/// </summary>
public static class StampPlacement
{
    /// <summary>Most tiles a watermark may repeat on one page.</summary>
    public const int MaxTiles = 2000;

    /// <summary>
    /// The centre of content of <paramref name="content"/> size (already turned by <paramref name="angle"/> degrees
    /// clockwise) placed at <paramref name="anchor"/> with the given margins from the page edges.
    /// </summary>
    public static ViewPoint Place(PdfSize page, PdfSize content, double angle, StampAnchor anchor, double marginX, double marginY)
    {
        var bounds = RotatedSize(content, angle);
        var column = (int)anchor % 3;
        var row = (int)anchor / 3;
        var x = column switch
        {
            0 => marginX + (bounds.Width / 2),
            1 => page.Width / 2,
            _ => page.Width - marginX - (bounds.Width / 2),
        };
        var y = row switch
        {
            0 => marginY + (bounds.Height / 2),
            1 => page.Height / 2,
            _ => page.Height - marginY - (bounds.Height / 2),
        };
        return new ViewPoint(x, y);
    }

    /// <summary>
    /// Centres that cover the whole page with copies of the turned content, <paramref name="gap"/> points apart, every
    /// other row shifted by half a step. Copies that only touch the page are included; at most <see cref="MaxTiles"/>.
    /// </summary>
    public static IReadOnlyList<ViewPoint> Tile(PdfSize page, PdfSize content, double angle, double gap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gap);
        var bounds = RotatedSize(content, angle);
        var stepX = Math.Max(1, bounds.Width + gap);
        var stepY = Math.Max(1, bounds.Height + gap);
        var centers = new List<ViewPoint>();
        for (var row = 0; ; row++)
        {
            var y = (bounds.Height / 2) + (row * stepY) - (stepY / 2);
            if (y - (bounds.Height / 2) > page.Height)
            {
                break;
            }

            var shift = row % 2 == 1 ? stepX / 2 : 0;
            for (var x = (bounds.Width / 2) - stepX + shift; x - (bounds.Width / 2) <= page.Width; x += stepX)
            {
                if (x + (bounds.Width / 2) >= 0)
                {
                    centers.Add(new ViewPoint(x, y));
                    if (centers.Count == MaxTiles)
                    {
                        return centers;
                    }
                }
            }
        }

        return centers;
    }

    /// <summary>Width and height of the box around content turned by <paramref name="angle"/> degrees.</summary>
    public static PdfSize RotatedSize(PdfSize content, double angle)
    {
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        var (s, c) = (Math.Abs(sin), Math.Abs(cos));
        return new PdfSize((content.Width * c) + (content.Height * s), (content.Width * s) + (content.Height * c));
    }
}
