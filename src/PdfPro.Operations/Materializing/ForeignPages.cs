using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// Pages imported from another file. PDFsharp imports a page together with everything it references, so a link to
/// another page of that file would drag the whole file into the output (and point at a page that is not in the page
/// tree). Before importing, page references inside annotations are cut and link targets are replaced by page
/// numbers; after importing, the links are pointed at the right output pages, or removed when their target is not
/// part of the output.
/// </summary>
internal static class ForeignPages
{
    /// <summary>Prepares every page of a foreign document for import (the document is opened only for this).</summary>
    public static void Prepare(PdfDocument document)
    {
        var names = new NamedDestinations(document);
        var pageIndex = new Dictionary<PdfObject, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < document.PageCount; i++)
        {
            pageIndex[document.Pages[i]] = i;
        }

        foreach (var page in pageIndex.Keys.OfType<PdfPage>())
        {
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            for (var i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (AsDictionary(annotations.Elements[i]) is not { } annotation)
                {
                    continue;
                }

                annotation.Elements.Remove("/P");
                annotation.Elements.Remove("/IRT");
                if (Name(annotation, "/Subtype") == "/Widget")
                {
                    // Keep the widget's appearance but not its form field, whose tree spans other pages (Phase 7).
                    annotation.Elements.Remove("/Parent");
                }

                if (Name(annotation, "/Subtype") == "/Link" && Destinations.IsInternal(annotation)
                    && !ToPageNumber(annotation, names, pageIndex))
                {
                    annotations.Elements.RemoveAt(i);
                    continue;
                }

                // Anything else still pointing at a page (actions, odd custom entries) would import that page too.
                RemoveReferences(annotation, IsPage);
            }
        }
    }

    /// <summary>Points imported links at the first output page showing their target, or removes them.</summary>
    /// <param name="placed">The output pages.</param>
    /// <param name="foreignSourceIds">Sources whose pages were imported.</param>
    public static void Retarget(IReadOnlyList<PlacedPage> placed, IReadOnlySet<string> foreignSourceIds)
    {
        var firstOutputPage = new Dictionary<(string Source, int Page), PdfPage>();
        foreach (var (page, pageRef) in placed)
        {
            if (!pageRef.IsBlank)
            {
                firstOutputPage.TryAdd((pageRef.SourceId, pageRef.SourcePageIndex), page);
            }
        }

        foreach (var (page, pageRef) in placed)
        {
            if (!foreignSourceIds.Contains(pageRef.SourceId) || Annotations(page) is not { } annotations)
            {
                continue;
            }

            for (var i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (AsDictionary(annotations.Elements[i]) is { } annotation
                    && AsArray(annotation.Elements["/Dest"]) is { Elements.Count: > 0 } destination
                    && destination.Elements[0] is PdfInteger target)
                {
                    if (firstOutputPage.TryGetValue((pageRef.SourceId, target.Value), out var targetPage))
                    {
                        destination.Elements[0] = targetPage.Reference!;
                    }
                    else
                    {
                        annotations.Elements.RemoveAt(i);
                    }
                }
            }
        }
    }

    /// <summary>Replaces the link's destination by [pageNumber …]; false when the target cannot be found.</summary>
    private static bool ToPageNumber(PdfDictionary link, NamedDestinations names, Dictionary<PdfObject, int> pageIndex)
    {
        var destination = Destinations.Of(link, names);
        if (destination is not { Elements.Count: > 0 } || Resolve(destination.Elements[0]) is not PdfObject target
            || !pageIndex.TryGetValue(target, out var index))
        {
            return false;
        }

        var placeholder = new PdfArray();
        placeholder.Elements.Add(new PdfInteger(index));
        for (var i = 1; i < destination.Elements.Count; i++)
        {
            var item = destination.Elements[i];
            placeholder.Elements.Add(item is PdfObject { IsIndirect: false } direct ? direct.Clone() : item);
        }

        link.Elements["/Dest"] = placeholder;
        link.Elements.Remove("/A");
        return true;
    }
}
