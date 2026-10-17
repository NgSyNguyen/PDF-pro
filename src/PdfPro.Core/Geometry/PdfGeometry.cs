namespace PdfPro.Core.Geometry;

/// <summary>A point in PDF user space: points (1/72 inch), origin bottom-left, y up.</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>A size in points.</summary>
public readonly record struct PdfSize(double Width, double Height)
{
    public bool IsLandscape => Width > Height;
}

/// <summary>
/// An axis-aligned rectangle in PDF user space (y up). Always normalized: <see cref="Left"/> ≤ <see cref="Right"/>
/// and <see cref="Bottom"/> ≤ <see cref="Top"/>, whatever order the corners were given in.
/// </summary>
public readonly record struct PdfRect
{
    public PdfRect(double left, double bottom, double right, double top)
    {
        Left = Math.Min(left, right);
        Right = Math.Max(left, right);
        Bottom = Math.Min(bottom, top);
        Top = Math.Max(bottom, top);
    }

    public double Left { get; }

    public double Bottom { get; }

    public double Right { get; }

    public double Top { get; }

    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PdfSize Size => new(Width, Height);

    public bool Contains(PdfPoint point)
        => point.X >= Left && point.X <= Right && point.Y >= Bottom && point.Y <= Top;
}

/// <summary>Converts page sizes to whole device pixels; renderer and viewer must agree on this rounding.</summary>
public static class PixelSize
{
    /// <summary>Pixels for <paramref name="points"/> at <paramref name="scale"/> pixels per point (at least 1).</summary>
    public static int FromPoints(double points, double scale)
        => Math.Max(1, (int)Math.Round(points * scale, MidpointRounding.AwayFromZero));
}

/// <summary>A rectangle of device pixels (origin top-left, y down).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public bool Contains(PixelRect other)
        => other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>The overlapping part of two rectangles, or an empty rectangle.</summary>
    public static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : default;
    }
}

/// <summary>
/// What a viewer needs to place a page: the visible box (CropBox ∩ MediaBox, as PDFium displays it)
/// and the page's /Rotate value normalized to 0, 90, 180 or 270 degrees clockwise.
/// </summary>
public readonly record struct PageGeometry
{
    public PageGeometry(PdfRect box, int rotationDegrees)
    {
        if (box.IsEmpty)
        {
            throw new ArgumentException("The page box must have a positive width and height.", nameof(box));
        }

        Box = box;
        RotationDegrees = NormalizeRotation(rotationDegrees);
    }

    public PdfRect Box { get; }

    public int RotationDegrees { get; }

    /// <summary>Size as displayed, i.e. width and height swapped for 90/270 degree rotations.</summary>
    public PdfSize DisplaySize => RotationDegrees is 90 or 270 ? new(Box.Height, Box.Width) : Box.Size;

    /// <summary>Normalizes any multiple of 90 (including negative values) to 0, 90, 180 or 270.</summary>
    public static int NormalizeRotation(int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Page rotation must be a multiple of 90 degrees.");
        }

        return ((degrees % 360) + 360) % 360;
    }
}
