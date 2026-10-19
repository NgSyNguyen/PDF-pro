using PdfPro.Pdfium;

namespace PdfPro.Operations.Materializing;

/// <summary>Writes a clean copy of a PDF that PDFsharp cannot parse (seam for tests).</summary>
public interface ISourceRepairer
{
    /// <summary>Returns the path of a repaired copy created in <paramref name="workingDirectory"/>.</summary>
    /// <exception cref="PdfDocumentException">PDFium cannot open the file either, or it needs a password.</exception>
    Task<string> RepairAsync(string sourcePath, string workingDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// PDFium tolerates damaged files (missing or broken cross-reference tables, wrong offsets) far better than PDFsharp;
/// its full save writes a well-formed file that PDFsharp can then read.
/// </summary>
public sealed class PdfiumSourceRepairer(PdfiumGate gate) : ISourceRepairer
{
    public async Task<string> RepairAsync(string sourcePath, string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        var target = Path.Combine(workingDirectory, $"repaired-{Guid.NewGuid():N}.pdf");
        try
        {
            await gate.RunAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(gate, sourcePath);
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    document.SaveCopy(output);
                },
                PdfiumPriority.Normal,
                cancellationToken).ConfigureAwait(false);
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left in the working directory, which is temporary.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }
}
