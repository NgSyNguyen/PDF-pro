namespace PdfPro.Core.Geometry;

/// <summary>
/// A point in view space: screen units (WPF DIP in the app), origin top-left, y down. Depending on context it is
/// relative to a displayed page, to the whole document layout or to the viewport.
/// </summary>
public readonly record struct ViewPoint(double X, double Y)
{
    public static ViewPoint operator +(ViewPoint a, ViewPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static ViewPoint operator -(ViewPoint a, ViewPoint b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>A size in view units.</summary>
public readonly record struct ViewSize(double Width, double Height);

/// <summary>An axis-aligned rectangle in view space (y down). Width and height are never negative.</summary>
public readonly record struct ViewRect
{
    public ViewRect(double x, double y, double width, double height)
    {
        X = width < 0 ? x + width : x;
        Y = height < 0 ? y + height : y;
        Width = Math.Abs(width);
        Height = Math.Abs(height);
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public ViewPoint TopLeft => new(X, Y);

    public ViewSize Size => new(Width, Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ViewRect FromCorners(ViewPoint a, ViewPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public bool Contains(ViewPoint point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;

    public bool IntersectsWith(ViewRect other)
        => other.X < Right && other.Right > X && other.Y < Bottom && other.Bottom > Y;

    /// <summary>The overlapping part of two rectangles, or an empty rectangle.</summary>
    public static ViewRect Intersect(ViewRect a, ViewRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new ViewRect(left, top, right - left, bottom - top) : default;
    }

    public ViewRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
}
