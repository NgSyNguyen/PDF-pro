using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfPro.Operations.Layout;

/// <summary>
/// Base of the sheet layouts (N-up, booklet, poster; <see cref="OperationKind.Export"/>): pages of one input file are
/// drawn on new sheets of a new file; the input never changes. In the app the current document is first written to a
/// working file, which is then the input.
/// </summary>
public abstract class ImpositionOperation<TParams>(ISourceRepairer repairer) : PdfOperation<TParams>
    where TParams : class
{
    /// <summary>Resource key of the progress stage (sheets done / sheets).</summary>
    public const string Stage = "Stage_LayoutSheets";

    public sealed override OperationKind Kind => OperationKind.Export;

    /// <summary>Appended to the input's name for runs on files ("-nup").</summary>
    protected abstract string OutputSuffix { get; }

    /// <summary>How many sheets the layout makes of pages of these displayed sizes (for the dialog's preview).</summary>
    public abstract int SheetCount(IReadOnlyList<PdfSize> pageSizes, TParams parameters);

    /// <summary>Writes the sheets of <paramref name="inputPath"/> to <paramref name="outputPath"/> (through a temporary file).</summary>
    /// <returns>The written file.</returns>
    /// <exception cref="OperationValidationException">The parameters do not fit the document.</exception>
    /// <exception cref="MaterializationException">The input cannot be read.</exception>
    public async Task<string> WriteAsync(
        string inputPath,
        string? password,
        TParams parameters,
        string outputPath,
        string workingDirectory,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(progress);
        var target = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var source = await OpenAsync(inputPath, password, workingDirectory, cancellationToken).ConfigureAwait(false);
            var validation = Validate(parameters, new DocumentInfo(source.PageCount));
            if (!validation.IsValid)
            {
                throw new OperationValidationException(Id, validation.Errors);
            }

            await Task.Run(
                () =>
                {
                    using var output = new PdfDocument();
                    output.Info.Title = Path.GetFileNameWithoutExtension(target);
                    output.Version = Math.Max(source.Version, 14);
                    Impose(output, source, parameters, progress, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Save(stream, closeStream: false);
                    stream.Flush(flushToDisk: true);
                },
                cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Draws the sheets into <paramref name="output"/>.</summary>
    protected abstract void Impose(PdfDocument output, PdfDocument source, TParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);

    protected override async Task<OperationResult> ExecuteCoreAsync(
        OperationContext context,
        TParams parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (context.InputPaths.Count != 1)
        {
            throw new ArgumentException("A sheet layout works on one file.", nameof(context));
        }

        var input = context.InputPaths[0];
        var output = Path.Combine(context.OutputDirectory, Path.GetFileNameWithoutExtension(input) + OutputSuffix + ".pdf");
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The output would overwrite the input file; choose another output folder.");
        }

        var written = await WriteAsync(input, context.Passwords?.For(input), parameters, output, context.WorkingDirectory, progress, cancellationToken).ConfigureAwait(false);
        return OperationResult.Success(written);
    }

    /// <summary>The 0-based pages a page range selects (all when empty).</summary>
    protected static IReadOnlyList<int> Selected(string? pages, int pageCount)
        => string.IsNullOrWhiteSpace(pages) ? [.. Enumerable.Range(0, pageCount)] : PageRanges.Parse(pages, pageCount);

    /// <summary>Validates an optional page range.</summary>
    protected static OperationValidationResult CheckPages(string? pages, int pageCount, string memberName)
        => string.IsNullOrWhiteSpace(pages) ? OperationValidationResult.Success : Organizing.ParameterChecks.Pages(pages, pageCount, memberName);

    private async Task<PdfDocument> OpenAsync(string path, string? password, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Open(path, password), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or IOException or UnauthorizedAccessException) && password is null)
        {
            // PDFsharp is stricter than PDFium: read a clean copy written by PDFium instead.
            var repaired = await repairer.RepairAsync(path, workingDirectory, cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => Open(repaired, null), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryError) when (retryError is not (OperationCanceledException or OutOfMemoryException))
            {
                throw new MaterializationException(MaterializationFailure.SourceUnreadable, SourceIds.Original, retryError);
            }
        }
    }

    private static PdfDocument Open(string path, string? password)
        => password is null ? PdfReader.Open(path, PdfDocumentOpenMode.Import) : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
}
