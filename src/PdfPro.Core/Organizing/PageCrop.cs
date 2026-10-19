using PdfPro.Core.Geometry;

namespace PdfPro.Core.Organizing;

/// <summary>Margins to cut off a page, in points, as the user sees the page (left, top, right, bottom).</summary>
public readonly record struct PageMargins
{
    public PageMargins(double left, double top, double right, double bottom)
    {
        Left = NonNegative(left, nameof(left));
        Top = NonNegative(top, nameof(top));
        Right = NonNegative(right, nameof(right));
        Bottom = NonNegative(bottom, nameof(bottom));
    }

    public double Left { get; }

    public double Top { get; }

    public double Right { get; }

    public double Bottom { get; }

    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    public static PageMargins Uniform(double margin) => new(margin, margin, margin, margin);

    private static double NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A margin must be a finite number of points, zero or more.");
        }

        return value;
    }
}

/// <summary>
/// The part of a page that a crop keeps visible, in points. It is measured on the page as displayed with its own
/// /Rotate, before the plan's extra rotation and before any crop: origin at the top-left of the visible box, x to the
/// right, y down. Stored this way, viewers know a cropped page's size without loading the page; the materializer
/// turns the rectangle into a PDF CropBox with <see cref="PageCoordinateMapper"/>.
/// </summary>
public readonly record struct PageCrop
{
    /// <summary>The smallest width or height a crop may leave, in points (a quarter inch).</summary>
    public const double MinimumSide = 18;

    /// <summary>Crops within this distance of the page edges count as the whole page (points).</summary>
    private const double Tolerance = 0.01;

    public PageCrop(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || left < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left), left, "The crop must start inside the page.");
        }

        if (!double.IsFinite(top) || top < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(top), top, "The crop must start inside the page.");
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The crop width must be a positive number.");
        }

        if (!double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "The crop height must be a positive number.");
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public PdfSize Size => new(Width, Height);

    /// <summary>The same area of the page after the page (of size <paramref name="pageSize"/>) turns clockwise.</summary>
    /// <param name="pageSize">Size of the page this crop is measured on.</param>
    /// <param name="quarterTurns">Clockwise quarter turns; any integer (negative turns counter-clockwise).</param>
    public PageCrop Rotate(PdfSize pageSize, int quarterTurns)
    {
        // Rounding may leave -1e-13 where the crop touches an edge; clamp so the result stays valid.
        return (((quarterTurns % 4) + 4) % 4) switch
        {
            0 => this,
            1 => new PageCrop(Math.Max(0, pageSize.Height - Bottom), Left, Height, Width),
            2 => new PageCrop(Math.Max(0, pageSize.Width - Right), Math.Max(0, pageSize.Height - Bottom), Width, Height),
            _ => new PageCrop(Top, Math.Max(0, pageSize.Width - Right), Height, Width),
        };
    }

    /// <summary>True when the crop keeps the whole page (within a hundredth of a point).</summary>
    public bool CoversWholePage(PdfSize pageSize)
        => Left <= Tolerance && Top <= Tolerance
            && Right >= pageSize.Width - Tolerance && Bottom >= pageSize.Height - Tolerance;

    /// <summary>
    /// True when cutting <paramref name="margins"/> off what is visible still leaves at least
    /// <see cref="MinimumSide"/> points in each direction.
    /// </summary>
    public static bool Fits(PdfSize pageSize, int rotationDelta, PageCrop? current, PageMargins margins)
    {
        var shown = Shown(pageSize, rotationDelta, current, out _);
        return shown.Width - margins.Left - margins.Right >= MinimumSide
            && shown.Height - margins.Top - margins.Bottom >= MinimumSide;
    }

    /// <summary>
    /// Cuts margins off what the user currently sees of a page and returns the new crop, or null when the result is the
    /// whole page again.
    /// </summary>
    /// <param name="pageSize">The page as displayed with its own /Rotate, uncropped (the crop's reference frame).</param>
    /// <param name="rotationDelta">The plan's extra clockwise rotation, in degrees.</param>
    /// <param name="current">The page's current crop, if any; the margins are cut from that area.</param>
    /// <param name="margins">Margins as the user sees the page, i.e. after <paramref name="rotationDelta"/>.</param>
    /// <exception cref="ArgumentException">The margins leave less than <see cref="MinimumSide"/> points.</exception>
    public static PageCrop? Apply(PdfSize pageSize, int rotationDelta, PageCrop? current, PageMargins margins)
    {
        var shown = Shown(pageSize, rotationDelta, current, out var turns);
        var width = shown.Width - margins.Left - margins.Right;
        var height = shown.Height - margins.Top - margins.Bottom;
        if (width < MinimumSide || height < MinimumSide)
        {
            throw new ArgumentException($"The margins must leave at least {MinimumSide} points in each direction.", nameof(margins));
        }

        var rotatedSize = turns % 2 == 1 ? new PdfSize(pageSize.Height, pageSize.Width) : pageSize;
        var cut = new PageCrop(shown.Left + margins.Left, shown.Top + margins.Top, width, height);
        var result = cut.Rotate(rotatedSize, -turns);
        return result.CoversWholePage(pageSize) ? null : result;
    }

    /// <summary>The visible area (current crop or whole page) as the user sees it, after the extra rotation.</summary>
    private static PageCrop Shown(PdfSize pageSize, int rotationDelta, PageCrop? current, out int turns)
    {
        if (!double.IsFinite(pageSize.Width) || !double.IsFinite(pageSize.Height) || pageSize.Width <= 0 || pageSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "The page size must be positive.");
        }

        if (rotationDelta % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDelta), rotationDelta, "Page rotation must be a multiple of 90 degrees.");
        }

        turns = PageGeometry.NormalizeRotation(rotationDelta) / 90;
        var area = current ?? new PageCrop(0, 0, pageSize.Width, pageSize.Height);
        return area.Rotate(pageSize, turns);
    }
}
