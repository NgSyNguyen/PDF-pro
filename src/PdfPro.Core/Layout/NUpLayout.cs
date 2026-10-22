using PdfPro.Core.Geometry;

namespace PdfPro.Core.Layout;

/// <summary>Order in which pages fill the cells of a sheet.</summary>
public enum NUpOrder
{
    /// <summary>"Z": left to right, then the next row down.</summary>
    RowsFirst,

    /// <summary>"N": top to bottom, then the next column to the right.</summary>
    ColumnsFirst,
}

public enum SheetOrientation
{
    /// <summary>The orientation that shows the pages largest.</summary>
    Auto,
    Portrait,
    Landscape,
}

/// <summary>How pages are put on sheets.</summary>
/// <param name="PagesPerSheet">2, 4, 6, 8, 9 or 16.</param>
/// <param name="Paper">The paper size (either orientation; <see cref="Orientation"/> decides).</param>
/// <param name="Margin">Blank border of the sheet, in points.</param>
/// <param name="Gap">Space between cells, in points.</param>
public sealed record NUpOptions(int PagesPerSheet, PdfSize Paper, SheetOrientation Orientation, NUpOrder Order, double Margin, double Gap);

/// <summary>The chosen sheet and its grid of cells.</summary>
/// <param name="Sheet">Sheet size in points, oriented.</param>
public sealed record NUpGrid(PdfSize Sheet, int Columns, int Rows, NUpOrder Order, double Margin, double Gap)
{
    public int CellsPerSheet => Columns * Rows;

    public double CellWidth => (Sheet.Width - (2 * Margin) - ((Columns - 1) * Gap)) / Columns;

    public double CellHeight => (Sheet.Height - (2 * Margin) - ((Rows - 1) * Gap)) / Rows;

    /// <summary>The cell of the <paramref name="slot"/>-th page of a sheet, in PDF space (origin bottom-left).</summary>
    public PdfRect Cell(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, CellsPerSheet);
        var (column, row) = Order == NUpOrder.RowsFirst ? (slot % Columns, slot / Columns) : (slot / Rows, slot % Rows);
        var left = Margin + (column * (CellWidth + Gap));
        var top = Sheet.Height - Margin - (row * (CellHeight + Gap));
        return new PdfRect(left, top - CellHeight, left + CellWidth, top);
    }
}

/// <summary>Several pages on one sheet ("N-up", booklets): grid choice and page placement. All sizes in points.</summary>
public static class NUpLayout
{
    public static IReadOnlyList<int> SupportedCounts { get; } = [2, 4, 6, 8, 9, 16];

    /// <summary>
    /// The sheet orientation and grid for <paramref name="options"/>. The grid's longer side follows the sheet's longer
    /// side (6 per sheet: 3 × 2 on landscape, 2 × 3 on portrait); <see cref="SheetOrientation.Auto"/> picks the
    /// orientation that shows <paramref name="typicalPage"/> largest, portrait on a tie.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Unsupported count, or margins and gaps leave no room for the cells.</exception>
    public static NUpGrid ChooseGrid(NUpOptions options, PdfSize typicalPage)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (longSide, shortSide) = options.PagesPerSheet switch
        {
            2 => (2, 1),
            4 => (2, 2),
            6 => (3, 2),
            8 => (4, 2),
            9 => (3, 3),
            16 => (4, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.PagesPerSheet, "Pages per sheet must be 2, 4, 6, 8, 9 or 16."),
        };
        CheckPositive(options.Paper, nameof(options));
        CheckPositive(typicalPage, nameof(typicalPage));
        if (options.Margin < 0 || options.Gap < 0 || !double.IsFinite(options.Margin) || !double.IsFinite(options.Gap))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Margin and gap must not be negative.");
        }

        var shortPaper = Math.Min(options.Paper.Width, options.Paper.Height);
        var longPaper = Math.Max(options.Paper.Width, options.Paper.Height);
        var portrait = new NUpGrid(new PdfSize(shortPaper, longPaper), shortSide, longSide, options.Order, options.Margin, options.Gap);
        var landscape = new NUpGrid(new PdfSize(longPaper, shortPaper), longSide, shortSide, options.Order, options.Margin, options.Gap);
        var grid = options.Orientation switch
        {
            SheetOrientation.Portrait => portrait,
            SheetOrientation.Landscape => landscape,
            _ => FitScale(landscape, typicalPage) > FitScale(portrait, typicalPage) + 1e-9 ? landscape : portrait,
        };
        if (grid.CellWidth <= 1 || grid.CellHeight <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The margin and gap leave no room for the pages.");
        }

        return grid;
    }

    /// <summary>Sheets needed for <paramref name="pageCount"/> pages.</summary>
    public static int SheetCount(int pageCount, int pagesPerSheet)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerSheet, 1);
        return (pageCount + pagesPerSheet - 1) / pagesPerSheet;
    }

    /// <summary>Where a page of <paramref name="page"/> size goes in <paramref name="cell"/>: as large as fits, centred.</summary>
    public static PdfRect Place(PdfRect cell, PdfSize page)
    {
        CheckPositive(page, nameof(page));
        var scale = Math.Min(cell.Width / page.Width, cell.Height / page.Height);
        var width = page.Width * scale;
        var height = page.Height * scale;
        var left = cell.Left + ((cell.Width - width) / 2);
        var bottom = cell.Bottom + ((cell.Height - height) / 2);
        return new PdfRect(left, bottom, left + width, bottom + height);
    }

    private static double FitScale(NUpGrid grid, PdfSize page) => Math.Min(grid.CellWidth / page.Width, grid.CellHeight / page.Height);

    private static void CheckPositive(PdfSize size, string parameterName)
    {
        if (!(size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)))
        {
            throw new ArgumentOutOfRangeException(parameterName, size, "Sizes must be positive.");
        }
    }
}
