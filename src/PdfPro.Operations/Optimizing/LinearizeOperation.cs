using PdfPro.Core.Documents;
using PdfPro.Core.Operations;

namespace PdfPro.Operations.Optimizing;

/// <param name="Overwrite">Replace files of the same name in the output folder (else "name (2).pdf").</param>
public sealed record LinearizeParams(bool Overwrite = false);

/// <summary>
/// optimize.linearize (<see cref="OperationKind.Export"/>, "Tối ưu cho web"): copies of files laid out for Fast Web
/// View by qpdf — a browser shows the first page before the rest has arrived.
/// </summary>
public sealed class LinearizeOperation(Qpdf qpdf) : PdfOperation<LinearizeParams>
{
    public const string OperationId = "optimize.linearize";

    public const string Stage = "Stage_Linearize";

    public override string Id => OperationId;

    public override OperationKind Kind => OperationKind.Export;

    /// <summary>Writes a linearized copy of <paramref name="input"/> to <paramref name="output"/>, replacing it safely.</summary>
    /// <exception cref="Core.ExternalTools.ExternalToolNotFoundException">qpdf is not installed.</exception>
    /// <exception cref="QpdfException">The file cannot be read.</exception>
    public Task WriteAsync(string input, string output, CancellationToken cancellationToken)
        => OptimizedFiles.WriteAsync(output, (temporary, token) => qpdf.LinearizeAsync(input, temporary, token), cancellationToken);

    protected override OperationValidationResult ValidateCore(LinearizeParams parameters, DocumentInfo document) => OperationValidationResult.Success;

    protected override async Task<OperationResult> ExecuteCoreAsync(OperationContext context, LinearizeParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var outputs = OptimizedFiles.Outputs(context, parameters.Overwrite);
        for (var i = 0; i < outputs.Count; i++)
        {
            progress.Report(new OperationProgress(i, outputs.Count, Stage));
            await WriteAsync(context.InputPaths[i], outputs[i], cancellationToken).ConfigureAwait(false);
        }

        progress.Report(new OperationProgress(outputs.Count, outputs.Count, Stage));
        return OperationResult.Success(outputs);
    }
}
