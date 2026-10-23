namespace PdfPro.Core.Geometry;

/// <summary>
/// A 2-D affine transform in PDF notation [a b c d e f]: x' = a·x + c·y + e, y' = b·x + d·y + f. Transforms compose
/// like the PDF "cm" operator: <c>first.Then(second)</c> applies <c>first</c>, then <c>second</c>.
/// </summary>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public double Determinant => (A * D) - (B * C);

    public static PdfMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static PdfMatrix Scaling(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>A rotation by <paramref name="degrees"/> counterclockwise in a y-up space (clockwise when y points down).</summary>
    public static PdfMatrix Rotation(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);

        // Exact values for quarter turns, so 90° rotations of page boxes stay free of rounding noise.
        if (degrees % 90 == 0)
        {
            sin = Math.Round(sin);
            cos = Math.Round(cos);
        }

        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>
    /// The affine transform that sends (0,0), (1,0) and (0,1) to <paramref name="origin"/>, <paramref name="xUnit"/> and
    /// <paramref name="yUnit"/>: how a mapping that is known point by point (like <see cref="PageCoordinateMapper"/>) is
    /// turned into a matrix.
    /// </summary>
    public static PdfMatrix FromPoints(PdfPoint origin, PdfPoint xUnit, PdfPoint yUnit)
        => new(xUnit.X - origin.X, xUnit.Y - origin.Y, yUnit.X - origin.X, yUnit.Y - origin.Y, origin.X, origin.Y);

    /// <summary>This transform followed by <paramref name="next"/>.</summary>
    public PdfMatrix Then(PdfMatrix next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    public PdfPoint Transform(PdfPoint point) => new((A * point.X) + (C * point.Y) + E, (B * point.X) + (D * point.Y) + F);

    /// <exception cref="InvalidOperationException">The transform flattens the plane (determinant 0).</exception>
    public PdfMatrix Invert()
    {
        var determinant = Determinant;
        if (Math.Abs(determinant) < 1e-12)
        {
            throw new InvalidOperationException("The matrix cannot be inverted.");
        }

        return new PdfMatrix(
            D / determinant,
            -B / determinant,
            -C / determinant,
            A / determinant,
            ((C * F) - (D * E)) / determinant,
            ((B * E) - (A * F)) / determinant);
    }

    /// <summary>The smallest axis-aligned rectangle holding the transformed corners of <paramref name="rect"/>.</summary>
    public PdfRect TransformBounds(PdfRect rect)
    {
        var a = Transform(new PdfPoint(rect.Left, rect.Bottom));
        var b = Transform(new PdfPoint(rect.Right, rect.Bottom));
        var c = Transform(new PdfPoint(rect.Right, rect.Top));
        var d = Transform(new PdfPoint(rect.Left, rect.Top));
        return new PdfRect(
            Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
    }
}
