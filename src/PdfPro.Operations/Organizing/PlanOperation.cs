using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

/// <summary>
/// Base of the page operations (<see cref="OperationKind.PlanOnly"/>). In a document session only
/// <see cref="ApplyToPlan"/> runs: an instant, file-free edit of the page plan. Run on files (CLI, batch, workflows),
/// the input files become sources "s0", "s1"… and the edited plan is materialized into the output folder.
/// </summary>
public abstract class PlanOperation<TParams>(IPlanMaterializer materializer, IPdfDocumentLoader loader) : PdfOperation<TParams>, IPlanOperation
    where TParams : class
{
    public sealed override OperationKind Kind => OperationKind.PlanOnly;

    /// <inheritdoc cref="IPlanOperation.ChangesContent"/>
    public virtual bool ChangesContent => true;

    public PagePlan ApplyToPlan(PagePlan plan, object parameters, PlanEditContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(context);
        var validation = Validate(parameters, new DocumentInfo(plan.Count));
        if (validation.IsValid)
        {
            validation = ValidateForPlan((TParams)parameters, plan, context);
        }

        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        return Apply(plan, (TParams)parameters, context);
    }

    /// <summary>Checks that need the plan itself (page sizes, inserted sources). Default: valid.</summary>
    protected virtual OperationValidationResult ValidateForPlan(TParams parameters, PagePlan plan, PlanEditContext context)
        => OperationValidationResult.Success;

    /// <summary>The edit itself; parameters are already validated.</summary>
    protected abstract PagePlan Apply(PagePlan plan, TParams parameters, PlanEditContext context);

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        TParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var sources = await PlanFiles.LoadAsync(loader, context.InputPaths, cancellationToken).ConfigureAwait(false);
        var edited = ApplyToPlan(sources.IdentityPlan(), parameters, new PlanEditContext(sources.PageSizes, RandomPageKeys.Instance));
        var outputPath = await PlanFiles.WriteAsync(materializer, context, edited, sources, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(outputPath);
    }

    /// <summary>Validates a page range parameter against the page count.</summary>
    protected static OperationValidationResult CheckPages(string? pages, int pageCount, string memberName)
        => ParameterChecks.Pages(pages, pageCount, memberName);

    /// <summary>Validates a 1-based "insert before" position (page count + 1 = at the end).</summary>
    protected static OperationValidationResult CheckPosition(int insertBefore, int pageCount, string memberName)
        => ParameterChecks.Position(insertBefore, pageCount, memberName);

    protected static OperationValidationResult Combine(params OperationValidationResult[] results)
        => ParameterChecks.Combine(results);
}

/// <summary>Validation shared by the page operations.</summary>
internal static class ParameterChecks
{
    public static OperationValidationResult Pages(string? pages, int pageCount, string memberName)
        => PageRanges.TryParse(pages, Math.Max(pageCount, 1), out _, out var error)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(memberName, $"Invalid page range ({error.Kind}): '{error.Token}'."));

    public static OperationValidationResult Position(int insertBefore, int pageCount, string memberName)
        => insertBefore >= 1 && insertBefore <= pageCount + 1
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(memberName, $"The position must be between 1 and {pageCount + 1}."));

    public static OperationValidationResult Combine(params OperationValidationResult[] results)
    {
        var errors = results.SelectMany(r => r.Errors).ToArray();
        return errors.Length == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }
}
