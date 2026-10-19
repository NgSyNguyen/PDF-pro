using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.Operations;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

[assembly: AssemblyFixture(typeof(PdfiumFixture))]

namespace PdfPro.Operations.Tests.TestSupport;

/// <summary>The one PDFium gate of the test run (PDFium may be initialised once per process).</summary>
public sealed class PdfiumFixture : IDisposable
{
    public PdfiumGate Gate { get; } = new(NullLogger<PdfiumGate>.Instance);

    public PdfDocumentLoader Loader => new(Gate);

    public void Dispose() => Gate.Dispose();
}

/// <summary>Paths of the committed sample documents (samples/).</summary>
internal static class Samples
{
    public static readonly string RepositoryRoot = FindRepositoryRoot();

    public static string BasicVi => Path("basic-vi.pdf");

    public static string Big1000 => Path("big-1000.pdf");

    public static string Encrypted => Path("encrypted.pdf");

    public static string Broken => Path("broken.pdf");

    public static string RotatedCropBox => Path("rotated-cropbox.pdf");

    public static string LinksAnnots => Path("links-annots.pdf");

    private static string Path(string fileName)
    {
        var path = System.IO.Path.Combine(RepositoryRoot, "samples", fileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Sample {fileName} is missing; run tests/PdfPro.SampleGenerator.", path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PdfPro.slnx not found above the test folder.");
    }
}

/// <summary>Unique temporary folder deleted when the test finishes.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PdfPro.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A handle may still be closing; the OS temp cleanup removes leftovers.
        }
    }
}

/// <summary>Collects progress reports synchronously (Progress&lt;T&gt; would post them to the thread pool).</summary>
internal sealed class RecordingProgress : IProgress<OperationProgress>
{
    private readonly List<OperationProgress> _reports = [];

    public IReadOnlyList<OperationProgress> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(OperationProgress value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }
    }
}
