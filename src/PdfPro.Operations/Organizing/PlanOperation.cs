using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
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
        if (context.InputPaths.Count == 0)
        {
            throw new ArgumentException("A page operation needs at least one input file.", nameof(context));
        }

        var sizes = new Dictionary<string, IReadOnlyList<PdfSize>>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < context.InputPaths.Count; i++)
        {
            var id = SourceIds.Next('s', paths.Keys);
            var document = await loader.LoadAsync(context.InputPaths[i], password: null, cancellationToken).ConfigureAwait(false);
            await using (document.ConfigureAwait(false))
            {
                sizes[id] = document.PageSizes;
            }

            paths[id] = Path.GetFullPath(context.InputPaths[i]);
        }

        var plan = PagePlan.Identity(SourceIds.Original, sizes[SourceIds.Original].Count, RandomPageKeys.Instance);
        var edited = ApplyToPlan(plan, parameters, new PlanEditContext(sizes, RandomPageKeys.Instance));

        var outputPath = Path.GetFullPath(Path.Combine(context.OutputDirectory, Path.GetFileName(context.InputPaths[0])));
        if (paths.Values.Any(p => string.Equals(p, outputPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The output would overwrite an input file; choose another output folder.");
        }

        Directory.CreateDirectory(context.OutputDirectory);
        try
        {
            var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
            {
                var request = new MaterializeRequest
                {
                    Plan = edited,
                    BaseSourceId = SourceIds.Original,
                    SourcePaths = paths,
                    WorkingDirectory = context.WorkingDirectory,
                };
                await materializer.MaterializeAsync(request, output, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }

        return OperationResult.Success(outputPath);
    }

    /// <summary>Validates a page range parameter against the page count.</summary>
    protected static OperationValidationResult CheckPages(string? pages, int pageCount, string memberName)
        => PageRanges.TryParse(pages, Math.Max(pageCount, 1), out _, out var error)
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(memberName, $"Invalid page range ({error.Kind}): '{error.Token}'."));

    /// <summary>Validates a 1-based "insert before" position (page count + 1 = at the end).</summary>
    protected static OperationValidationResult CheckPosition(int insertBefore, int pageCount, string memberName)
        => insertBefore >= 1 && insertBefore <= pageCount + 1
            ? OperationValidationResult.Success
            : OperationValidationResult.Failed(new OperationValidationError(memberName, $"The position must be between 1 and {pageCount + 1}."));

    protected static OperationValidationResult Combine(params OperationValidationResult[] results)
    {
        var errors = results.SelectMany(r => r.Errors).ToArray();
        return errors.Length == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }
}
