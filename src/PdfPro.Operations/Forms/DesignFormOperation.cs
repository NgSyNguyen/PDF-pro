using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;

namespace PdfPro.Operations.Forms;

/// <param name="Remove">Fully qualified names of fields to take off the form, with their widgets.</param>
/// <param name="Add">Fields to place (after everything else: a name freed by a removal or a rename can be used).</param>
/// <param name="Widgets">Widgets moved, resized or taken off (by the names before <paramref name="Update"/>).</param>
/// <param name="Update">Fields whose properties change in place.</param>
public sealed record DesignFormParams(
    IReadOnlyList<string>? Remove = null,
    IReadOnlyList<FormFieldSpec>? Add = null,
    IReadOnlyList<FormWidgetChange>? Widgets = null,
    IReadOnlyList<FormFieldUpdate>? Update = null)
{
    public bool Equals(DesignFormParams? other)
        => other is not null
            && (Remove ?? []).SequenceEqual(other.Remove ?? [])
            && (Add ?? []).SequenceEqual(other.Add ?? [])
            && (Widgets ?? []).SequenceEqual(other.Widgets ?? [])
            && (Update ?? []).SequenceEqual(other.Update ?? []);

    public override int GetHashCode() => HashCode.Combine(Remove?.Count, Add?.Count, Widgets?.Count, Update?.Count);
}

/// <summary>
/// "forms.design": changes the fields of the document's form, in this order — fields taken off (their widgets leave
/// the pages, parents left empty go too), widgets moved, resized or taken off, fields changed in place (their actions
/// and colours stay), fields placed with their widgets and appearances (<see cref="FormWriter.AddFields"/>). A content
/// operation: in a session it writes the next revision (one undo step).
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
        var widgets = parameters.Widgets ?? [];
        var update = parameters.Update ?? [];
        if (remove.Count == 0 && add.Count == 0 && widgets.Count == 0 && update.Count == 0)
        {
            return OperationValidationResult.Failed(new OperationValidationError(null, "Nothing to change."));
        }

        if (remove.Count > MaxFields || add.Count > MaxFields || widgets.Count > MaxFields || update.Count > MaxFields)
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

        for (var i = 0; i < widgets.Count; i++)
        {
            if (widgets[i] is not { } change || string.IsNullOrWhiteSpace(change.Name) || change.Widget < 0
                || change.Rect is { } rect && (rect.Width < 2 || rect.Height < 2 || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Bottom)))
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Widgets)}[{i}]", "A widget needs its field, its place and a box at least 2 points wide and high."));
            }
        }

        for (var i = 0; i < update.Count; i++)
        {
            // The page and box of the properties are not used: any valid ones are checked in their place.
            if (update[i] is not { } change || string.IsNullOrWhiteSpace(change.Name) || change.Properties is null)
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Update)}[{i}]", "A change needs its field and its properties."));
            }
            else if ((change.Properties with { Page = 0, Rect = new PdfRect(0, 0, 10, 10) }).Problem(1) is { } problem)
            {
                errors.Add(new OperationValidationError($"{nameof(parameters.Update)}[{i}]", problem));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override void Apply(PdfDocument document, string inputPath, DesignFormParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 4, Stage));
        FormWriter.RemoveFields(document, [.. (parameters.Remove ?? []).Distinct(StringComparer.Ordinal)]);
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new OperationProgress(1, 4, Stage));
        if (parameters.Widgets is { Count: > 0 } widgets)
        {
            FormWriter.ChangeWidgets(document, widgets);
        }

        progress.Report(new OperationProgress(2, 4, Stage));
        try
        {
            if (parameters.Update is { Count: > 0 } update)
            {
                FormWriter.UpdateFields(document, update);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(3, 4, Stage));
            if (parameters.Add is { Count: > 0 } add)
            {
                FormWriter.AddFields(document, add);
            }
        }
        catch (ArgumentException ex)
        {
            // A name taken by a field that stays, or a kind that would change: only the document can tell.
            throw new OperationValidationException(Id, [new OperationValidationError(null, ex.Message)]);
        }

        progress.Report(new OperationProgress(4, 4, Stage));
    }
}
