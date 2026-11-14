using PdfPro.Core.Geometry;

namespace PdfPro.Operations.Comparing;

/// <summary>A highlight of a page shown in a comparison: a change's words (<see cref="Change"/> is its index) or a region that looks different.</summary>
/// <param name="Box">Where, in the page's PDF space.</param>
/// <param name="Kind">Added, deleted or modified words; null for a region found by comparing the pages as pictures.</param>
/// <param name="Change">The index of the change in <see cref="TextComparison.Changes"/>, or null for a region.</param>
public sealed record PageHighlight(PdfRect Box, ChangeKind? Kind, int? Change);

/// <summary>The highlights of a pair of pages: on the old page and on the new one.</summary>
public sealed record PairHighlights(IReadOnlyList<PageHighlight> Old, IReadOnlyList<PageHighlight> New);

/// <summary>A comparison seen pair of pages by pair of pages, as the comparison window shows it.</summary>
public static class ComparisonPages
{
    /// <summary>
    /// The pair of pages that shows a change: the one holding both its pages, else the one holding the page of its
    /// words — the new page for added and modified text, the old page for deleted text.
    /// </summary>
    /// <returns>The index in <see cref="TextComparison.Pages"/>, or -1 when no pair holds its pages.</returns>
    public static int PairOf(TextComparison comparison, DocumentChange change)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(change);
        var pairs = comparison.Pages;
        var both = Find(pairs, p => p.OldPage == change.OldPage && p.NewPage == change.NewPage);
        if (both >= 0)
        {
            return both;
        }

        var byNew = Find(pairs, p => p.NewPage == change.NewPage);
        var byOld = Find(pairs, p => p.OldPage == change.OldPage);
        return change.Kind == ChangeKind.Deleted ? (byOld >= 0 ? byOld : byNew) : (byNew >= 0 ? byNew : byOld);
    }

    /// <summary>
    /// The highlights of a pair: every change's words on its old page and on its new page, then the regions that
    /// looked different when compared as pictures (<paramref name="visual"/> in the order of the pairs, or null).
    /// </summary>
    public static PairHighlights HighlightsOf(TextComparison comparison, IReadOnlyList<VisualDifference>? visual, int pair)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentOutOfRangeException.ThrowIfNegative(pair);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pair, comparison.Pages.Count);
        var (oldPage, newPage) = (comparison.Pages[pair].OldPage, comparison.Pages[pair].NewPage);
        var old = new List<PageHighlight>();
        var @new = new List<PageHighlight>();
        for (var i = 0; i < comparison.Changes.Count; i++)
        {
            var change = comparison.Changes[i];
            old.AddRange(change.OldMarks.Where(m => m.Page == oldPage).Select(m => new PageHighlight(m.Box, m.Kind, i)));
            @new.AddRange(change.NewMarks.Where(m => m.Page == newPage).Select(m => new PageHighlight(m.Box, m.Kind, i)));
        }

        if (VisualOf(comparison, visual, pair) is { } difference)
        {
            old.AddRange(difference.OldRegions.Select(r => new PageHighlight(r, null, null)));
            @new.AddRange(difference.NewRegions.Select(r => new PageHighlight(r, null, null)));
        }

        return new PairHighlights(old, @new);
    }

    /// <summary>The visual difference of a pair: by position when the list follows the pairs, else by its pages.</summary>
    public static VisualDifference? VisualOf(TextComparison comparison, IReadOnlyList<VisualDifference>? visual, int pair)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (visual is null || pair < 0 || pair >= comparison.Pages.Count)
        {
            return null;
        }

        var pages = comparison.Pages[pair];
        return visual.Count == comparison.Pages.Count && visual[pair].Pair == pages
            ? visual[pair]
            : visual.FirstOrDefault(v => v.Pair == pages);
    }

    private static int Find(IReadOnlyList<PagePair> pairs, Func<PagePair, bool> match)
    {
        for (var i = 0; i < pairs.Count; i++)
        {
            if (match(pairs[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
