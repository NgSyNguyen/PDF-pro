using System.ComponentModel.DataAnnotations;
using PdfPro.Core.Annotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Text;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Annotating;

/// <summary>What is done to an annotation already in the file.</summary>
public enum FileAnnotationAction
{
    /// <summary>Adds an answer to its conversation.</summary>
    Reply,

    /// <summary>Sets a review state (an answer carrying the state).</summary>
    SetState,

    /// <summary>Replaces its comment.</summary>
    SetContents,

    /// <summary>Removes it with its conversation and pop-up window.</summary>
    Delete,
}

/// <summary>
/// A change to one annotation already in the file. The annotation is found by its place in the page's /Annots
/// (<paramref name="Index"/>, as PDFium read it) and recognised by its kind, name and place — when the file changed
/// meanwhile it is looked for by those; when it cannot be found the change is refused rather than made elsewhere.
/// </summary>
/// <param name="Page">1-based page.</param>
/// <param name="Index">Its place in the page's /Annots when read.</param>
/// <param name="Kind">Its kind, to recognise it.</param>
/// <param name="Name">Its /NM, or null when it has none.</param>
/// <param name="Rect">Its /Rect when read, to recognise it.</param>
/// <param name="Action">What to do.</param>
/// <param name="Text">The answer or the new comment.</param>
/// <param name="State">The review state to set.</param>
/// <param name="Author">Who answers or reviews.</param>
/// <param name="When">When; the answer is dated with it.</param>
/// <param name="Id">The name (/NM) of the answer added.</param>
public sealed record FileAnnotationEdit(
    [property: Range(1, int.MaxValue)] int Page,
    [property: Range(0, int.MaxValue)] int Index,
    FileAnnotationKind Kind,
    string? Name,
    PdfRect Rect,
    FileAnnotationAction Action,
    string? Text = null,
    ReviewState State = ReviewState.None,
    string Author = "",
    DateTimeOffset When = default,
    string? Id = null);

/// <param name="Edits">The changes, made in order (removals last).</param>
public sealed record EditFileAnnotationsParams([property: Required] IReadOnlyList<FileAnnotationEdit> Edits);

/// <summary>
/// "annotate.editFile": answers, reviews, rewrites or removes comments that were already in the file — a content
/// change (the next revision of the session, one undoable step), since they live in the file, not in the page plan.
/// </summary>
public sealed class EditFileAnnotationsOperation : DocumentContentOperation<EditFileAnnotationsParams>
{
    public const string OperationId = "annotate.editFile";

    /// <summary>How far a /Rect may be from the one read to still be the same annotation, in points.</summary>
    private const double RectTolerance = 0.5;

    public override string Id => OperationId;

    public override string Stage => "Stage_Annotate";

    protected override OperationValidationResult ValidateCore(EditFileAnnotationsParams parameters, DocumentInfo document)
    {
        var errors = new List<OperationValidationError>();
        if (parameters.Edits is null || parameters.Edits.Count == 0)
        {
            errors.Add(new OperationValidationError(nameof(parameters.Edits), "At least one change is needed."));
            return OperationValidationResult.Failed(errors);
        }

        for (var i = 0; i < parameters.Edits.Count; i++)
        {
            var member = $"{nameof(parameters.Edits)}[{i}]";
            var edit = parameters.Edits[i];
            if (edit is null)
            {
                errors.Add(new OperationValidationError(member, "A change is missing."));
                continue;
            }

            if (edit.Page < 1 || edit.Page > document.PageCount)
            {
                errors.Add(new OperationValidationError(member, $"The page must be between 1 and {document.PageCount}."));
            }

            if (edit.Index < 0)
            {
                errors.Add(new OperationValidationError(member, "The annotation index cannot be negative."));
            }

            if (edit.Action is FileAnnotationAction.Reply or FileAnnotationAction.SetContents
                && (edit.Text is null || edit.Text.Length > AnnotationItem.MaxContentsLength || (edit.Action == FileAnnotationAction.Reply && string.IsNullOrWhiteSpace(edit.Text))))
            {
                errors.Add(new OperationValidationError(member, "The text is missing or longer than 20000 characters."));
            }

            if (edit.Action is FileAnnotationAction.Reply or FileAnnotationAction.SetState && string.IsNullOrWhiteSpace(edit.Id))
            {
                errors.Add(new OperationValidationError(member, "An answer needs a name."));
            }
        }

        return errors.Count == 0 ? OperationValidationResult.Success : OperationValidationResult.Failed(errors);
    }

