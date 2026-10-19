using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

// Page lists use the page range syntax with 1-based numbers ("1-3, 7", "odd"); see PageRanges.
// Positions are 1-based "insert before" page numbers: page count + 1 inserts at the end.

/// <param name="Pages">Pages to turn.</param>
/// <param name="Degrees">Clockwise; negative turns counter-clockwise.</param>
public sealed record RotatePagesParams(
    [property: Required] string Pages,
    [property: AllowedValues(90, 180, 270, -90, -180, -270)] int Degrees);

/// <param name="Pages">Pages to move; they keep their relative order.</param>
/// <param name="InsertBefore">Page number the moved pages go before.</param>
public sealed record MovePagesParams(
    [property: Required] string Pages,
    [property: Range(1, int.MaxValue)] int InsertBefore);

public sealed record DeletePagesParams([property: Required] string Pages);

/// <param name="Pages">Pages to copy; the copies follow the last of them as one block.</param>
public sealed record DuplicatePagesParams([property: Required] string Pages);

/// <param name="InsertBefore">Page number the blank pages go before.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
/// <param name="Count">How many blank pages.</param>
public sealed record InsertBlankPagesParams(
    [property: Range(1, int.MaxValue)] int InsertBefore,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double Width,
    [property: Range(PageLimits.MinimumSide, PageLimits.MaximumSide)] double Height,
    [property: Range(1, 1000)] int Count = 1);

/// <param name="SourceId">The file to take pages from: "s1", "s2"… (session sources, or the extra input files).</param>
/// <param name="SourcePages">Pages of that file, or null for all of them.</param>
/// <param name="InsertBefore">Page number the inserted pages go before.</param>
public sealed record InsertPagesParams(
    [property: Required] string SourceId,
    string? SourcePages,
    [property: Range(1, int.MaxValue)] int InsertBefore);

/// <param name="Pages">Pages to crop.</param>
/// <param name="Left">Margin cut on the left of what is displayed, in points.</param>
/// <param name="Top">Margin cut at the top, in points.</param>
/// <param name="Right">Margin cut on the right, in points.</param>
/// <param name="Bottom">Margin cut at the bottom, in points.</param>
/// <param name="Reset">Removes the pages' crop instead (the margins are ignored).</param>
public sealed record CropPagesParams(
    [property: Required] string Pages,
    [property: Range(0, PageLimits.MaximumSide)] double Left,
    [property: Range(0, PageLimits.MaximumSide)] double Top,
    [property: Range(0, PageLimits.MaximumSide)] double Right,
    [property: Range(0, PageLimits.MaximumSide)] double Bottom,
    bool Reset = false);

/// <summary>Page size limits in points (PDF allows 3–14,400; tiny pages are not useful).</summary>
public static class PageLimits
{
    public const double MinimumSide = PageCrop.MinimumSide;
    public const double MaximumSide = 14_400;
}

public sealed class RotatePagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<RotatePagesParams>(materializer, loader)
{
    public const string OperationId = "organize.rotate";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(RotatePagesParams parameters, DocumentInfo document)
        => CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));

    protected override PagePlan Apply(PagePlan plan, RotatePagesParams parameters, PlanEditContext context)
        => plan.Rotate(PageRanges.Parse(parameters.Pages, plan.Count), parameters.Degrees);
}

public sealed class MovePagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<MovePagesParams>(materializer, loader)
{
    public const string OperationId = "organize.move";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(MovePagesParams parameters, DocumentInfo document)
        => Combine(
            CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages)),
            CheckPosition(parameters.InsertBefore, document.PageCount, nameof(parameters.InsertBefore)));

    protected override PagePlan Apply(PagePlan plan, MovePagesParams parameters, PlanEditContext context)
        => plan.Move(PageRanges.Parse(parameters.Pages, plan.Count), parameters.InsertBefore - 1);
}

public sealed class DeletePagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<DeletePagesParams>(materializer, loader)
{
    public const string OperationId = "organize.delete";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(DeletePagesParams parameters, DocumentInfo document)
    {
        var pages = CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));
        if (!pages.IsValid)
        {
            return pages;
        }

        return PageRanges.Parse(parameters.Pages, document.PageCount).Count < document.PageCount
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pages), "A document must keep at least one page."));
    }

    protected override PagePlan Apply(PagePlan plan, DeletePagesParams parameters, PlanEditContext context)
        => plan.Delete(PageRanges.Parse(parameters.Pages, plan.Count));
}

