using System.ComponentModel.DataAnnotations;
using System.Globalization;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Layout;

/// <param name="PaperWidth">Paper width in points (either orientation).</param>
/// <param name="PaperHeight">Paper height in points.</param>
/// <param name="Orientation">Sheet orientation; Auto needs the fewest sheets for each page.</param>
/// <param name="Margin">Unprinted border of each sheet, in points (cut marks and labels go there).</param>
/// <param name="Overlap">How much neighbouring sheets repeat, in points (the strip to glue).</param>
/// <param name="Scale">Page size factor (1 = actual size); ignored when <see cref="FitColumns"/> is set.</param>
/// <param name="FitColumns">Scale each page to fit this many sheets across (0 = use <see cref="Scale"/>).</param>
/// <param name="FitRows">… and this many down (with <see cref="FitColumns"/>).</param>
/// <param name="CutMarks">Marks the corners of the printed area.</param>
/// <param name="Labels">Prints the sheet's position ("A1", "A2", "B1"…) under it.</param>
/// <param name="Pages">Pages to print as posters; null for all.</param>
public sealed record TileParams(
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperWidth = Paper.A4Width,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperHeight = Paper.A4Height,
    SheetOrientation Orientation = SheetOrientation.Auto,
    [property: Range(0.0, 500.0)] double Margin = Paper.TenMillimetres,
    [property: Range(0.0, 500.0)] double Overlap = Paper.TenMillimetres,
    [property: Range(0.01, 100.0)] double Scale = 1,
    [property: Range(0, 50)] int FitColumns = 0,
    [property: Range(0, 50)] int FitRows = 0,
    bool CutMarks = true,
    bool Labels = true,
    string? Pages = null);

/// <summary>"Chia ô" (layout.tile): prints large pages across several sheets with an overlap strip to glue them.</summary>
public sealed class TileOperation(ISourceRepairer repairer) : ImpositionOperation<TileParams>(repairer)
{
    public const string OperationId = "layout.tile";

    public override string Id => OperationId;

    protected override string OutputSuffix => "-tiles";

    /// <summary>The scale, sheet and grid used for one page (for the preview).</summary>
    public static (double Scale, PosterOptions Options, int Columns, int Rows) LayoutOf(PdfSize page, TileParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var paper = new PdfSize(parameters.PaperWidth, parameters.PaperHeight);
        var portrait = new PdfSize(Math.Min(paper.Width, paper.Height), Math.Max(paper.Width, paper.Height));
        var landscape = new PdfSize(portrait.Height, portrait.Width);
        PdfSize SheetFor(double scale) => parameters.Orientation switch
        {
            SheetOrientation.Portrait => portrait,
            SheetOrientation.Landscape => landscape,
            _ => PosterLayout.ChooseSheet(page, paper, parameters.Margin, parameters.Overlap, scale),
        };

        double scale;
        PdfSize sheet;
        if (parameters.FitColumns > 0 && parameters.FitRows > 0)
        {
            // Fit: the orientation that lets the page be printed largest.
            var candidates = parameters.Orientation switch
            {
                SheetOrientation.Portrait => [portrait],
                SheetOrientation.Landscape => [landscape],
                _ => new[] { portrait, landscape },
            };
            (scale, sheet) = candidates
                .Select(s => (PosterLayout.ScaleToFit(page, s, parameters.Margin, parameters.Overlap, parameters.FitColumns, parameters.FitRows), s))
                .MaxBy(x => x.Item1);
        }
        else
        {
            scale = parameters.Scale;
            sheet = SheetFor(scale);
        }

        var options = new PosterOptions(sheet, parameters.Margin, parameters.Overlap, scale);
        var (columns, rows) = PosterLayout.Grid(page, options);
        return (scale, options, columns, rows);
    }

    public override int SheetCount(IReadOnlyList<PdfSize> pageSizes, TileParams parameters)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentNullException.ThrowIfNull(parameters);
        return pageSizes.Count == 0
            ? 0
            : Selected(parameters.Pages, pageSizes.Count).Sum(i => LayoutOf(pageSizes[i], parameters) is var (_, _, columns, rows) ? columns * rows : 0);
    }

    protected override OperationValidationResult ValidateCore(TileParams parameters, DocumentInfo document)
    {
        var pages = CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));
        if ((parameters.FitColumns > 0) != (parameters.FitRows > 0))
        {
            pages = ParameterChecks.Combine(pages, OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.FitColumns), "Give both the columns and the rows to fit.")));
        }

        try
        {
            _ = LayoutOf(new PdfSize(Paper.A4Width, Paper.A4Height), parameters);
            return pages;
        }
        catch (ArgumentOutOfRangeException)
        {
            return ParameterChecks.Combine(pages, OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Overlap), "The margin and overlap leave no room to print.")));
        }
    }

    protected override void Impose(PdfDocument output, PdfDocument source, TileParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var pages = Selected(parameters.Pages, source.PageCount);
        for (var p = 0; p < pages.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(p, pages.Count, Stage));
            var form = PageForms.Import(output, source, pages[p]);
            var (scale, options, _, _) = LayoutOf(form.Size, parameters);
            var scaled = new PdfSize(form.Size.Width * scale, form.Size.Height * scale);
            foreach (var tile in PosterLayout.Tiles(form.Size, options))
            {
                var sheet = SheetPainter.AddSheet(output, options.Sheet);
                var left = tile.Target.Left - tile.Region.Left;
                var bottom = tile.Target.Bottom - tile.Region.Bottom;
                var whole = new PdfRect(left, bottom, left + scaled.Width, bottom + scaled.Height);
                SheetPainter.Draw(sheet, [new Placement(form, whole, tile.Target)]);
                if (parameters.CutMarks || parameters.Labels)
                {
                    var label = parameters.Labels ? Label(tile, pages.Count > 1 ? pages[p] + 1 : null) : null;
                    SheetPainter.DrawCutMarksAndLabel(sheet, tile.Target, parameters.CutMarks ? Math.Min(12, parameters.Margin * 0.6) : 0, label);
                }
            }
        }

        progress.Report(new OperationProgress(pages.Count, pages.Count, Stage));
    }

    /// <summary>"B3": row letter and column number, with the page number in front when several pages are printed.</summary>
    public static string Label(PosterTile tile, int? pageNumber)
    {
        var row = tile.Row < 26 ? ((char)('A' + tile.Row)).ToString() : (tile.Row + 1).ToString(CultureInfo.InvariantCulture) + "-";
        var position = row + (tile.Column + 1).ToString(CultureInfo.InvariantCulture);
        return pageNumber is { } number ? string.Create(CultureInfo.InvariantCulture, $"{number} · {position}") : position;
    }
}
