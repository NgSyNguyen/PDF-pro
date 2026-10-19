using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>
/// Cleans up after pages were deleted: references to them are cut everywhere, then links, bookmarks and the open
/// action that pointed at them are removed. A bookmark with children stays as a heading without destination.
/// </summary>
internal static class DeadDestinations
{
    private const int MaxOutlineDepth = 64;
    private const int MaxOutlineItems = 100_000;

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
            var budget = MaxOutlineItems;
            PruneOutline(outlines, names, depth: 0, ref budget);
            var visible = Recount(outlines, depth: 0);
            if (outlines.Elements["/First"] is null)
            {
                catalog.Elements.Remove("/Outlines");
            }
            else if (visible > 0)
            {
                outlines.Elements.SetInteger("/Count", visible);
            }
            else
            {
                outlines.Elements.Remove("/Count");
            }
        }

        var openAction = catalog.Elements["/OpenAction"];
        var openDestination = AsDictionary(openAction) is { } action ? Destinations.Of(action, names) : Destinations.Explicit(openAction, names);
        if (Destinations.IsDead(openDestination))
        {
            catalog.Elements.Remove("/OpenAction");
        }
    }

    /// <summary>The child items of an outline node (cycle-safe).</summary>
    private static List<PdfDictionary> Children(PdfDictionary parent)
    {
        var children = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var item = AsDictionary(parent.Elements["/First"]); item is not null && seen.Add(item); item = AsDictionary(item.Elements["/Next"]))
        {
            children.Add(item);
        }

        return children;
    }

    private static void PruneOutline(PdfDictionary parent, NamedDestinations names, int depth, ref int budget)
    {
        var kept = new List<PdfDictionary>();
        foreach (var child in Children(parent))
        {
            if (--budget < 0 || depth >= MaxOutlineDepth)
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

        Relink(parent, kept);
    }

    private static void Relink(PdfDictionary parent, List<PdfDictionary> kept)
    {
        if (kept.Count == 0)
        {
            parent.Elements.Remove("/First");
            parent.Elements.Remove("/Last");
            parent.Elements.Remove("/Count");
            return;
        }

        parent.Elements["/First"] = kept[0].Reference!;
        parent.Elements["/Last"] = kept[^1].Reference!;
        for (var i = 0; i < kept.Count; i++)
        {
            var item = kept[i];
            if (i == 0)
            {
                item.Elements.Remove("/Prev");
            }
            else
            {
                item.Elements["/Prev"] = kept[i - 1].Reference!;
            }

            if (i == kept.Count - 1)
            {
                item.Elements.Remove("/Next");
            }
            else
            {
                item.Elements["/Next"] = kept[i + 1].Reference!;
            }
        }
    }

    /// <summary>
    /// Sets /Count on every item below <paramref name="parent"/> (positive when open, negative when closed) and returns
    /// how many descendants are visible when <paramref name="parent"/> is open.
    /// </summary>
    private static int Recount(PdfDictionary parent, int depth)
    {
        if (depth >= MaxOutlineDepth)
        {
            return 0;
        }

        var visible = 0;
        foreach (var child in Children(parent))
        {
            var isOpen = child.Elements.GetInteger("/Count") > 0;
            var below = Recount(child, depth + 1);
            if (below == 0)
            {
                child.Elements.Remove("/Count");
            }
            else
            {
                child.Elements.SetInteger("/Count", isOpen ? below : -below);
            }

            visible += 1 + (isOpen ? below : 0);
        }

        return visible;
    }
}
