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
public abstract class DocumentContentOperation<TParams> : PdfOperation<TParams>, IContentOperation
    where TParams : class
{
    public override OperationKind Kind => OperationKind.Content;

    /// <summary>Resource key of the progress stage.</summary>
    public abstract string Stage { get; }

    public async Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
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

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        TParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.InputPaths.Count != 1)
        {
            throw new ArgumentException("This operation works on one file.", nameof(context));
        }

        var input = Path.GetFullPath(context.InputPaths[0]);
        var outputPath = Path.GetFullPath(Path.Combine(context.OutputDirectory, Path.GetFileName(input)));
        if (string.Equals(input, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output would overwrite the input file; choose another output folder.");
        }

        Directory.CreateDirectory(context.OutputDirectory);
        try
        {
            var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (output.ConfigureAwait(false))
            {
                await ApplyToFileAsync(input, output, parameters, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }

        return OperationResult.Success(outputPath);
    }

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
