using PdfPro.Core.Operations;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Comparing;

/// <summary>Two documents to compare, and what to leave out.</summary>
/// <param name="OldPath">The older version.</param>
/// <param name="NewPath">The newer version.</param>
/// <param name="OldPassword">The older version's password, or null (in memory only).</param>
/// <param name="NewPassword">The newer version's password, or null (in memory only).</param>
/// <param name="IgnoreRunningLines">Headers and footers repeated on most pages.</param>
/// <param name="IgnoreCase">Letter case.</param>
/// <param name="Visual">Also compare the pages as pictures.</param>
public sealed record CompareRequest(
    string OldPath,
    string NewPath,
    string? OldPassword = null,
    string? NewPassword = null,
    bool IgnoreRunningLines = true,
    bool IgnoreCase = false,
    bool Visual = true)
{
    public override string ToString() => $"CompareRequest {{ Visual = {Visual}, IgnoreRunningLines = {IgnoreRunningLines}, IgnoreCase = {IgnoreCase} }}";
}

/// <summary>Two documents compared: their text, and — when asked — their pages as pictures, in the order of the pairs.</summary>
public sealed record ComparedDocuments(TextComparison Text, IReadOnlyList<VisualDifference>? Visual);

/// <summary>
/// compare.documents as the worker runs it (SPEC Phase 11, 13): the text compared by PdfPig (<see cref="TextComparer"/>),
/// then the paired pages drawn by PDFium (<see cref="VisualComparer"/>) — where a "content bomb" page would exhaust the
/// app's memory. Not an operation of the catalog: the worker is given it beside them.
/// </summary>
public sealed class ComparisonMeasurement(PdfiumGate gate) : IMeasuringOperation
{
    public const string OperationId = "compare.documents";

    public const string TextStage = "Stage_CompareText";

    public string Id => OperationId;

    public Type MeasureType => typeof(CompareRequest);

    public Type MeasurementType => typeof(ComparedDocuments);

    /// <exception cref="Security.DocumentPasswordException">A document is protected and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">A document cannot be read.</exception>
    /// <exception cref="PdfDocumentException">PDFium cannot open a document to compare its pages as pictures.</exception>
    public async Task<ComparedDocuments> CompareAsync(CompareRequest request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(new OperationProgress(0, 1, TextStage));
        var text = await TextComparer.CompareAsync(
            request.OldPath,
            request.OldPassword,
            request.NewPath,
            request.NewPassword,
            new TextCompareOptions(request.IgnoreRunningLines, request.IgnoreCase),
            cancellationToken).ConfigureAwait(false);
        if (!request.Visual)
        {
            return new ComparedDocuments(text, null);
        }

        var visual = await VisualComparer.CompareAsync(gate, request.OldPath, request.OldPassword, request.NewPath, request.NewPassword, text.Pages, new VisualCompareOptions(), progress, cancellationToken).ConfigureAwait(false);
        return new ComparedDocuments(text, visual);
    }

    async Task<object> IMeasuringOperation.MeasureAsync(string input, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        => await CompareAsync(parameters as CompareRequest ?? throw new ArgumentException("Documents are compared with a CompareRequest.", nameof(parameters)), progress, cancellationToken).ConfigureAwait(false);
}
