using PdfPro.App.Services.Rendering;
using PdfPro.Core.Operations;
using PdfPro.Operations.Comparing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Comparing;

/// <summary>A file to compare and the password it opens with (never shown).</summary>
public sealed record ComparedFile(string Path, string? Password)
{
    public override string ToString() => $"ComparedFile {{ password: {(Password is null ? "no" : "yes")} }}";
}

/// <summary>What to compare, and what to leave out.</summary>
/// <param name="IgnoreRunningLines">Headers and footers repeated on most pages.</param>
/// <param name="IgnoreCase">Letter case.</param>
/// <param name="Visual">Also compare the pages as pictures.</param>
public sealed record CompareSettings(bool IgnoreRunningLines = true, bool IgnoreCase = false, bool Visual = true);

/// <summary>Two documents compared: their text, and — when asked — their pages as pictures, in the order of the pairs.</summary>
public sealed record ComparisonResult(TextComparison Text, IReadOnlyList<VisualDifference>? Visual);

/// <summary>Compares two documents (seam for view-model tests).</summary>
public interface IDocumentComparison
{
    /// <exception cref="Operations.Security.DocumentPasswordException">A document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">A document cannot be read.</exception>
    /// <exception cref="PdfDocumentException">PDFium cannot open a document to compare its pages as pictures.</exception>
    Task<ComparisonResult> CompareAsync(ComparedFile oldFile, ComparedFile newFile, CompareSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// The text compared by PdfPig (<see cref="TextComparer"/>), then the paired pages drawn by PDFium
/// (<see cref="VisualComparer"/>) — in the worker process when it is on (<paramref name="runner"/>), so a "content bomb"
/// page exhausts the worker, not the window.
/// </summary>
public sealed class DocumentComparison(PdfiumGate gate, Optimizing.IExportRunner? runner = null) : IDocumentComparison
{
    public const string TextStage = ComparisonMeasurement.TextStage;

    private readonly ComparisonMeasurement _measurement = new(gate);

    public async Task<ComparisonResult> CompareAsync(ComparedFile oldFile, ComparedFile newFile, CompareSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldFile);
        ArgumentNullException.ThrowIfNull(newFile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        var request = new CompareRequest(oldFile.Path, newFile.Path, oldFile.Password, newFile.Password, settings.IgnoreRunningLines, settings.IgnoreCase, settings.Visual);
        var compared = runner is null
            ? await _measurement.CompareAsync(request, progress, cancellationToken).ConfigureAwait(false)
            : await runner.MeasureAsync(_measurement, oldFile.Path, request, (p, t) => _measurement.CompareAsync(request, p, t), progress, cancellationToken).ConfigureAwait(false);
        return new ComparisonResult(compared.Text, compared.Visual);
    }
}

/// <summary>What the comparison window uses: the comparison, PDFium to show the pages, and the password prompt.</summary>
public sealed record CompareServices(IDocumentComparison Comparison, IPdfDocumentLoader Loader, IPageImageRenderer Renderer, IPasswordPrompt Passwords);
