using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Layout;

/// <summary>Paper sizes in points.</summary>
public static class Paper
{
    public const double A4Width = 595.28;
    public const double A4Height = 841.89;

    /// <summary>5 mm.</summary>
    public const double FiveMillimetres = 14.17;

    /// <summary>10 mm.</summary>
    public const double TenMillimetres = 28.35;
}

/// <param name="PagesPerSheet">2, 4, 6, 8, 9 or 16.</param>
/// <param name="PaperWidth">Paper width in points (either orientation).</param>
/// <param name="PaperHeight">Paper height in points.</param>
/// <param name="Orientation">Sheet orientation; Auto shows the pages largest.</param>
/// <param name="Order">Z (rows first) or N (columns first).</param>
/// <param name="Margin">Blank border of each sheet, in points.</param>
/// <param name="Gap">Space between pages, in points.</param>
/// <param name="Border">Draws a thin frame around each page.</param>
/// <param name="Pages">Pages to lay out; null for all.</param>
public sealed record NUpParams(
    [property: AllowedValues(2, 4, 6, 8, 9, 16)] int PagesPerSheet = 2,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperWidth = Paper.A4Width,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double PaperHeight = Paper.A4Height,
    SheetOrientation Orientation = SheetOrientation.Auto,
    NUpOrder Order = NUpOrder.RowsFirst,
    [property: Range(0.0, 500.0)] double Margin = Paper.FiveMillimetres,
    [property: Range(0.0, 500.0)] double Gap = Paper.FiveMillimetres,
    bool Border = false,
    string? Pages = null)
{
    internal NUpOptions Options => new(PagesPerSheet, new PdfSize(PaperWidth, PaperHeight), Orientation, Order, Margin, Gap);
}

/// <summary>"N trang trên một tờ" (layout.nup): 2–16 pages per sheet, scaled to fit, in Z or N order.</summary>
public sealed class NUpOperation(ISourceRepairer repairer) : ImpositionOperation<NUpParams>(repairer)
{
    public const string OperationId = "layout.nup";

    public override string Id => OperationId;

    protected override string OutputSuffix => "-nup";

    /// <summary>The sheet orientation and grid that will be used (for the preview).</summary>
    public static NUpGrid Grid(IReadOnlyList<PdfSize> pageSizes, NUpParams parameters)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentNullException.ThrowIfNull(parameters);
        return NUpLayout.ChooseGrid(parameters.Options, pageSizes.Count > 0 ? pageSizes[0] : new PdfSize(Paper.A4Width, Paper.A4Height));
    }

    public override int SheetCount(IReadOnlyList<PdfSize> pageSizes, NUpParams parameters)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentNullException.ThrowIfNull(parameters);
        return NUpLayout.SheetCount(Selected(parameters.Pages, Math.Max(1, pageSizes.Count)).Count, parameters.PagesPerSheet);
    }

    protected override OperationValidationResult ValidateCore(NUpParams parameters, DocumentInfo document)
    {
        var pages = CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));
        try
        {
            _ = NUpLayout.ChooseGrid(parameters.Options, new PdfSize(Paper.A4Width, Paper.A4Height));
            return pages;
        }
        catch (ArgumentOutOfRangeException)
        {
            return ParameterChecks.Combine(pages, OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Margin), "The margin and gap leave no room for the pages.")));
        }
    }

    protected override void Impose(PdfDocument output, PdfDocument source, NUpParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var pages = Selected(parameters.Pages, source.PageCount);
        var first = PageForms.Import(output, source, pages[0]);
        var grid = NUpLayout.ChooseGrid(parameters.Options, first.Size);
        var sheets = NUpLayout.SheetCount(pages.Count, grid.CellsPerSheet);
        for (var s = 0; s < sheets; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(s, sheets, Stage));
            var sheet = SheetPainter.AddSheet(output, grid.Sheet);
            var placements = new List<Placement>();
            for (var slot = 0; slot < grid.CellsPerSheet && (s * grid.CellsPerSheet) + slot < pages.Count; slot++)
            {
                var index = (s * grid.CellsPerSheet) + slot;
                var form = index == 0 ? first : PageForms.Import(output, source, pages[index]);
                placements.Add(new Placement(form, NUpLayout.Place(grid.Cell(slot), form.Size)));
            }

            SheetPainter.Draw(sheet, placements);
            if (parameters.Border)
            {
                SheetPainter.DrawBorders(sheet, placements.Select(p => p.Target));
            }
        }

        progress.Report(new OperationProgress(sheets, sheets, Stage));
    }
}
