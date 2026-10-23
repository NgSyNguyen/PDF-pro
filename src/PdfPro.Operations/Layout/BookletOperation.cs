using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Layout;

/// <param name="PaperWidth">Paper width in points (either orientation; sheets are printed landscape).</param>
/// <param name="PaperHeight">Paper height in points.</param>
/// <param name="Binding">Fold on the left (read left to right) or on the right.</param>
/// <param name="Margin">Blank border of each sheet, in points.</param>
/// <param name="Gutter">Space on each side of the fold, in points.</param>
public sealed record BookletParams(
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperWidth = Paper.A4Width,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperHeight = Paper.A4Height,
    BookletBinding Binding = BookletBinding.Left,
    [property: Range(0.0, 500.0)] double Margin = Paper.FiveMillimetres,
    [property: Range(0.0, 500.0)] double Gutter = 0)
{
    /// <summary>A landscape sheet with two cells, left and right of the fold.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public NUpGrid Grid
    {
        get
        {
            var (shortSide, longSide) = (Math.Min(PaperWidth, PaperHeight), Math.Max(PaperWidth, PaperHeight));
            return new NUpGrid(new PdfSize(longSide, shortSide), 2, 1, NUpOrder.RowsFirst, Margin, Gutter * 2);
        }
    }
}

/// <summary>
/// "Sổ gập" (layout.booklet): pages ordered for saddle-stitch printing — print both sides flipping on the short edge,
/// stack, fold in the middle. Blank pages are added at the end up to a multiple of 4.
/// </summary>
public sealed class BookletOperation(ISourceRepairer repairer) : ImpositionOperation<BookletParams>(repairer)
{
    public const string OperationId = "layout.booklet";

    public override string Id => OperationId;

    protected override string OutputSuffix => "-booklet";

    /// <summary>Printed sides (two per sheet of paper).</summary>
    public override int SheetCount(IReadOnlyList<PdfSize> pageSizes, BookletParams parameters)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        return BookletLayout.PaddedPageCount(Math.Max(1, pageSizes.Count)) / 2;
    }

    protected override OperationValidationResult ValidateCore(BookletParams parameters, DocumentInfo document)
        => parameters.Grid.CellWidth > 1 && parameters.Grid.CellHeight > 1
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Margin), "The margin and gutter leave no room for the pages."));

    protected override void Impose(PdfDocument output, PdfDocument source, BookletParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var grid = parameters.Grid;
        var sides = BookletLayout.Sides(source.PageCount, parameters.Binding);
        for (var s = 0; s < sides.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(s, sides.Count, Stage));
            var sheet = SheetPainter.AddSheet(output, grid.Sheet);
            var placements = new List<Placement>(2);
            foreach (var (page, slot) in new[] { (sides[s].Left, 0), (sides[s].Right, 1) })
            {
                if (page >= 0)
                {
                    var form = PageForms.Import(output, source, page);
                    placements.Add(new Placement(form, NUpLayout.Place(grid.Cell(slot), form.Size)));
                }
            }

            SheetPainter.Draw(sheet, placements);
        }

        progress.Report(new OperationProgress(sides.Count, sides.Count, Stage));
    }
}
