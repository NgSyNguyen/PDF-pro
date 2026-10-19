using PdfPro.Core.Organizing;
using PdfSharp.Pdf;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Materializing;

/// <summary>A page of the output and the plan entry it shows.</summary>
internal sealed record PlacedPage(PdfPage Page, PageRef Ref);

/// <summary>
/// Rearranges the base document's page tree into the plan's order. Pages of the base source are moved, not copied,
/// so everything that points at them (bookmarks, links, named destinations, form fields) still points at the right
/// page; only repeated pages are copies. Pages of other sources are imported, blank pages created.
/// </summary>
internal static class PageTreeBuilder
{
    // Keys a copy of a page must not share with the original: its parent, its slot in the structure tree and the
    // article beads that point back at the original page. /Annots is copied separately.
    private static readonly HashSet<string> NotCopiedKeys = new(StringComparer.Ordinal) { "/Parent", "/StructParents", "/B", "/Annots" };

    /// <param name="baseDocument">The base source, opened for modification.</param>
    /// <param name="baseSourceId">Id of the base source in the plan.</param>
    /// <param name="plan">The pages to produce.</param>
    /// <param name="source">Opened document of another source (pages are imported from it).</param>
    /// <param name="removedPages">Receives the base pages that are no longer in the document.</param>
    /// <param name="pagePlaced">Progress callback with the number of pages placed so far.</param>
    /// <param name="cancellationToken">Checked between pages.</param>
    public static IReadOnlyList<PlacedPage> Build(
        PdfDocument baseDocument,
        string baseSourceId,
        PagePlan plan,
        Func<string, PdfDocument> source,
        out IReadOnlySet<PdfObject> removedPages,
        Action<int> pagePlaced,
        CancellationToken cancellationToken)
    {
        var original = Enumerable.Range(0, baseDocument.PageCount).Select(i => baseDocument.Pages[i]).ToArray();
        var kinds = Classify(plan, baseSourceId, original.Length, out var used);

        // 1. Drop the base pages the plan no longer shows (from the end, so indices stay valid).
        var removed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        for (var i = original.Length - 1; i >= 0; i--)
        {
            if (!used[i])
            {
                baseDocument.Pages.RemoveAt(i);
                removed.Add(original[i]);
            }
        }

        // 2. Put the kept pages in plan order (they are moved, never re-inserted: PDFsharp refuses that).
        var current = original.Where((_, i) => used[i]).ToList();
        var kept = plan.Where((_, p) => kinds[p] == Placement.Keep).Select(r => original[r.SourcePageIndex]).ToArray();
        for (var target = 0; target < kept.Length; target++)
        {
            var at = current.FindIndex(target, page => ReferenceEquals(page, kept[target]));
            if (at != target)
            {
                baseDocument.Pages.MovePage(at, target);
                current.RemoveAt(at);
                current.Insert(target, kept[target]);
            }
        }

        // 3. Insert the new pages at their final positions; kept pages are already in between, in order.
        var placed = new List<PlacedPage>(plan.Count);
        for (var p = 0; p < plan.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageRef = plan[p];
            var page = kinds[p] switch
            {
                Placement.Keep => original[pageRef.SourcePageIndex],
                Placement.Copy => baseDocument.Pages.Insert(p, CopyOf(original[pageRef.SourcePageIndex])),
                Placement.Blank => baseDocument.Pages.Insert(p, PageBoxes.Blank(pageRef.BlankSize!.Value)),
                _ => baseDocument.Pages.Insert(p, ForeignPage(source(pageRef.SourceId), pageRef)),
            };
            placed.Add(new PlacedPage(page, pageRef));
            pagePlaced(p + 1);
        }

        for (var p = 0; p < placed.Count; p++)
        {
            if (!ReferenceEquals(baseDocument.Pages[p], placed[p].Page))
            {
                throw new InvalidOperationException($"The page tree does not match the plan at page {p + 1}.");
            }
        }

        removedPages = removed;
        return placed;
    }

    private static Placement[] Classify(PagePlan plan, string baseSourceId, int basePageCount, out bool[] used)
    {
        var kinds = new Placement[plan.Count];
        used = new bool[basePageCount];
        for (var p = 0; p < plan.Count; p++)
        {
            var pageRef = plan[p];
            if (pageRef.IsBlank)
            {
                kinds[p] = Placement.Blank;
            }
            else if (pageRef.SourceId != baseSourceId)
            {
                kinds[p] = Placement.Foreign;
            }
            else if (pageRef.SourcePageIndex >= basePageCount)
            {
                throw new Core.Organizing.MaterializationException(MaterializationFailure.SourceUnreadable, baseSourceId);
            }
            else
            {
                kinds[p] = used[pageRef.SourcePageIndex] ? Placement.Copy : Placement.Keep;
                used[pageRef.SourcePageIndex] = true;
            }
        }

        return kinds;
    }

    /// <summary>
    /// A second appearance of a base page: shares the content and resources, gets its own copy of the direct values.
    /// The /Annots array lists the same annotations; <see cref="AnnotationOwnership"/> then copies them.
    /// </summary>
    private static PdfPage CopyOf(PdfPage original)
    {
        var copy = new PdfPage();
        copy.Elements.Clear();
        foreach (var key in original.Elements.Keys)
        {
            if (!NotCopiedKeys.Contains(key))
            {
                var item = original.Elements[key];
                copy.Elements[key] = item is PdfObject { IsIndirect: false } direct ? direct.Clone() : item;
            }
        }

        if (Annotations(original) is { } annotations)
        {
            copy.Elements["/Annots"] = annotations.Clone();
        }

        return copy;
    }

    private static PdfPage ForeignPage(PdfDocument document, PageRef pageRef)
        => pageRef.SourcePageIndex < document.PageCount
            ? document.Pages[pageRef.SourcePageIndex]
            : throw new Core.Organizing.MaterializationException(MaterializationFailure.SourceUnreadable, pageRef.SourceId);

    private enum Placement
    {
        Keep,
        Copy,
        Foreign,
        Blank,
    }
}
