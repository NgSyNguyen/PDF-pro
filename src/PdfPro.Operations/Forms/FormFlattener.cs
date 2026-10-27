using PdfPro.Core.Documents;
using PdfPro.Core.Forms;
using PdfPro.Core.Operations;
using PdfPro.Operations.Annotating;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Forms;

/// <param name="Fields">The fields to flatten (fully qualified names); null flattens the whole form.</param>
public sealed record FlattenFormParams(IReadOnlyList<string>? Fields = null)
{
    public bool Equals(FlattenFormParams? other)
        => other is not null && (Fields is null ? other.Fields is null : other.Fields is not null && Fields.SequenceEqual(other.Fields));

    public override int GetHashCode() => Fields?.Count ?? -1;
}

/// <summary>
/// "forms.flatten": draws the fields as they are shown (their values) into the page content and takes them off the
/// form, so that nobody can change them any more. A content operation: in a session it writes the next revision.
/// </summary>
public sealed class FlattenFormOperation : DocumentContentOperation<FlattenFormParams>
{
    public const string OperationId = "forms.flatten";

    public override string Id => OperationId;

    public override string Stage => "Stage_FlattenForm";

    protected override OperationValidationResult ValidateCore(FlattenFormParams parameters, DocumentInfo document)
        => parameters.Fields is { } fields && (fields.Count == 0 || fields.Any(string.IsNullOrWhiteSpace))
            ? OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Fields), "Name at least one field, or none for the whole form."))
            : OperationValidationResult.Success;

    protected override void Apply(PdfDocument document, string inputPath, FlattenFormParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new OperationProgress(0, 1, Stage));
        FormFlattener.Flatten(document, parameters.Fields);
        progress.Report(new OperationProgress(1, 1, Stage));
    }
}

/// <summary>
/// Flattening form fields (12.7.1): every widget's normal appearance (the state of /AS for buttons) is drawn into its
/// page's content in /Annots order, then the widgets and their fields leave the form; the /AcroForm goes when no field
/// is left. Widgets without an appearance (or all of them, when /NeedAppearances asks for it) are drawn first, as other
/// readers would show them. Hidden widgets are taken off without being drawn. Signed signature fields stay: drawing
/// them in would break the signature.
/// </summary>
public static class FormFlattener
{
    /// <summary>Annotation flags (12.5.3): Hidden and NoView.</summary>
    private const int HiddenFlag = 1 << 1;
    private const int NoViewFlag = 1 << 5;

    /// <summary>Flattens the named fields (null: all of them). Returns how many fields were flattened.</summary>
    public static int Flatten(PdfDocument document, IReadOnlyCollection<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (AsDictionary(document.Internals.Catalog.Elements["/AcroForm"]) is not { } form)
        {
            return 0;
        }

        var redraw = Resolve(form.Elements["/NeedAppearances"]) is PdfBoolean { Value: true };
        var chosen = FormReader.Fields(document)
            .Where(f => (names is null || names.Contains(f.Field.Name)) && !(f.Field.Kind == FormFieldKind.Signature && f.Dictionary.Elements.ContainsKey("/V")))
            .ToList();
        if (chosen.Count == 0)
        {
            return 0;
        }

        FormWriter.DrawMissingAppearances(document, chosen, redraw);
        var widgets = chosen.SelectMany(f => f.Widgets).ToHashSet(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            var placements = new List<FormPlacement>();
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            foreach (var annotation in annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>().Where(widgets.Contains))
            {
                var flags = Resolve(annotation.Elements["/F"]) is PdfInteger integer ? integer.Value : 0;
                if ((flags & (HiddenFlag | NoViewFlag)) == 0
                    && AppearancePlacement.Normal(annotation) is { } appearance
                    && AsArray(annotation.Elements["/Rect"]) is { Elements.Count: 4 } rect
                    && AppearancePlacement.Place(document, appearance, AppearancePlacement.Rectangle(rect)) is { } placement)
                {
                    placements.Add(placement);
                }
            }

            PageStamper.Place(page, placements, StampDepth.Above);
        }

        FormWriter.RemoveFields(document, [.. chosen.Select(f => f.Field.Name).Distinct(StringComparer.Ordinal)]);
        if (AsArray(form.Elements["/Fields"]) is not { Elements.Count: > 0 })
        {
            document.Internals.Catalog.Elements.Remove("/AcroForm");
        }

        return chosen.Count;
    }
}
