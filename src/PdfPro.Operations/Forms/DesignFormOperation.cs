using PdfPro.Core.Documents;
using PdfPro.Core.Operations;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Forms;

/// <param name="Remove">Fully qualified names of fields to take off the form, with their widgets.</param>
/// <param name="Add">
/// Fields to place (after the removals: a field changed in the designer is removed and placed again under its name).
/// </param>
public sealed record DesignFormParams(IReadOnlyList<string>? Remove = null, IReadOnlyList<FormFieldSpec>? Add = null)
{
    public bool Equals(DesignFormParams? other)
        => other is not null
            && (Remove ?? []).SequenceEqual(other.Remove ?? [])
            && (Add ?? []).SequenceEqual(other.Add ?? []);

    public override int GetHashCode() => HashCode.Combine(Remove?.Count, Add?.Count);
}

/// <summary>
/// "forms.design": changes the fields of the document's form — fields taken off (their widgets leave the pages,
/// parents left empty go too), fields placed with their widgets and appearances (<see cref="FormWriter.AddFields"/>).
/// A content operation: in a session it writes the next revision (one undo step).
/// </summary>
public sealed class DesignFormOperation : DocumentContentOperation<DesignFormParams>
{
    public const string OperationId = "forms.design";

    /// <summary>Most fields placed or removed at once.</summary>
    public const int MaxFields = 2_000;

    public override string Id => OperationId;

    public override string Stage => "Stage_DesignForm";

    protected override OperationValidationResult ValidateCore(DesignFormParams parameters, DocumentInfo document)
    {
        var remove = parameters.Remove ?? [];
        var add = parameters.Add ?? [];
        if (remove.Count == 0 && add.Count == 0)
        {
            return OperationValidationResult.Failed(new OperationValidationError(null, "Nothing to change."));
        }

        if (remove.Count > MaxFields || add.Count > MaxFields)
        {
            return OperationValidationResult.Failed(new OperationValidationError(null, $"At most {MaxFields} fields at once."));
        }

        var errors = new List<OperationValidationError>();
        for (var i = 0; i < remove.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(remove[i]))
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Remove)}[{i}]", "A field name is needed."));
            }
        }

        for (var i = 0; i < add.Count; i++)
        {
            if (add[i] is not { } spec)
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Add)}[{i}]", "A field is needed."));
            }
            else if (spec.Problem(document.PageCount) is { } problem)
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Add)}[{i}]", problem));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override void Apply(PdfDocument document, string inputPath, DesignFormParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 2, Stage));
        FormWriter.RemoveFields(document, [.. (parameters.Remove ?? []).Distinct(StringComparer.Ordinal)]);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new OperationProgress(1, 2, Stage));
        if (parameters.Add is { Count: > 0 } add)
        {
            try
            {
                FormWriter.AddFields(document, add);
            }
            catch (ArgumentException ex)
            {
                // A name taken by a field that stays: only the document can tell.
                throw new OperationValidationException(Id, [new OperationValidationError(nameof(parameters.Add), ex.Message)]);
            }
        }

        progress.Report(new OperationProgress(2, 2, Stage));
    }
}
