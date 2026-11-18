using PdfPro.Core.Ocr;
using PdfPro.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Pdfium;

namespace PdfPro.Cli;

/// <summary>Exit codes of pdfpro (docs/CLI.md).</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>An operation failed (on some or all files).</summary>
    public const int Failed = 1;

    /// <summary>The command line, the parameters or the workflow are wrong.</summary>
    public const int Usage = 2;

    /// <summary>An input file does not exist or cannot be read.</summary>
    public const int Input = 3;

    /// <summary>A protected file's password is missing or wrong.</summary>
    public const int Password = 4;

    /// <summary>A program the operation needs (Tesseract, LibreOffice, qpdf) is not installed.</summary>
    public const int ToolMissing = 5;

    /// <summary>The result could not be written.</summary>
    public const int Output = 6;

    /// <summary>Something unexpected went wrong (a bug).</summary>
    public const int Internal = 70;

    /// <summary>Stopped with Ctrl+C.</summary>
    public const int Cancelled = 130;
}

/// <summary>What the commands work with: the operations, PDFium, the console and the process's environment.</summary>
/// <param name="Catalog">Every operation.</param>
/// <param name="Gate">The PDFium thread.</param>
/// <param name="Ocr">The OCR engine, or null when none is installed.</param>
/// <param name="Output">Results (stdout).</param>
/// <param name="Error">Progress and errors (stderr).</param>
/// <param name="CurrentDirectory">What relative paths are relative to.</param>
/// <param name="Variables">Reads an environment variable (passwords can be given that way).</param>
/// <param name="WorkingFolder">Where intermediate files go (removed after).</param>
public sealed record CliEnvironment(
    OperationCatalog Catalog,
    PdfiumGate Gate,
    IOcrEngine? Ocr,
    TextWriter Output,
    TextWriter Error,
    string CurrentDirectory,
    Func<string, string?> Variables,
    string WorkingFolder)
{
    public WorkflowRunner Workflows => new(Catalog, Gate, Ocr);

    public BatchRunner Batches => new(Workflows);

    /// <summary>A path the user typed, made absolute against <see cref="CurrentDirectory"/>.</summary>
    public string FullPath(string path) => Path.GetFullPath(Path.Combine(CurrentDirectory, path));
}