public sealed class DuplicatePagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<DuplicatePagesParams>(materializer, loader)
{
    public const string OperationId = "organize.duplicate";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(DuplicatePagesParams parameters, DocumentInfo document)
        => CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));

    protected override PagePlan Apply(PagePlan plan, DuplicatePagesParams parameters, PlanEditContext context)
        => plan.Duplicate(PageRanges.Parse(parameters.Pages, plan.Count), context.Keys);
}

public sealed class InsertBlankPagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<InsertBlankPagesParams>(materializer, loader)
{
    public const string OperationId = "organize.insertBlank";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(InsertBlankPagesParams parameters, DocumentInfo document)
        => CheckPosition(parameters.InsertBefore, document.PageCount, nameof(parameters.InsertBefore));

    protected override PagePlan Apply(PagePlan plan, InsertBlankPagesParams parameters, PlanEditContext context)
        => plan.InsertBlank(parameters.InsertBefore - 1, new PdfSize(parameters.Width, parameters.Height), context.Keys, parameters.Count);
}

public sealed class InsertPagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<InsertPagesParams>(materializer, loader)
{
    public const string OperationId = "organize.insertPages";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(InsertPagesParams parameters, DocumentInfo document)
        => Combine(
            SourceIds.IsValid(parameters.SourceId) && parameters.SourceId != SourceIds.Blank
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.SourceId), "Unknown source id.")),
            CheckPosition(parameters.InsertBefore, document.PageCount, nameof(parameters.InsertBefore)));

    protected override OperationValidationResult ValidateForPlan(InsertPagesParams parameters, PagePlan plan, PlanEditContext context)
    {
        if (!context.HasSource(parameters.SourceId))
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.SourceId), $"Source '{parameters.SourceId}' is not available."));
        }

        return parameters.SourcePages is null
            ? OperationValidationResult.Success
            : CheckPages(parameters.SourcePages, context.GetSourcePageCount(parameters.SourceId), nameof(parameters.SourcePages));
    }

    protected override PagePlan Apply(PagePlan plan, InsertPagesParams parameters, PlanEditContext context)
    {
        var pageCount = context.GetSourcePageCount(parameters.SourceId);
        var pages = parameters.SourcePages is null ? Enumerable.Range(0, pageCount).ToArray() : PageRanges.Parse(parameters.SourcePages, pageCount);
        return plan.InsertFromSource(parameters.InsertBefore - 1, parameters.SourceId, pages, context.Keys);
    }
}

public sealed class CropPagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<CropPagesParams>(materializer, loader)
{
    public const string OperationId = "organize.crop";

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(CropPagesParams parameters, DocumentInfo document)
        => CheckPages(parameters.Pages, document.PageCount, nameof(parameters.Pages));

    protected override OperationValidationResult ValidateForPlan(CropPagesParams parameters, PagePlan plan, PlanEditContext context)
    {
        if (parameters.Reset)
        {
            return OperationValidationResult.Success;
        }

        var margins = Margins(parameters);
        foreach (var index in PageRanges.Parse(parameters.Pages, plan.Count))
        {
            var page = plan[index];
            if (!PageCrop.Fits(context.GetBaseSize(page), page.RotationDelta, page.CropBoxOverride, margins))
            {
                return OperationValidationResult.Failed(new OperationValidationError(
                    nameof(parameters.Pages),
                    $"The margins leave less than {PageCrop.MinimumSide} points of page {index + 1}."));
            }
        }

        return OperationValidationResult.Success;
    }

    protected override PagePlan Apply(PagePlan plan, CropPagesParams parameters, PlanEditContext context)
    {
        var indices = PageRanges.Parse(parameters.Pages, plan.Count);
        if (parameters.Reset)
        {
            return plan.Crop(indices, crop: null);
        }

        var margins = Margins(parameters);
        return plan.Crop(indices.ToDictionary(
            i => i,
            i => PageCrop.Apply(context.GetBaseSize(plan[i]), plan[i].RotationDelta, plan[i].CropBoxOverride, margins)));
    }

    private static PageMargins Margins(CropPagesParams parameters)
        => new(parameters.Left, parameters.Top, parameters.Right, parameters.Bottom);
}
