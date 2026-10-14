using PdfPro.Core.Documents;

namespace PdfPro.Core.Operations;

/// <summary>
/// A single PDF feature (rotate, merge, encrypt…) shared by buttons, undo/redo, workflows, batch runs and the CLI.
/// </summary>
public interface IPdfOperation
{
    /// <summary>Stable identifier in the form "group.name", e.g. "organize.rotate".</summary>
    string Id { get; }

    OperationKind Kind { get; }

    /// <summary>Immutable parameter record type, annotated with DataAnnotations.</summary>
    Type ParamsType { get; }

    OperationValidationResult Validate(object parameters, DocumentInfo document);

    Task<OperationResult> ExecuteAsync(
        OperationContext context,
        object parameters,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken);
}
