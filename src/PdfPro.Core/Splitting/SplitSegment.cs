namespace PdfPro.Core.Splitting;

/// <summary>One output file of a split: its pages, and what its name can be made of.</summary>
public sealed record SplitSegment
{
    /// <param name="pages">0-based page positions in the document, in output order; at least one.</param>
    /// <param name="title">Text the naming pattern may use: the bookmark title or the matched text; null when none.</param>
    /// <param name="fileName">The file name the user chose for this file (overrides the pattern), or null.</param>
    public SplitSegment(IReadOnlyList<int> pages, string? title = null, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            throw new ArgumentException("A file needs at least one page.", nameof(pages));
        }

        foreach (var page in pages)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(page, nameof(pages));
        }

        Pages = pages;
        Title = string.IsNullOrWhiteSpace(title) ? null : title;
        FileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    public IReadOnlyList<int> Pages { get; }

    public string? Title { get; }

    public string? FileName { get; }

    public int First => Pages[0];

    public int Last => Pages[^1];

    public int PageCount => Pages.Count;

    /// <summary>The same pages and name, with only the pages not in <paramref name="excluded"/>; null when none is left.</summary>
    public SplitSegment? Without(IReadOnlySet<int> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        var kept = Pages.Where(p => !excluded.Contains(p)).ToArray();
        return kept.Length == Pages.Count ? this : kept.Length == 0 ? null : new SplitSegment(kept, Title, FileName);
    }
}

/// <summary>A page that starts a new file, with the text its file may be named after.</summary>
/// <param name="Page">0-based position.</param>
/// <param name="Title">Bookmark title or matched text, or null.</param>
/// <param name="FileName">A file name chosen by the user, or null.</param>
public readonly record struct SplitStart(int Page, string? Title = null, string? FileName = null);

/// <summary>A bookmark flattened for splitting.</summary>
/// <param name="Page">0-based position of its target in the document, or -1 when it points nowhere in it.</param>
/// <param name="Level">1 for top-level bookmarks, 2 for their children…</param>
/// <param name="Title">Its title (already normalized).</param>
public readonly record struct SplitBookmark(int Page, int Level, string Title);

/// <summary>A piece of a page's content counted once per output file (a stream shared by several pages).</summary>
/// <param name="Id">Identifies the piece within the document (object number).</param>
/// <param name="Bytes">Its size in the file.</param>
public readonly record struct SizedPart(long Id, long Bytes);
