using PdfPro.Core.Documents;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

/// <param name="BackStart">When the backs are in this document: the 1-based page where they begin (the fronts come
/// first, e.g. a stack scanned on one side, turned over, scanned again).</param>
/// <param name="BackSourceId">When the backs are in another file: its source id ("s1"…, added to the session like an
/// inserted file); null when they are in this document.</param>
/// <param name="BackPages">Pages of that file to use; null for all.</param>
/// <param name="ReverseBacks">The backs come last sheet first (the stack was turned over).</param>
public sealed record InterleaveParams(int BackStart = 0, string? BackSourceId = null, string? BackPages = null, bool ReverseBacks = false);

/// <summary>
/// "Trộn mặt trước/sau" (organize.interleave): puts fronts and backs scanned separately in reading order — front 1,
/// back 1, front 2… An edit of the page plan, so it is instant and undoable; pages without a partner go to the end.
/// </summary>
public sealed class InterleaveOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<InterleaveParams>(materializer, loader)
{
    public const string OperationId = "organize.interleave";

    public override string Id => OperationId;

    /// <summary>Where the backs begin by default in a single document: after the first half (rounded up).</summary>
    public static int DefaultBackStart(int pageCount) => ((pageCount + 1) / 2) + 1;

    protected override OperationValidationResult ValidateCore(InterleaveParams parameters, DocumentInfo document)
        => parameters.BackSourceId is null && (parameters.BackStart < 2 || parameters.BackStart > document.PageCount)
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.BackStart), $"The backs must begin between page 2 and {document.PageCount}."))
            : OperationValidationResult.Success;

    protected override OperationValidationResult ValidateForPlan(InterleaveParams parameters, PagePlan plan, PlanEditContext context)
    {
        if (parameters.BackSourceId is not { } sourceId)
        {
            return OperationValidationResult.Success;
        }

        if (sourceId == SourceIds.Blank || !context.HasSource(sourceId))
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.BackSourceId), $"Unknown source '{sourceId}'."));
        }

        return string.IsNullOrWhiteSpace(parameters.BackPages)
            ? OperationValidationResult.Success
            : CheckPages(parameters.BackPages, context.GetSourcePageCount(sourceId), nameof(parameters.BackPages));
    }

    protected override PagePlan Apply(PagePlan plan, InterleaveParams parameters, PlanEditContext context)
    {
        IReadOnlyList<PageRef> fronts;
        IReadOnlyList<PageRef> backs;
        if (parameters.BackSourceId is { } sourceId)
        {
            var count = context.GetSourcePageCount(sourceId);
            var pages = string.IsNullOrWhiteSpace(parameters.BackPages) ? [.. Enumerable.Range(0, count)] : PageRanges.Parse(parameters.BackPages, count);
            fronts = plan;
            backs = [.. pages.Select(page => new PageRef(sourceId, page, 0, null, context.Keys.NewKey()))];
        }
        else
        {
            fronts = [.. plan.Take(parameters.BackStart - 1)];
            backs = [.. plan.Skip(parameters.BackStart - 1)];
        }

        var result = InterleaveOrder.Order(fronts.Count, backs.Count, parameters.ReverseBacks)
            .Select(p => p.IsBack ? backs[p.Index] : fronts[p.Index])
            .ToArray();
        return result.Length == plan.Count && result.Select((page, i) => ReferenceEquals(page, plan[i])).All(same => same)
            ? plan
            : PagePlan.Create(result, plan.FormValues.Values);
    }
}
