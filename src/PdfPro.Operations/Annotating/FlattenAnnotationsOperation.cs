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
                || AppearancePlacement.Place(document, appearance, AppearancePlacement.Rectangle(rect)) is not { } placement)
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
            yield return (annotation, AppearancePlacement.Normal(annotation), (flags & (HiddenFlag | NoViewFlag)) != 0);
        }
    }
}
