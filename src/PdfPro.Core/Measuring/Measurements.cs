using System.Globalization;
using PdfPro.Core.Geometry;

namespace PdfPro.Core.Measuring;

/// <summary>What is measured on a page.</summary>
public enum MeasureKind
{
    /// <summary>The straight distance between two points.</summary>
    Distance,

    /// <summary>The length of a line through several points.</summary>
    Perimeter,

    /// <summary>The area inside a polygon (and its perimeter).</summary>
    Area,
}

/// <summary>The unit lengths are given in.</summary>
public enum LengthUnit
{
    Millimetre,
    Centimetre,
    Metre,
    Inch,
    Foot,
    Point,
}

/// <summary>
/// A measurement's value: <see cref="Length"/> (the distance, the line's length, or the polygon's perimeter) and, for an
/// area, <see cref="Area"/> — both already scaled and in <see cref="Unit"/> (the area in its square).
/// </summary>
/// <param name="Angle">For a distance: the angle of the line on the page as shown, in degrees anticlockwise from the
/// right (0 to 360); null otherwise.</param>
public sealed record MeasureResult(MeasureKind Kind, double Length, double? Area, LengthUnit Unit, double? Angle);

/// <summary>Lengths and areas on a page, from its PDF points (1/72 inch), at a drawing's scale ("1 : 100").</summary>
public static class Measurements
{
    /// <summary>PDF points in one <paramref name="unit"/>.</summary>
    public static double PointsPer(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetre => 72 / 25.4,
        LengthUnit.Centimetre => 720 / 25.4,
        LengthUnit.Metre => 72000 / 25.4,
        LengthUnit.Inch => 72,
        LengthUnit.Foot => 864,
        LengthUnit.Point => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>The unit's symbol ("mm", "cm", "in"…).</summary>
    public static string Symbol(LengthUnit unit) => unit switch
    {
        LengthUnit.Millimetre => "mm",
        LengthUnit.Centimetre => "cm",
        LengthUnit.Metre => "m",
        LengthUnit.Inch => "in",
        LengthUnit.Foot => "ft",
        LengthUnit.Point => "pt",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>
    /// Measures <paramref name="points"/> (PDF space of one page) in <paramref name="unit"/>, real lengths being
    /// <paramref name="scale"/> times those on paper (100 for a drawing at 1 : 100). Null when there are too few points
    /// (2 for a distance or a line, 3 for an area).
    /// </summary>
    /// <param name="shown">Maps a point to the page as shown, for the distance's angle (null: PDF space).</param>
    public static MeasureResult? Measure(MeasureKind kind, IReadOnlyList<PdfPoint> points, LengthUnit unit, double scale = 1, Func<PdfPoint, ViewPoint>? shown = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        if (points.Count < (kind == MeasureKind.Area ? 3 : 2))
        {
            return null;
        }

        var perUnit = PointsPer(unit) / scale;
        switch (kind)
        {
            case MeasureKind.Distance:
                var (a, b) = (points[0], points[^1]);
                return new MeasureResult(kind, Length(a, b) / perUnit, null, unit, AngleOf(a, b, shown));
            case MeasureKind.Perimeter:
                return new MeasureResult(kind, PathLength(points, closed: false) / perUnit, null, unit, null);
            default:
                return new MeasureResult(kind, PathLength(points, closed: true) / perUnit, PolygonArea(points) / (perUnit * perUnit), unit, null);
        }
    }

    /// <summary>The straight distance between two points.</summary>
    public static double Length(PdfPoint a, PdfPoint b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>The length of the line through <paramref name="points"/>, back to the first when <paramref name="closed"/>.</summary>
    public static double PathLength(IReadOnlyList<PdfPoint> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Length(points[i - 1], points[i]);
        }

        return closed && points.Count > 2 ? length + Length(points[^1], points[0]) : length;
    }

    /// <summary>The area inside the polygon (the shoelace formula; a polygon crossing itself counts its loops apart).</summary>
    public static double PolygonArea(IReadOnlyList<PdfPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var twice = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (p, q) = (points[i], points[(i + 1) % points.Count]);
            twice += (p.X * q.Y) - (q.X * p.Y);
        }

        return Math.Abs(twice) / 2;
    }

    /// <summary>
    /// <paramref name="to"/> moved onto the nearest line from <paramref name="from"/> at a multiple of 45° (as Shift does
    /// while measuring), keeping its distance.
    /// </summary>
    public static PdfPoint Snap(PdfPoint from, PdfPoint to)
    {
        var length = Length(from, to);
        if (length == 0)
        {
            return to;
        }

        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        return new PdfPoint(from.X + (length * Math.Cos(snapped)), from.Y + (length * Math.Sin(snapped)));
    }

    /// <summary>"125,4 mm", "3,25 cm²" — at most two decimals, in the culture's way of writing numbers.</summary>
    public static string Format(double value, LengthUnit unit, bool squared = false, CultureInfo? culture = null)
    {
        var decimals = Math.Abs(value) >= 1000 ? "#,0.#" : "#,0.##";
        return value.ToString(decimals, culture ?? CultureInfo.CurrentCulture) + " " + Symbol(unit) + (squared ? "²" : string.Empty);
    }

    private static double AngleOf(PdfPoint a, PdfPoint b, Func<PdfPoint, ViewPoint>? shown)
    {
        double dx, dy;
        if (shown is null)
        {
            (dx, dy) = (b.X - a.X, b.Y - a.Y);
        }
        else
        {
            var (from, to) = (shown(a), shown(b));
            (dx, dy) = (to.X - from.X, from.Y - to.Y); // the screen's y grows downwards
        }

        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }
}
