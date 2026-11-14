namespace PdfPro.Pdfium.Tests.TestSupport;

/// <summary>Paths of the committed sample documents (samples/) and golden images.</summary>
internal static class Samples
{
    public static readonly string RepositoryRoot = FindRepositoryRoot();

    public static string BasicVi => Path("basic-vi.pdf");

    public static string Big1000 => Path("big-1000.pdf");

    public static string Encrypted => Path("encrypted.pdf");

    public static string Broken => Path("broken.pdf");

    public static string RotatedCropBox => Path("rotated-cropbox.pdf");

    public static string LinksAnnots => Path("links-annots.pdf");

    public static string GoldenDirectory => System.IO.Path.Combine(RepositoryRoot, "tests", "PdfPro.Pdfium.Tests", "Golden");

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
