using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Organizing;

/// <param name="Pages">Pages to copy into the new file; they keep the document's order.</param>
public sealed record ExtractPagesParams([property: Required] string Pages);

/// <summary>
/// "Trích trang ra file mới" (<see cref="OperationKind.Export"/>): writes the chosen pages, as the document shows them
/// (turned, cropped), to a new file; the document itself does not change. In a session the app materializes
/// <see cref="SelectPages"/> of the current plan; run on files, the chosen pages of the input go to the output folder.
/// </summary>
public sealed class ExtractPagesOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader) : PdfOperation<ExtractPagesParams>
{
    public const string OperationId = "organize.extract";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>The plan of the new file: the chosen pages of <paramref name="plan"/>, in document order.</summary>
    /// <exception cref="OperationValidationException">The pages do not fit the plan.</exception>
    public PagePlan SelectPages(PagePlan plan, ExtractPagesParams parameters)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(parameters);
        var validation = Validate(parameters, new DocumentInfo(plan.Count));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        return PagePlan.Create(PageRanges.Parse(parameters.Pages, plan.Count).Select(i => plan[i]));
    }

    protected override OperationValidationResult ValidateCore(ExtractPagesParams parameters, DocumentInfo document)
        => ParameterChecks.Pages(parameters.Pages, document.PageCount, nameof(parameters.Pages));

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        ExtractPagesParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        var sources = await PlanFiles.LoadAsync(loader, context.InputPaths, cancellationToken).ConfigureAwait(false);
        var pages = SelectPages(sources.IdentityPlan(), parameters);
        var outputPath = await PlanFiles.WriteAsync(materializer, context, pages, sources, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(outputPath);
    }
}
