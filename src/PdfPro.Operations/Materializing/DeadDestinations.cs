using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// Cleans up after pages were deleted: references to them are cut everywhere, then links, bookmarks and the open
/// action that pointed at them are removed. A bookmark with children stays as a heading without destination.
/// </summary>
internal static class DeadDestinations
{
    public static void Remove(PdfDocument document, IReadOnlySet<PdfObject> removedPages, IEnumerable<PdfPage> pages)
    {
        if (removedPages.Count == 0)
        {
            return;
        }

        var catalog = document.Internals.Catalog;
        RemoveReferences(catalog, removedPages.Contains);

        var names = new NamedDestinations(document);
        foreach (var page in pages)
        {
            if (Annotations(page) is not { } annotations)
            {
                continue;
            }

            for (var i = annotations.Elements.Count - 1; i >= 0; i--)
            {
                if (AsDictionary(annotations.Elements[i]) is { } annotation && Name(annotation, "/Subtype") == "/Link"
                    && Destinations.IsInternal(annotation) && Destinations.IsDead(Destinations.Of(annotation, names)))
                {
                    annotations.Elements.RemoveAt(i);
                }
            }
        }

        if (AsDictionary(catalog.Elements["/Outlines"]) is { } outlines)
        {
            var budget = OutlineItems.MaxItems;
            PruneOutline(outlines, names, depth: 0, ref budget);
            OutlineItems.Finish(document, outlines);
        }

        var openAction = catalog.Elements["/OpenAction"];
        var openDestination = AsDictionary(openAction) is { } action ? Destinations.Of(action, names) : Destinations.Explicit(openAction, names);
        if (Destinations.IsDead(openDestination))
        {
            catalog.Elements.Remove("/OpenAction");
        }
    }

    private static void PruneOutline(PdfDictionary parent, NamedDestinations names, int depth, ref int budget)
    {
        var kept = new List<PdfDictionary>();
        foreach (var child in OutlineItems.Children(parent))
        {
            if (--budget < 0 || depth >= OutlineItems.MaxDepth)
            {
                kept.Add(child); // pathological outline: leave the rest untouched
                continue;
            }

            PruneOutline(child, names, depth + 1, ref budget);
            var dead = Destinations.IsInternal(child) && Destinations.IsDead(Destinations.Of(child, names));
            if (!dead)
            {
                kept.Add(child);
            }
            else if (child.Elements["/First"] is not null)
            {
                child.Elements.Remove("/Dest");
                child.Elements.Remove("/A");
                kept.Add(child);
            }
        }

        OutlineItems.Relink(parent, kept);
    }
}
