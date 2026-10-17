using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Pdfium.Tests.TestSupport;

[assembly: AssemblyFixture(typeof(PdfiumGateFixture))]

namespace PdfPro.Pdfium.Tests.TestSupport;

/// <summary>The one real PDFium gate of the test run (PDFium may be initialised once per process).</summary>
public sealed class PdfiumGateFixture : IDisposable
{
    public PdfiumGate Gate { get; } = new(NullLogger<PdfiumGate>.Instance);

    /// <summary>Waits until every release queued before this call has run.</summary>
    public Task FlushAsync() => Gate.RunAsync(() => 0, PdfiumPriority.Low);

    public void Dispose() => Gate.Dispose();
}

/// <summary>Tests that count global native handles must not run alongside other PDFium tests.</summary>
[CollectionDefinition(nameof(SerialPdfiumCollection), DisableParallelization = true)]
public sealed class SerialPdfiumCollection;
