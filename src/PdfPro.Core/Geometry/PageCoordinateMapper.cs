namespace PdfPro.Core.Geometry;

/// <summary>
/// The single place where PDF page coordinates and screen coordinates are converted (AGENTS rule 6).
/// </summary>
/// <remarks>
/// PDF space: points, origin at the bottom-left of the page coordinate system, y up; the visible box
/// (CropBox ∩ MediaBox) need not start at (0, 0). View space: the displayed page, origin at its top-left, y down,
/// in <see cref="Scale"/> view units per point. The page is shown rotated clockwise by <see cref="RotationDegrees"/>
/// (its /Rotate plus any extra rotation), exactly like PDFium renders it.
/// </remarks>
public sealed class PageCoordinateMapper
{
    private readonly PdfRect _box;

    public PageCoordinateMapper(PageGeometry geometry, double scale)
        : this(geometry.Box, geometry.RotationDegrees, scale)
    {
    }

    /// <param name="box">The visible page box in PDF space (CropBox ∩ MediaBox).</param>
    /// <param name="rotationDegrees">Clockwise display rotation: any multiple of 90.</param>
    /// <param name="scale">View units per PDF point, e.g. zoom × 96/72 for WPF DIPs.</param>
    public PageCoordinateMapper(PdfRect box, int rotationDegrees, double scale)
    {
        if (box.IsEmpty)
        {
            throw new ArgumentException("The page box must have a positive width and height.", nameof(box));
        }

        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be a positive number.");
        }

        _box = box;
        RotationDegrees = PageGeometry.NormalizeRotation(rotationDegrees);
        Scale = scale;
        ViewSize = RotationDegrees is 90 or 270
            ? new ViewSize(box.Height * scale, box.Width * scale)
            : new ViewSize(box.Width * scale, box.Height * scale);
    }

    public int RotationDegrees { get; }

    public double Scale { get; }

    /// <summary>Size of the displayed page in view units.</summary>
    public ViewSize ViewSize { get; }

    public ViewPoint PdfToScreen(PdfPoint point)
    {
        var s = Scale;
        return RotationDegrees switch
        {
            0 => new ViewPoint((point.X - _box.Left) * s, (_box.Top - point.Y) * s),
            90 => new ViewPoint((point.Y - _box.Bottom) * s, (point.X - _box.Left) * s),
            180 => new ViewPoint((_box.Right - point.X) * s, (point.Y - _box.Bottom) * s),
            _ => new ViewPoint((_box.Top - point.Y) * s, (_box.Right - point.X) * s),
        };
    }

    public PdfPoint ScreenToPdf(ViewPoint point)
    {
        var s = Scale;
        return RotationDegrees switch
        {
            0 => new PdfPoint(_box.Left + (point.X / s), _box.Top - (point.Y / s)),
            90 => new PdfPoint(_box.Left + (point.Y / s), _box.Bottom + (point.X / s)),
            180 => new PdfPoint(_box.Right - (point.X / s), _box.Bottom + (point.Y / s)),
            _ => new PdfPoint(_box.Right - (point.Y / s), _box.Top - (point.X / s)),
        };
    }

    /// <summary>
    /// <see cref="ScreenToPdf"/> as a matrix acting on view coordinates written as (x, y): what content drawn in view
    /// space (top-left origin, y down, <see cref="Scale"/> units per point) needs as its "cm" to land on the page.
    /// </summary>
    public PdfMatrix ScreenToPdfMatrix
        => PdfMatrix.FromPoints(ScreenToPdf(new ViewPoint(0, 0)), ScreenToPdf(new ViewPoint(1, 0)), ScreenToPdf(new ViewPoint(0, 1)));

    /// <summary><see cref="PdfToScreen"/> as a matrix: view coordinates come out as (x, y).</summary>
    public PdfMatrix PdfToScreenMatrix => ScreenToPdfMatrix.Invert();

    public ViewRect PdfRectToScreen(PdfRect rect)
        => ViewRect.FromCorners(PdfToScreen(new PdfPoint(rect.Left, rect.Bottom)), PdfToScreen(new PdfPoint(rect.Right, rect.Top)));

    public PdfRect ScreenRectToPdf(ViewRect rect)
    {
        var a = ScreenToPdf(rect.TopLeft);
        var b = ScreenToPdf(new ViewPoint(rect.Right, rect.Bottom));
        return new PdfRect(a.X, a.Y, b.X, b.Y);
    }
}
