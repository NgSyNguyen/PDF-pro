using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Operations.Automation;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.TestSupport;

/// <summary>Every operation, built as the command line builds them, on the tests' PDFium thread (one per process).</summary>
internal static class TestCatalog
{
    public static OperationCatalog Create(PdfiumGate gate, Core.Ocr.IOcrEngine? ocr = null)
        => StandardOperations.Create(new StandardOperationServices(
            gate,
            new Infrastructure.ExternalTools.ExternalToolRunner(NullLogger<Infrastructure.ExternalTools.ExternalToolRunner>.Instance),
            Path.Combine(Samples.RepositoryRoot, "tools"),
            ocr));
}
