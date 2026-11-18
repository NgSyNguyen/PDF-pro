using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>Editing the outline (bookmark) tree at the dictionary level: children, relinking, counts.</summary>
internal static class OutlineItems
{
    public const int MaxDepth = 64;
    public const int MaxItems = 100_000;

    /// <summary>The child items of an outline node (cycle-safe).</summary>
    public static List<PdfDictionary> Children(PdfDictionary parent)
    {
        var children = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var item = AsDictionary(parent.Elements["/First"]); item is not null && seen.Add(item); item = AsDictionary(item.Elements["/Next"]))
        {
            children.Add(item);
        }

        return children;
    }

    /// <summary>Makes <paramref name="kept"/> the children of <paramref name="parent"/>, in this order.</summary>
    public static void Relink(PdfDictionary parent, List<PdfDictionary> kept)
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
            if (parent.Reference is { } up)
            {
                item.Elements["/Parent"] = up;
            }

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
    /// Sets the counts of the whole tree after an edit and removes the outline when nothing is left in it.
    /// </summary>
    public static void Finish(PdfDocument document, PdfDictionary outlines)
    {
        var catalog = document.Internals.Catalog;
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

    /// <summary>
    /// Sets /Count on every item below <paramref name="parent"/> (positive when open, negative when closed) and returns
    /// how many descendants are visible when <paramref name="parent"/> is open.
    /// </summary>
    private static int Recount(PdfDictionary parent, int depth)
    {
        if (depth >= MaxDepth)
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
