using PdfPro.Core.Annotations;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Stamping;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Annotating;

/// <param name="Pages">Page range; null or empty means all pages.</param>
/// <param name="Groups">The families of comments to flatten; null means all of them.</param>
public sealed record FlattenAnnotationsParams(string? Pages = null, IReadOnlyList<AnnotationGroup>? Groups = null)
{
    public bool Equals(FlattenAnnotationsParams? other)
        => other is not null && Pages == other.Pages
            && (Groups is null ? other.Groups is null : other.Groups is not null && Groups.SequenceEqual(other.Groups));

    public override int GetHashCode() => HashCode.Combine(Pages, Groups?.Count);
}

/// <summary>
/// "annotate.flatten": draws the comments of the pages (notes, markup, shapes, ink, text boxes, stamps… or only the
/// chosen families) into the page content as they are shown, then takes them off with their conversations and pop-up windows — the page looks the same
/// but the comments cannot be changed or removed any more. Links, form fields and other annotations that are not
/// comments stay; so do comments without an appearance (nothing to draw: they would vanish). Hidden comments are taken
/// off without being drawn, as they were not shown.
/// </summary>
public sealed class FlattenAnnotationsOperation : DocumentContentOperation<FlattenAnnotationsParams>
{
    public const string OperationId = "annotate.flatten";

    /// <summary>Annotation flags (12.5.3): Hidden and NoView.</summary>
    private const int HiddenFlag = 1 << 1;
    private const int NoViewFlag = 1 << 5;

    public override string Id => OperationId;

    public override string Stage => "Stage_Annotate";

    protected override OperationValidationResult ValidateCore(FlattenAnnotationsParams parameters, DocumentInfo document)
        => ParameterChecks.Combine(
            parameters.Groups is null || (parameters.Groups.Count > 0 && parameters.Groups.All(Enum.IsDefined))
                ? OperationValidationResult.Success
                : OperationValidationResult.Failed(new OperationValidationError(nameof(parameters.Groups), "Choose at least one known kind of comment.")),
            StampContent.CheckPages(parameters.Pages, document, nameof(parameters.Pages)));

    protected override void Apply(PdfDocument document, string inputPath, FlattenAnnotationsParams parameters, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        var pages = StampContent.Pages(parameters.Pages, document.PageCount);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new OperationProgress(i, pages.Count, Stage));
            Flatten(document, document.Pages[pages[i]], parameters.Groups);
        }

        progress.Report(new OperationProgress(pages.Count, pages.Count, Stage));
    }

    /// <summary>The comments of a page that would be flattened: shown with an appearance, or hidden.</summary>
    /// <param name="page">The page.</param>
    /// <param name="groups">The families of comments; null means all of them.</param>
    public static int Count(PdfPage page, IReadOnlyCollection<AnnotationGroup>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Comments(page, groups).Count(c => c.Hidden || c.Appearance is not null);
    }

    private static void Flatten(PdfDocument document, PdfPage page, IReadOnlyCollection<AnnotationGroup>? groups)
    {
        var placements = new List<FormPlacement>();
        var flattened = new List<PdfDictionary>();
        foreach (var (annotation, appearance, hidden) in Comments(page, groups))
        {
            if (hidden)
            {
                flattened.Add(annotation);
                continue;
            }

            if (appearance is null || AsArray(annotation.Elements["/Rect"]) is not { Elements.Count: 4 } rect
                || Placement(document, appearance, Rectangle(rect)) is not { } placement)
            {
                continue;
            }

            placements.Add(placement);
            flattened.Add(annotation);
        }

        if (flattened.Count == 0)
        {
            return;
        }

        // Annots order is drawing order: later comments are drawn over earlier ones, as viewers show them.
        PageStamper.Place(page, placements, StampDepth.Above);
        AnnotationThreads.Remove(document, page, flattened);
    }

    /// <summary>The page's comments (answers excluded: viewers do not draw them), with their normal appearance.</summary>
    private static IEnumerable<(PdfDictionary Annotation, PdfDictionary? Appearance, bool Hidden)> Comments(PdfPage page, IReadOnlyCollection<AnnotationGroup>? groups)
    {
        if (Annotations(page) is not { } annotations)
        {
            yield break;
        }

        foreach (var annotation in annotations.Elements.Select(AsDictionary).OfType<PdfDictionary>())
        {
            if (AnnotationGroups.Of(AnnotationGroups.KindOf(Name(annotation, "/Subtype"))) is not { } group
                || (groups is not null && !groups.Contains(group)) || AnnotationThreads.IsAnswer(annotation))
            {
                continue;
            }

            var flags = Resolve(annotation.Elements["/F"]) is PdfInteger integer ? integer.Value : 0;
            yield return (annotation, NormalAppearance(annotation), (flags & (HiddenFlag | NoViewFlag)) != 0);
        }
    }

    /// <summary>/AP /N: the stream itself, or the one of the state named by /AS.</summary>
    private static PdfDictionary? NormalAppearance(PdfDictionary annotation)
    {
        if (AsDictionary(annotation.Elements["/AP"]) is not { } appearances || AsDictionary(appearances.Elements["/N"]) is not { } normal)
        {
            return null;
        }

        if (normal.Stream is not null)
        {
            return normal;
        }

        return Name(annotation, "/AS") is { } state && AsDictionary(normal.Elements[state]) is { Stream: not null } chosen ? chosen : null;
    }

    /// <summary>
    /// Where the appearance goes (12.5.5): its /BBox, transformed by its /Matrix, is fitted to the annotation's /Rect.
    /// The form's /Matrix itself is applied by the Do operator.
    /// </summary>
    private static FormPlacement? Placement(PdfDocument document, PdfDictionary form, PdfRect rect)
    {
        if (AsArray(form.Elements["/BBox"]) is not { Elements.Count: 4 } box || (rect.Width <= 0 && rect.Height <= 0))
        {
            return null;
        }

        var matrix = AsArray(form.Elements["/Matrix"]) is { Elements.Count: 6 } m
            ? new PdfMatrix(Number(m.Elements[0]), Number(m.Elements[1]), Number(m.Elements[2]), Number(m.Elements[3]), Number(m.Elements[4]), Number(m.Elements[5]))
            : PdfMatrix.Identity;
        var drawn = matrix.TransformBounds(Rectangle(box));
        var scaleX = drawn.Width > 0 ? rect.Width / drawn.Width : 1;
        var scaleY = drawn.Height > 0 ? rect.Height / drawn.Height : 1;
        var fit = PdfMatrix.Translation(-drawn.Left, -drawn.Bottom)
            .Then(PdfMatrix.Scaling(scaleX, scaleY))
            .Then(PdfMatrix.Translation(rect.Left, rect.Bottom));

        // Appearance streams are form XObjects (12.5.5); some writers leave the entries out.
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        if (form.Reference is null)
        {
            document.Internals.AddObject(form);
        }

        return new FormPlacement(form.Reference!, fit);
    }

    private static PdfRect Rectangle(PdfArray array)
        => new(Number(array.Elements[0]), Number(array.Elements[1]), Number(array.Elements[2]), Number(array.Elements[3]));

    private static double Number(PdfItem item) => Resolve(item) switch
    {
        PdfReal real => real.Value,
        PdfInteger integer => integer.Value,
        PdfLongInteger integer => integer.Value,
        _ => 0,
    };
}
