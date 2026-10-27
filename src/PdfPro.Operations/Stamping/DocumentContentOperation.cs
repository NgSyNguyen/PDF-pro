using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Stamping;

/// <summary>
/// A <see cref="OperationKind.Content"/> operation that changes a document in place with PDFsharp: in a session it
/// writes the next revision (<see cref="IContentOperation.ApplyToFileAsync"/>); run on a file (command line, batch) the
/// changed copy goes to the output folder and the input is never overwritten.
/// </summary>
public abstract class DocumentContentOperation<TParams> : FileContentOperation<TParams>
    where TParams : class
{
    public override async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        if (parameters is not TParams request)
        {
            throw new ArgumentException($"Expected {typeof(TParams).Name}.", nameof(parameters));
        }

        await Task.Run(() => Run(inputPath, output, request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the opened document; <paramref name="inputPath"/> is the file it was read from.</summary>
    protected abstract void Apply(PdfDocument document, string inputPath, TParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);

    /// <summary>Opens a document for modification; a file PDFsharp cannot read is reported as invalid data.</summary>
    internal static PdfDocument OpenForChange(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("The document cannot be read.", ex);
        }
    }

    private void Run(string inputPath, Stream output, TParams request, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        using var document = OpenForChange(inputPath);
        var validation = Validate(request, new DocumentInfo(document.PageCount));
        if (!validation.IsValid)
        {
            throw new OperationValidationException(Id, validation.Errors);
        }

        Apply(document, inputPath, request, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        document.Save(output, closeStream: false);
    }
}
