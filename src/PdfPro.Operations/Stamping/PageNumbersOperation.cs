using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Organizing;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// stamp.pageNumbers: "1", "iv" or "Trang 3/10" at one place of the pages, starting on a chosen page with a chosen
/// number (earlier pages get none). In the "Header/Footer" layer, removable with stamp.remove.
/// </summary>
public sealed class PageNumbersOperation : DocumentContentOperation<PageNumbersParams>
{
    public const string OperationId = "stamp.pageNumbers";

    public override string Id => OperationId;

    public override string Stage => "Stage_PageNumbers";

    /// <summary>
    /// The text page <paramref name="pageIndex"/> gets in a document of <paramref name="pageCount"/> pages, or null when
    /// it gets no number: what the operation writes, for previews.
    /// </summary>
    public static string? TextOn(PageNumbersParams parameters, int pageIndex, int pageCount, string fileName, string? title)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Numbering(parameters, pageCount) is { } plan && plan.Pages.Contains(pageIndex) && StampTemplate.TryParse(parameters.Pattern, out var template, out _, out _)
            ? template.Render(plan.Values(pageIndex, DateTime.Now, fileName, title))
            : null;
    }

    protected override OperationValidationResult ValidateCore(PageNumbersParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            !string.IsNullOrWhiteSpace(parameters.Pattern) && StampTemplate.TryParse(parameters.Pattern, out _, out _, out _)
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Pattern), "The text is empty or has an unknown or unclosed {field}.")),
            parameters.FirstPage <= document.PageCount
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.FirstPage), $"The document has {document.PageCount} pages.")),
            StampContent.CheckStyle(parameters.TextStyle, nameof(parameters.TextStyle)),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)));

    protected override void Apply(PdfDocument document, string inputPath, PageNumbersParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (Numbering(parameters, document.PageCount) is not { } plan)
        {
            return;
        }

        var template = StampTemplate.Parse(parameters.Pattern);
        var title = string.IsNullOrWhiteSpace(document.Info.Title) ? null : document.Info.Title;
        var fileName = parameters.FileName ?? Path.GetFileNameWithoutExtension(inputPath);
        var date = DateTime.Now;
        var zone = new TextZone(parameters.Anchor, page => template.Render(plan.Values(page, date, fileName, title)));
        TextZones.Stamp(document, plan.Pages, [zone], parameters.TextStyle ?? PageNumbersParams.DefaultStyle, parameters.MarginX, parameters.MarginY, StampContent.PageNumbersLayer, progress, Stage, cancellationToken);
    }

    /// <summary>The numbered pages (from <see cref="PageNumbersParams.FirstPage"/>) and their numbers; null when none.</summary>
    private static PageNumberPlan? Numbering(PageNumbersParams parameters, int pageCount)
    {
        var first = parameters.FirstPage - 1;
        var pages = StampContent.Pages(parameters.Pages, pageCount).Where(p => p >= first).ToArray();
        return pages.Length == 0 ? null : new PageNumberPlan(pages, first, parameters.StartNumber, parameters.Style);
    }
}
