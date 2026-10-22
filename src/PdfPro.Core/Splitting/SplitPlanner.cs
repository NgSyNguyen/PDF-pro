using System.Diagnostics.CodeAnalysis;
using PdfPro.Core.Organizing;
using PdfPro.Core.Text;

namespace PdfPro.Core.Splitting;

/// <summary>
/// Turns the split modes (SPEC "Tách") into output files: page ranges, every N pages, bookmarks, marked first pages,
/// blank separator pages, matched text and file size. Pure: callers supply bookmarks, detected blank pages, matches
/// and page sizes; page positions are 0-based positions in the document as displayed.
/// </summary>
public static class SplitPlanner
{
    /// <summary>
    /// "1-3, 4-10, 11-": each comma (or semicolon) separated part is one file, with the part's pages in ascending order.
    /// Parts may overlap.
    /// </summary>
    public static bool TryByRanges(
        string? text,
        int pageCount,
        [NotNullWhen(true)] out IReadOnlyList<SplitSegment>? segments,
        [NotNullWhen(false)] out PageRangeError? error)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        var parts = TextNormalizer.ToNfc(text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<SplitSegment>(parts.Length);
        foreach (var part in parts)
        {
            if (!PageRanges.TryParse(part, pageCount, out var pages, out error))
            {
                segments = null;
                return false;
            }

            result.Add(new SplitSegment(pages));
        }

        if (result.Count == 0)
        {
            segments = null;
            error = new PageRangeError(PageRangeErrorKind.Empty, text?.Trim() ?? string.Empty);
            return false;
        }

        segments = result;
        error = null;
        return true;
    }

    /// <summary>Files of <paramref name="pagesPerFile"/> pages; the last one may be shorter.</summary>
    public static IReadOnlyList<SplitSegment> EveryN(int pageCount, int pagesPerFile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pagesPerFile, 1);
        var segments = new List<SplitSegment>((pageCount + pagesPerFile - 1) / pagesPerFile);
        for (var first = 0; first < pageCount; first += pagesPerFile)
        {
            segments.Add(new SplitSegment(Run(first, Math.Min(pageCount, first + pagesPerFile) - 1)));
        }

