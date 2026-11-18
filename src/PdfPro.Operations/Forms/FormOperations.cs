using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Forms;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Forms;

/// <param name="Values">The values given to fields (each replacing the field's earlier value in the session).</param>
public sealed record FillFormParams([property: Required] IReadOnlyList<FormValue> Values);

/// <summary>
/// "forms.fill": gives values to the fields of the document's form. Like the other page operations it only edits the
/// plan (instant, one undo step); the values are written into the file — with their appearances — when the plan is
/// saved or materialized for a content change.
/// </summary>
public sealed class FillFormOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<FillFormParams>(materializer, loader)
{
    public const string OperationId = "forms.fill";

    /// <summary>The longest text value, and the most choices of a list box.</summary>
    public const int MaxTextLength = 20_000;
    public const int MaxChoices = 1_000;

    public override string Id => OperationId;

    protected override OperationValidationResult ValidateCore(FillFormParams parameters, DocumentInfo document)
        => Check(parameters.Values);

    protected override PagePlan Apply(PagePlan plan, FillFormParams parameters, PlanEditContext context)
        => plan.WithFormValues(parameters.Values);

    /// <summary>At least one value, each with a name (given once) and a value of a sensible length.</summary>
    internal static OperationValidationResult Check(IReadOnlyList<FormValue>? values)
    {
        if (values is null || values.Count == 0)
        {
            return OperationValidationResult.Failed(new OperationValidationError(nameof(FillFormParams.Values), "At least one value is needed."));
        }

        var errors = new List<OperationValidationError>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            var member = $"{nameof(FillFormParams.Values)}[{i}]";
            var value = values[i];
            if (value is null || string.IsNullOrWhiteSpace(value.Name) || value.Value is null)
            {
                errors.Add(new OperationValidationError(member, "A value needs a field name and a value."));
                continue;
            }

            if (!names.Add(value.Name))
            {
                errors.Add(new OperationValidationError(member, $"The field {value.Name} is given two values."));
            }

            if (value.Value.Length > MaxTextLength || value.Selected is { Count: > MaxChoices } || value.Selected?.Any(s => s is null || s.Length > MaxTextLength) == true)
            {
                errors.Add(new OperationValidationError(member, "The value is too long."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }
}

/// <param name="Values">The values the fields go back to (their defaults, else empty), computed from the form's fields.</param>
public sealed record ResetFormParams([property: Required] IReadOnlyList<FormValue> Values);

/// <summary>
/// "forms.reset": puts fields back to their default values (/DV, else empty or off). The caller computes those values
/// from the form; like forms.fill it only edits the plan (one undo step) and is written when the document is saved.
/// </summary>
public sealed class ResetFormOperation(IPlanMaterializer materializer, IPdfDocumentLoader loader)
    : PlanOperation<ResetFormParams>(materializer, loader)
{
    public const string OperationId = "forms.reset";

    public override string Id => OperationId;

    /// <summary>The values that put fields back to their defaults (buttons and signatures have none).</summary>
    public static IReadOnlyList<FormValue> Defaults(IEnumerable<FormField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return [.. fields.Where(f => f.IsFillable).Select(f => f.Kind switch
        {
            FormFieldKind.CheckBox or FormFieldKind.RadioGroup => new FormValue(f.Name, f.DefaultValue is { Length: > 0 } on ? on : FormField.Off),
            FormFieldKind.ListBox => new FormValue(f.Name, f.DefaultValue ?? string.Empty, f.DefaultValue is { Length: > 0 } choice ? [choice] : []),
            _ => new FormValue(f.Name, f.DefaultValue ?? string.Empty),
        })];
    }

    protected override OperationValidationResult ValidateCore(ResetFormParams parameters, DocumentInfo document)
        => FillFormOperation.Check(parameters.Values);

    protected override PagePlan Apply(PagePlan plan, ResetFormParams parameters, PlanEditContext context)
        => plan.WithFormValues(parameters.Values);
}
