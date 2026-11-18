using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;

namespace PdfPro.Operations;

/// <summary>
/// A <see cref="OperationKind.Content"/> operation: in a session it writes the next revision
/// (<see cref="IContentOperation.ApplyToFileAsync"/>); run on a file (command line, batch) the changed copy goes to the
/// output folder and the input is never overwritten.
/// </summary>
public abstract class FileContentOperation<TParams> : PdfOperation<TParams>, IContentOperation
    where TParams : class
{
    public override OperationKind Kind => OperationKind.Content;

    /// <summary>Resource key of the progress stage.</summary>
    public abstract string Stage { get; }

    public abstract Task ApplyToFileAsync(string inputPath, Stream output, object parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken);

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
}
