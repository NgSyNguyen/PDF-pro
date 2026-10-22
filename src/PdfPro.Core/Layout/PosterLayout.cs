using PdfPro.Core.Geometry;

namespace PdfPro.Core.Layout;

/// <summary>Printing one large page on several sheets that are taped together ("chia ô", poster).</summary>
/// <param name="Sheet">Sheet size in points, oriented.</param>
/// <param name="Margin">Unprinted border of each sheet, in points.</param>
/// <param name="Overlap">How much neighbouring sheets repeat of each other, in points (glue strip).</param>
/// <param name="Scale">Page size factor (1 = actual size).</param>
public sealed record PosterOptions(PdfSize Sheet, double Margin, double Overlap, double Scale);

/// <summary>One sheet of a poster.</summary>
/// <param name="Row">0-based, from the top.</param>
/// <param name="Column">0-based, from the left.</param>
/// <param name="Region">The part of the scaled page it shows, in the scaled page's PDF space (origin bottom-left).</param>
/// <param name="Target">Where that part goes on the sheet, in the sheet's PDF space (top-left of the printable area).</param>
public sealed record PosterTile(int Row, int Column, PdfRect Region, PdfRect Target);

public static class PosterLayout
{
    /// <summary>Columns and rows of sheets for a page (sizes in points, displayed orientation).</summary>
    public static (int Columns, int Rows) Grid(PdfSize page, PosterOptions options)
    {
        var (printable, step) = Check(page, options);
        return (Count(page.Width * options.Scale, printable.Width, step.Width), Count(page.Height * options.Scale, printable.Height, step.Height));
    }

    /// <summary>The sheets in reading order (top row first, left to right).</summary>
    public static IReadOnlyList<PosterTile> Tiles(PdfSize page, PosterOptions options)
    {
        var (printable, step) = Check(page, options);
        var scaled = new PdfSize(page.Width * options.Scale, page.Height * options.Scale);
        var (columns, rows) = Grid(page, options);
        var tiles = new List<PosterTile>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = column * step.Width;
                var top = scaled.Height - (row * step.Height);
                var right = Math.Min(scaled.Width, left + printable.Width);
                var bottom = Math.Max(0, top - printable.Height);
                var region = new PdfRect(left, bottom, right, top);
                var targetTop = options.Sheet.Height - options.Margin;
                var target = new PdfRect(options.Margin, targetTop - region.Height, options.Margin + region.Width, targetTop);
                tiles.Add(new PosterTile(row, column, region, target));
            }
        }

        return tiles;
    }

    /// <summary>The largest scale at which the page fits on <paramref name="columns"/> × <paramref name="rows"/> sheets.</summary>
    public static double ScaleToFit(PdfSize page, PdfSize sheet, double margin, double overlap, int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        var (printable, step) = Check(page, new PosterOptions(sheet, margin, overlap, 1));
        var width = printable.Width + ((columns - 1) * step.Width);
        var height = printable.Height + ((rows - 1) * step.Height);
        return Math.Min(width / page.Width, height / page.Height);
    }

    /// <summary>The paper oriented to need the fewest sheets for the page (portrait on a tie).</summary>
    public static PdfSize ChooseSheet(PdfSize page, PdfSize paper, double margin, double overlap, double scale)
    {
        var portrait = new PdfSize(Math.Min(paper.Width, paper.Height), Math.Max(paper.Width, paper.Height));
        var landscape = new PdfSize(portrait.Height, portrait.Width);
        var (pc, pr) = Grid(page, new PosterOptions(portrait, margin, overlap, scale));
        var (lc, lr) = Grid(page, new PosterOptions(landscape, margin, overlap, scale));
        return lc * lr < pc * pr ? landscape : portrait;
    }

    private static int Count(double length, double printable, double step)
        => length <= printable + 1e-6 ? 1 : 1 + (int)Math.Ceiling(((length - printable) / step) - 1e-9);

    private static (PdfSize Printable, PdfSize Step) Check(PdfSize page, PosterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!(page.Width > 0 && page.Height > 0) || !(options.Sheet.Width > 0 && options.Sheet.Height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page and sheet sizes must be positive.");
        }

        if (!(options.Scale > 0) || !double.IsFinite(options.Scale))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "The scale must be positive.");
        }

        var printable = new PdfSize(options.Sheet.Width - (2 * options.Margin), options.Sheet.Height - (2 * options.Margin));
        if (options.Margin < 0 || printable.Width <= 1 || printable.Height <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Margin, "The margin leaves no printable area.");
        }

        if (options.Overlap < 0 || options.Overlap >= Math.Min(printable.Width, printable.Height) / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Overlap, "The overlap must be less than half the printable area.");
        }

        return (printable, new PdfSize(printable.Width - options.Overlap, printable.Height - options.Overlap));
    }
}
