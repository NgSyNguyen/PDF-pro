using PdfPro.Core.ExternalTools;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Editing;
using PdfPro.Operations.Extracting;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Forms;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Merging;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Security;
using PdfPro.Operations.Signing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Structure;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Automation;

/// <summary>What the operations need from outside: PDFium, external tools and, when installed, an OCR engine.</summary>
/// <param name="Gate">The PDFium thread.</param>
/// <param name="Tools">Runs qpdf, LibreOffice…</param>
/// <param name="ToolsDirectory">Where qpdf is looked for (tools\qpdf), or null for PATH only.</param>
/// <param name="Ocr">The OCR engine, or null: ocr.run then says OCR is not installed.</param>
/// <param name="LibreOffice">Finds LibreOffice, or null for the usual places.</param>
/// <param name="Timestamps">The time-stamping client of signatures, or null for none.</param>
/// <param name="Fonts">The font library (fonts installed on the computer for PDF/A), or null for the bundled fonts only.</param>
/// <param name="Revocation">Downloads revocation lists for long-term validation of signatures, or null: no LTV.</param>
/// <param name="Office">Converts office documents with the office applications installed, or null: LibreOffice only.</param>
public sealed record StandardOperationServices(
    PdfiumGate Gate,
    IExternalToolRunner Tools,
    string? ToolsDirectory = null,
    IOcrEngine? Ocr = null,
    ILibreOfficeLocator? LibreOffice = null,
    ITimestampClient? Timestamps = null,
    FontLibrary? Fonts = null,
    IRevocationFetcher? Revocation = null,
    IOfficeAutomation? Office = null);

/// <summary>
/// Every operation of PDF_Pro built without the app (the command line, batch runs, tests): the same classes the app
/// registers through dependency injection, with the same services.
/// </summary>
public static class StandardOperations
{
    public static OperationCatalog Create(StandardOperationServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var gate = services.Gate;
        var loader = new PdfDocumentLoader(gate);
        var repairer = new PdfiumSourceRepairer(gate);
        var materializer = new PdfSharpPlanMaterializer(repairer);
        var qpdf = new Qpdf(services.Tools, services.ToolsDirectory is { } tools ? QpdfOptions.In(tools) : new QpdfOptions("qpdf"));
        var ocr = services.Ocr ?? new MissingOcrEngine();
        return new OperationCatalog(
        [
            new RotatePagesOperation(materializer, loader),
            new MovePagesOperation(materializer, loader),
            new DeletePagesOperation(materializer, loader),
            new DuplicatePagesOperation(materializer, loader),
            new InsertBlankPagesOperation(materializer, loader),
            new InsertPagesOperation(materializer, loader),
            new CropPagesOperation(materializer, loader),
            new ExtractPagesOperation(materializer, loader),
            new ChangePageSizeOperation(),
            new MarkSplitOperation(materializer, loader),
            new SetSplitMarksOperation(materializer, loader),
            new InterleaveOperation(materializer, loader),
            new SplitOperation(materializer, loader),
            new MergeOperation(materializer, loader),
            new NUpOperation(repairer),
            new BookletOperation(repairer),
            new TileOperation(repairer),
            new OverlayOperation(),
            new WatermarkOperation(),
            new BackgroundOperation(),
            new HeaderFooterOperation(),
            new PageNumbersOperation(),
            new BatesOperation(),
            new RemoveStampsOperation(),
            new AddAnnotationsOperation(materializer, loader),
            new UpdateAnnotationsOperation(materializer, loader),
            new DeleteAnnotationsOperation(materializer, loader),
            new EditFileAnnotationsOperation(),
            new FlattenAnnotationsOperation(),
            new EditContentOperation(gate),
            new FillFormOperation(materializer, loader),
            new DesignFormOperation(),
            new MergeFormDataOperation(),
            new FlattenFormOperation(),
            new ResetFormOperation(materializer, loader),
            new ProtectDocumentOperation(),
            new UnprotectDocumentOperation(),
            new SetProtectionOperation(materializer, loader),
            new RedactOperation(gate),
            new SanitizeOperation(gate),
            new SignOperation(services.Timestamps, services.Revocation),
            new LinearizeOperation(qpdf),
            new RepairOperation(gate, qpdf),
            new CompressOperation(qpdf),
            new GrayscaleOperation(gate),
            new ToImagesOperation(gate),
            new FromImagesOperation(),
            new ToTextOperation(),
            new OfficeToPdfOperation(services.Tools, services.LibreOffice ?? new LibreOfficeLocator(), services.Office),
            new ToPdfAOperation(gate, services.Fonts),
            new OcrOperation(gate, ocr),
            new RemoveBlankPagesOperation(materializer, loader, services.Gate),
            new AutoRotateOperation(materializer, loader, services.Gate, services.Ocr),
            new DeskewOperation(services.Gate),
            new CleanBordersOperation(services.Gate),
            new ExtractTablesOperation(),
            new ExtractImagesOperation(gate),
            new EditAttachmentsOperation(),
            new SetBookmarksOperation(),
            new SetMetadataOperation(),
            new SetPageLabelsOperation(),
            new EditLinksOperation(),
        ]);
    }

    /// <summary>The engine when none is installed: OCR says so.</summary>
    private sealed class MissingOcrEngine : IOcrEngine
    {
        public OcrEngineInfo? Describe() => null;

        public Task<OcrPageResult> RecognizeAsync(OcrImage image, OcrOptions options, CancellationToken cancellationToken)
            => throw new OcrException(OcrFailure.NotInstalled);

        public Task<OcrOrientation?> DetectOrientationAsync(OcrImage image, CancellationToken cancellationToken)
            => throw new OcrException(OcrFailure.NotInstalled);
    }
}
