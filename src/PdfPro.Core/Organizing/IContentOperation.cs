using PdfPro.Core.Operations;

namespace PdfPro.Core.Organizing;

/// <summary>
/// A content operation (<see cref="OperationKind.Content"/>): page size, watermark, OCR… In a document session the
/// current page plan is first written to a file (one page per plan entry, in order); the operation rewrites that file
/// into the session's next revision, which then replaces the plan's sources (SPEC "DocumentSession và Revision").
/// </summary>
public interface IContentOperation : IPdfOperation
{
    /// <summary>
    /// Reads <paramref name="inputPath"/> (a complete PDF, not password protected), applies the change and writes the
    /// result to <paramref name="output"/>. The output has the same pages, in the same order.
    /// </summary>
    /// <param name="inputPath">The document to change; it is only read.</param>
    /// <param name="output">Receives the changed document.</param>
    /// <param name="parameters">The operation's parameter record (already validated for this document).</param>
    /// <param name="progress">Receives progress.</param>
    /// <param name="cancellationToken">Cancels before the output is complete.</param>
    /// <exception cref="OperationValidationException">The parameters do not fit the document.</exception>
    /// <exception cref="InvalidDataException">The input cannot be read as a PDF.</exception>
    Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);
}