        return segments;
    }

    /// <summary>
    /// Every start begins a file that runs up to the page before the next start. The first page always starts a file
    /// (named by a start at page 0 when there is one). When several starts name the same page the first one counts.
    /// </summary>
    public static IReadOnlyList<SplitSegment> ByStarts(int pageCount, IEnumerable<SplitStart> starts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentNullException.ThrowIfNull(starts);
        var byPage = new SortedDictionary<int, SplitStart>();
        foreach (var start in starts)
        {
            if (start.Page < 0 || start.Page >= pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(starts), start.Page, $"A start page must be between 0 and {pageCount - 1}.");
            }

            byPage.TryAdd(start.Page, start);
        }

        byPage.TryAdd(0, new SplitStart(0));
        var ordered = byPage.Values.ToArray();
        var segments = new SplitSegment[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            var last = i + 1 < ordered.Length ? ordered[i + 1].Page - 1 : pageCount - 1;
            segments[i] = new SplitSegment(Run(ordered[i].Page, last), ordered[i].Title, ordered[i].FileName);
        }

        return segments;
    }

    /// <summary>Split by marks: every marked page starts a file named as the mark says.</summary>
    public static IReadOnlyList<SplitSegment> ByMarks(PagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ByStarts(plan.Count, plan.GetSplitStarts().Select(i => new SplitStart(i, FileName: plan[i].SplitMark?.FileName)));
    }

    /// <summary>
    /// Split by bookmarks: every bookmark at <paramref name="level"/> or above starts a file named after it. When several
    /// bookmarks point to the same page the deepest one names the file (it is the most specific), then the first.
    /// Bookmarks that point nowhere in the document are ignored.
    /// </summary>
    /// <param name="bookmarks">The outline flattened in document order.</param>
    /// <param name="level">1 = top-level bookmarks only.</param>
    public static IReadOnlyList<SplitSegment> ByBookmarks(int pageCount, IReadOnlyList<SplitBookmark> bookmarks, int level)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        var starts = bookmarks
            .Select((bookmark, order) => (bookmark, order))
            .Where(b => b.bookmark.Level <= level && b.bookmark.Page >= 0 && b.bookmark.Page < pageCount)
            .OrderBy(b => b.bookmark.Page)
            .ThenByDescending(b => b.bookmark.Level)
            .ThenBy(b => b.order)
            .Select(b => new SplitStart(b.bookmark.Page, b.bookmark.Title));
        return ByStarts(pageCount, starts);
    }

    /// <summary>
    /// Split at separator pages (blank sheets put between documents): the pages between separators form the files and
    /// the separators themselves are left out. Empty when every page is a separator.
    /// </summary>
    public static IReadOnlyList<SplitSegment> BySeparators(int pageCount, IReadOnlySet<int> separators)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentNullException.ThrowIfNull(separators);
        var segments = new List<SplitSegment>();
        var first = -1;
        for (var page = 0; page <= pageCount; page++)
        {
            var isContent = page < pageCount && !separators.Contains(page);
            if (isContent && first < 0)
            {
                first = page;
            }
            else if (!isContent && first >= 0)
            {
                segments.Add(new SplitSegment(Run(first, page - 1)));
                first = -1;
            }
        }

        return segments;
    }

    /// <summary>The files without the <paramref name="excluded"/> pages (blank backs); files left empty are dropped.</summary>
    public static IReadOnlyList<SplitSegment> Without(IReadOnlyList<SplitSegment> segments, IReadOnlySet<int> excluded)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(excluded);
        return excluded.Count == 0 ? segments : [.. segments.Select(s => s.Without(excluded)).OfType<SplitSegment>()];
    }

    /// <summary>
    /// Split by size: consecutive pages are gathered while the estimated file stays within
    /// <paramref name="limitBytes"/>. A part shared by several pages (font, image) counts once per file. A page that is
    /// too big on its own still gets a file; <see cref="IsOverLimit"/> tells which.
    /// </summary>
    /// <param name="partsOf">The parts of each page (its content streams, images, fonts…).</param>
    /// <param name="overheadBytes">What every file costs besides its pages (header, catalog, cross-reference table).</param>
    public static IReadOnlyList<SplitSegment> BySize(int pageCount, Func<int, IReadOnlyList<SizedPart>> partsOf, long overheadBytes, long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        ArgumentNullException.ThrowIfNull(partsOf);
        ArgumentOutOfRangeException.ThrowIfNegative(overheadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(limitBytes, 1);
        var segments = new List<SplitSegment>();
        var counted = new HashSet<long>();
        var first = 0;
        var size = overheadBytes;
        for (var page = 0; page < pageCount; page++)
        {
            var parts = partsOf(page);
            var added = parts.Where(p => !counted.Contains(p.Id)).DistinctBy(p => p.Id).Sum(p => p.Bytes);
            if (page > first && size + added > limitBytes)
            {
                segments.Add(new SplitSegment(Run(first, page - 1)));
                first = page;
                counted.Clear();
                size = overheadBytes;
                added = parts.DistinctBy(p => p.Id).Sum(p => p.Bytes);
            }

            counted.UnionWith(parts.Select(p => p.Id));
            size += added;
        }

        segments.Add(new SplitSegment(Run(first, pageCount - 1)));
        return segments;
    }

    /// <summary>The estimated size of <paramref name="segment"/> (shared parts counted once).</summary>
    public static long EstimateSize(SplitSegment segment, Func<int, IReadOnlyList<SizedPart>> partsOf, long overheadBytes)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(partsOf);
        return overheadBytes + segment.Pages.SelectMany(partsOf).DistinctBy(p => p.Id).Sum(p => p.Bytes);
    }

    /// <summary>True for a one-page file whose estimate already exceeds the limit (it cannot be split further).</summary>
    public static bool IsOverLimit(SplitSegment segment, Func<int, IReadOnlyList<SizedPart>> partsOf, long overheadBytes, long limitBytes)
        => segment.PageCount == 1 && EstimateSize(segment, partsOf, overheadBytes) > limitBytes;

    private static int[] Run(int first, int last) => Enumerable.Range(first, last - first + 1).ToArray();
}
