using PdfPro.Core.Geometry;
using PdfPro.Core.Organizing;

namespace PdfPro.Core.Merging;

/// <summary>One file of a merge, as far as the page order is concerned.</summary>
public sealed record MergeFile
{
    /// <param name="sourceId">Its source id in the merged plan ("s0", "s1"…).</param>
    /// <param name="pages">0-based pages of the file to take, in order; at least one.</param>
    /// <param name="pageSizes">Displayed size of every page of the file.</param>
    public MergeFile(string sourceId, IReadOnlyList<int> pages, IReadOnlyList<PdfSize> pageSizes)
    {
        if (!SourceIds.IsValid(sourceId) || sourceId == SourceIds.Blank)
        {
            throw new ArgumentException("A merged file needs a source id.", nameof(sourceId));
        }

        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(pageSizes);
        if (pages.Count == 0)
        {
            throw new ArgumentException("A merged file contributes at least one page.", nameof(pages));
        }

        if (pages.Any(p => p < 0 || p >= pageSizes.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(pages), "A page is not in the file.");
        }

        SourceId = sourceId;
        Pages = pages;
        PageSizes = pageSizes;
    }

    public string SourceId { get; }

    public IReadOnlyList<int> Pages { get; }

    public IReadOnlyList<PdfSize> PageSizes { get; }
}

/// <summary>The merged document's pages and where each part starts.</summary>
/// <param name="Plan">Table of contents pages (blank, drawn later), then each file's pages, with blank pages so that
/// files start on odd pages when asked.</param>
/// <param name="TableOfContentsPages">How many pages at the start are the table of contents.</param>
/// <param name="FirstPositions">Position in <paramref name="Plan"/> of each file's first page.</param>
/// <param name="AddedBlankPages">Blank pages added so that files start on odd pages.</param>
public sealed record MergeLayout(PagePlan Plan, int TableOfContentsPages, IReadOnlyList<int> FirstPositions, int AddedBlankPages);

/// <summary>Page order of a merge (SPEC "Gộp"): pure, so the dialog can preview the page count before writing.</summary>
public static class MergePlanner
{
    /// <param name="files">The files in merge order.</param>
    /// <param name="tableOfContentsPages">Pages to reserve at the start for a table of contents (0 for none).</param>
    /// <param name="tableOfContentsSize">Size of those pages.</param>
    /// <param name="startFilesOnOddPages">Adds a blank page before a file that would start on an even page, so each
    /// file begins on the front of a sheet when printed on both sides.</param>
    /// <param name="keys">Keys of the new pages.</param>
    /// <param name="blankSize">Size a padding blank page gets from the page before it (page size normalization);
    /// null keeps that page's size.</param>
    public static MergeLayout Plan(
        IReadOnlyList<MergeFile> files,
        int tableOfContentsPages,
        PdfSize tableOfContentsSize,
        bool startFilesOnOddPages,
        IPageKeyGenerator keys,
        Func<PdfSize, PdfSize>? blankSize = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfNegative(tableOfContentsPages);
        if (files.Count == 0)
        {
            throw new ArgumentException("A merge needs at least one file.", nameof(files));
        }

        var pages = new List<PageRef>();
        for (var i = 0; i < tableOfContentsPages; i++)
        {
            pages.Add(PageRef.Blank(tableOfContentsSize, keys.NewKey()));
        }

        var previousSize = tableOfContentsSize;
        var firstPositions = new int[files.Count];
        var added = 0;
        for (var f = 0; f < files.Count; f++)
        {
            var file = files[f];
            if (startFilesOnOddPages && pages.Count % 2 == 1)
            {
                pages.Add(PageRef.Blank(blankSize?.Invoke(previousSize) ?? previousSize, keys.NewKey()));
                added++;
            }

            firstPositions[f] = pages.Count;
            foreach (var page in file.Pages)
            {
                pages.Add(new PageRef(file.SourceId, page, 0, null, keys.NewKey()));
            }

            previousSize = file.PageSizes[file.Pages[^1]];
        }

        return new MergeLayout(PagePlan.Create(pages), tableOfContentsPages, firstPositions, added);
    }

    /// <summary>Pages a table of contents of <paramref name="entries"/> lines needs.</summary>
    public static int TableOfContentsPageCount(int entries, int entriesPerPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(entriesPerPage, 1);
        return (entries + entriesPerPage - 1) / entriesPerPage;
    }

    /// <summary>
    /// The size a page gets when sizes are normalized to <paramref name="target"/>: the target turned to the page's
    /// orientation, so landscape pages stay landscape.
    /// </summary>
    public static PdfSize Normalize(PdfSize page, PdfSize target)
    {
        var (shortSide, longSide) = (Math.Min(target.Width, target.Height), Math.Max(target.Width, target.Height));
        return page.Width > page.Height ? new PdfSize(longSide, shortSide) : new PdfSize(shortSide, longSide);
    }
}
