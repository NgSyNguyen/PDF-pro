using PdfPro.Core.Security;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;
using PdfPro.Operations.Stamping;

namespace PdfPro.Operations.Automation;

/// <summary>The words of the built-in workflows, from the app's or the command line's resources.</summary>
/// <param name="Name">The workflow's name ("Số hóa hồ sơ").</param>
/// <param name="Watermark">The watermark of "Phát hành tài liệu".</param>
/// <param name="PageNumbers">The page number pattern of "Phát hành tài liệu", with {page} and {pages}.</param>
public sealed record WorkflowTemplateTexts(string Name, string Watermark = "", string PageNumbers = "{page}/{pages}");

/// <summary>
/// The built-in workflows (SPEC Phase 12): "Số hóa hồ sơ" (OCR → blank pages removed → compressed for eBooks →
/// PDF/A-2b), "Phát hành tài liệu" (watermark → page numbers → sanitized → protected with a password asked when it
/// runs), "Chuẩn bị in" (A4 pages → booklet).
/// </summary>
public static class WorkflowTemplates
{
    public const string Digitize = "digitize";

    public const string Publish = "publish";

    public const string Print = "print";

    public static IReadOnlyList<string> Ids { get; } = [Digitize, Publish, Print];

    /// <exception cref="ArgumentException">The id is not a built-in workflow.</exception>
    public static Workflow Create(string id, WorkflowTemplateTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        IReadOnlyList<WorkflowStep> steps = id switch
        {
            Digitize =>
            [
                WorkflowSteps.Create(OcrOperation.OperationId, new OcrParams([WorkflowRunner.DefaultLanguage])),
                WorkflowSteps.Create(RemoveBlankPagesOperation.OperationId, new BlankPagesMeasure()),
                WorkflowSteps.Create(CompressOperation.OperationId, new CompressParams(CompressionPreset.Ebook)),
                WorkflowSteps.Create(ToPdfAOperation.OperationId, new ToPdfAParams(PdfALevel.A2B)),
            ],
            Publish =>
            [
                WorkflowSteps.Create(WatermarkOperation.OperationId, new WatermarkParams(Text: texts.Watermark)),
                WorkflowSteps.Create(PageNumbersOperation.OperationId, new PageNumbersParams(texts.PageNumbers)),
                WorkflowSteps.Create(SanitizeOperation.OperationId, new SanitizeParams()),
                WorkflowSteps.Create(
                    ProtectDocumentOperation.OperationId,
                    new ProtectParams(new DocumentSecurity(null, string.Empty, EncryptionAlgorithm.Aes256, DocumentPermissions.Print | DocumentPermissions.PrintHighQuality | DocumentPermissions.Copy))),
            ],
            Print =>
            [
                WorkflowSteps.Create(ChangePageSizeOperation.OperationId, new ChangePageSizeParams("all", Paper.A4Width, Paper.A4Height)),
                WorkflowSteps.Create(BookletOperation.OperationId, new BookletParams()),
            ],
            _ => throw new ArgumentException($"'{id}' is not a built-in workflow.", nameof(id)),
        };
        return new Workflow(texts.Name, steps);
    }
}
