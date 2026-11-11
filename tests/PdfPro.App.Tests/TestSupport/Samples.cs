namespace PdfPro.App.Tests.TestSupport;

/// <summary>Paths of the committed sample documents (samples/).</summary>
internal static class Samples
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>AES-256, fake open password "test123", owner password "chu-so-huu-456"; printing and copying forbidden.</summary>
    public static string Encrypted => System.IO.Path.Combine(RepositoryRoot, "samples", "encrypted.pdf");

    /// <summary>Three pages whose cross-reference table and trailer were cut off (PROMPTS P9: repaired).</summary>
    public static string Broken => System.IO.Path.Combine(RepositoryRoot, "samples", "broken.pdf");

    /// <summary>18 pages of JPEG scans (90 DPI) without text: tilted pages, page 12 upside down, blank sheets 4, 7, 10 and 14.</summary>
    public static string ScanVi => System.IO.Path.Combine(RepositoryRoot, "samples", "scan-vi.pdf");

    /// <summary>A four-page Vietnamese contract (P11): the old version.</summary>
    public static string ContractV1 => System.IO.Path.Combine(RepositoryRoot, "samples", "contract-v1.pdf");

    /// <summary>The contract with an appendix page inserted after page 2 and three places changed.</summary>
    public static string ContractV2 => System.IO.Path.Combine(RepositoryRoot, "samples", "contract-v2.pdf");

    /// <summary>Six A4 pages of Vietnamese text, no form, not encrypted.</summary>
    public static string Basic => System.IO.Path.Combine(RepositoryRoot, "samples", "basic-vi.pdf");

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