    protected override void Apply(PdfDocument document, string inputPath, EditFileAnnotationsParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        // Every annotation is found before anything changes, so indices still match what was read.
        var targets = parameters.Edits.Select(edit => (Edit: edit, Page: document.Pages[edit.Page - 1], Annotation: Find(document, document.Pages[edit.Page - 1], edit))).ToList();
        foreach (var (edit, page, annotation) in targets.Where(t => t.Edit.Action != FileAnnotationAction.Delete))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = TextNormalizer.ToNfc(edit.Text ?? string.Empty);
            switch (edit.Action)
            {
                case FileAnnotationAction.Reply:
                    AnnotationWriter.AddToThread(document, page, annotation, edit.Rect, edit.Id!, edit.Author, edit.When, text.Trim(), state: null);
                    break;
                case FileAnnotationAction.SetState:
                    AnnotationWriter.AddToThread(document, page, annotation, edit.Rect, edit.Id!, edit.Author, edit.When, string.Empty, edit.State);
                    break;
                case FileAnnotationAction.SetContents:
                    if (text.Length == 0)
                    {
                        annotation.Elements.Remove("/Contents");
                    }
                    else
                    {
                        annotation.Elements["/Contents"] = text.All(c => c < 128) ? new PdfString(text) : new PdfString(text, PdfStringEncoding.Unicode);
                    }

                    annotation.Elements["/M"] = new PdfString(Core.Documents.PdfDate.Format(edit.When));
                    break;
            }
        }

        foreach (var group in targets.Where(t => t.Edit.Action == FileAnnotationAction.Delete).GroupBy(t => t.Page))
        {
            AnnotationThreads.Remove(document, group.Key, group.Select(t => t.Annotation).ToList());
        }
    }

    /// <summary>The annotation an edit is about: at its index when it is still the one read, otherwise found by its marks.</summary>
    /// <exception cref="InvalidDataException">It is not in the page any more.</exception>
    private static PdfDictionary Find(PdfDocument document, PdfPage page, FileAnnotationEdit edit)
    {
        var annotations = Annotations(page) ?? throw new InvalidDataException($"Page {edit.Page} has no annotations.");
        var candidates = annotations.Elements.Select((item, index) => (Index: index, Item: item, Dictionary: AsDictionary(item))).Where(c => c.Dictionary is not null).ToList();
        var match = candidates.FirstOrDefault(c => c.Index == edit.Index && IsSame(c.Dictionary!, edit));
        if (match.Dictionary is null)
        {
            match = candidates.FirstOrDefault(c => IsSame(c.Dictionary!, edit));
        }

        if (match.Dictionary is not { } annotation)
        {
            throw new InvalidDataException($"The annotation {edit.Index} of page {edit.Page} is not in the file any more.");
        }

        if (annotation.Reference is null)
        {
            // Stored inside the array: made an object of its own so that answers can point at it.
            document.Internals.AddObject(annotation);
            annotations.Elements[match.Index] = annotation.Reference!;
        }

        return annotation;
    }

    private static bool IsSame(PdfDictionary annotation, FileAnnotationEdit edit)
    {
        if (!string.Equals(Name(annotation, "/Subtype"), "/" + SubtypeName(edit.Kind), StringComparison.Ordinal))
        {
            return false;
        }

        if (edit.Name is { } name)
        {
            return string.Equals(annotation.Elements.GetString("/NM"), name, StringComparison.Ordinal);
        }

        return AsArray(annotation.Elements["/Rect"]) is { Elements.Count: 4 } rect
            && Near(Number(rect.Elements[0]), Math.Min(edit.Rect.Left, edit.Rect.Right))
            && Near(Number(rect.Elements[1]), edit.Rect.Bottom)
            && Near(Number(rect.Elements[2]), edit.Rect.Right)
            && Near(Number(rect.Elements[3]), edit.Rect.Top);
    }

    private static bool Near(double? value, double expected) => value is { } actual && Math.Abs(actual - expected) <= RectTolerance;

    private static double? Number(PdfItem item) => Resolve(item) switch
    {
        PdfReal real => real.Value,
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        _ => null,
    };

    /// <summary>The /Subtype of a kind (12.5.6), without the slash.</summary>
    private static string SubtypeName(FileAnnotationKind kind) => kind switch
    {
        FileAnnotationKind.ThreeD => "3D",
        FileAnnotationKind.XfaWidget => "XFAWidget",
        _ => kind.ToString(),
    };
}
