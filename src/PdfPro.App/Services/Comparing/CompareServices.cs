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

/// <summary>The text compared by PdfPig (<see cref="TextComparer"/>), then the paired pages drawn by PDFium (<see cref="VisualComparer"/>).</summary>
public sealed class DocumentComparison(PdfiumGate gate) : IDocumentComparison
{
    public const string TextStage = "Stage_CompareText";

    public async Task<ComparisonResult> CompareAsync(ComparedFile oldFile, ComparedFile newFile, CompareSettings settings, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldFile);
        ArgumentNullException.ThrowIfNull(newFile);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(new OperationProgress(0, 1, TextStage));
        var text = await TextComparer.CompareAsync(
            oldFile.Path,
            oldFile.Password,
            newFile.Path,
            newFile.Password,
            new TextCompareOptions(settings.IgnoreRunningLines, settings.IgnoreCase),
            cancellationToken).ConfigureAwait(false);
        if (!settings.Visual)
        {
            return new ComparisonResult(text, null);
        }

        var visual = await VisualComparer.CompareAsync(gate, oldFile.Path, oldFile.Password, newFile.Path, newFile.Password, text.Pages, new VisualCompareOptions(), progress, cancellationToken).ConfigureAwait(false);
        return new ComparisonResult(text, visual);
    }
}

/// <summary>What the comparison window uses: the comparison, PDFium to show the pages, and the password prompt.</summary>
public sealed record CompareServices(IDocumentComparison Comparison, IPdfDocumentLoader Loader, IPageImageRenderer Renderer, IPasswordPrompt Passwords);
