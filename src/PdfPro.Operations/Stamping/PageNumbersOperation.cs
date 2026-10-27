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
        var first = parameters.FirstPage - 1;
        var pages = StampContent.Pages(parameters.Pages, document.PageCount).Where(p => p >= first).ToArray();
        if (pages.Length == 0)
        {
            return;
        }

        var template = StampTemplate.Parse(parameters.Pattern);
        var last = PageNumbering.Format(PageNumbering.NumberOf(pages[^1], first, parameters.StartNumber)!.Value, parameters.Style);
        var title = string.IsNullOrWhiteSpace(document.Info.Title) ? null : document.Info.Title;
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        var zone = new TextZone(
            parameters.Anchor,
            page => template.Render(new StampValues(PageNumbering.Format(PageNumbering.NumberOf(page, first, parameters.StartNumber)!.Value, parameters.Style), last, DateTime.Now, fileName, title)));
        TextZones.Stamp(document, pages, [zone], parameters.TextStyle ?? PageNumbersParams.DefaultStyle, parameters.MarginX, parameters.MarginY, StampContent.PageNumbersLayer, progress, Stage, cancellationToken);
    }
}
