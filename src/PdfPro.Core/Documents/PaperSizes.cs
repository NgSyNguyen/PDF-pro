using PdfPro.Core.Geometry;

namespace PdfPro.Core.Documents;

/// <summary>A standard paper format recognised from a page size.</summary>
/// <param name="Name">ISO or US name ("A4", "Letter"…), the same in every language.</param>
/// <param name="IsLandscape">The page is wider than tall.</param>
public sealed record PaperSize(string Name, bool IsLandscape);

/// <summary>Recognises common paper formats (ISO A/B, US) from a page size in points, in either orientation.</summary>
public static class PaperSizes
{
    public const double MillimetresPerPoint = 25.4 / 72.0;

    /// <summary>Allowed difference per side, in points (≈ 1 mm), for rounding in PDF producers.</summary>
    public const double Tolerance = 3.0;

    private static readonly (string Name, double Width, double Height)[] Known =
    [
        ("A0", 2384, 3370),
        ("A1", 1684, 2384),
        ("A2", 1191, 1684),
        ("A3", 842, 1191),
        ("A4", 595, 842),
        ("A5", 420, 595),
        ("A6", 298, 420),
        ("B4", 709, 1001),
        ("B5", 499, 709),
        ("Letter", 612, 792),
        ("Legal", 612, 1008),
        ("Tabloid", 792, 1224),
        ("Executive", 522, 756),
    ];

    /// <summary>The matching format, or null for a custom size.</summary>
    public static PaperSize? Identify(PdfSize size)
    {
        var shortSide = Math.Min(size.Width, size.Height);
        var longSide = Math.Max(size.Width, size.Height);
        foreach (var (name, width, height) in Known)
        {
            if (Math.Abs(shortSide - width) <= Tolerance && Math.Abs(longSide - height) <= Tolerance)
            {
                return new PaperSize(name, size.Width > size.Height);
            }
        }

        return null;
    }

    public static double ToMillimetres(double points) => points * MillimetresPerPoint;
}
