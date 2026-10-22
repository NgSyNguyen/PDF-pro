using System.ComponentModel.DataAnnotations;

namespace PdfPro.Operations.Merging;

/// <summary>Page sizes of the merged document.</summary>
public enum MergePageSize
{
    /// <summary>Every page keeps its size.</summary>
    Unchanged,

    /// <summary>Every page gets the size of the first page (each keeps its orientation), content scaled to fit.</summary>
    LikeFirstPage,

    /// <summary>Every page becomes A4 (each keeps its orientation), content scaled to fit.</summary>
    A4,
}

/// <param name="Pages">Pages of the file to take ("1-3, 7"); null for all.</param>
/// <param name="Title">Name of the file in bookmarks and the table of contents; null uses the file name.</param>
public sealed record MergeItem(string? Pages = null, string? Title = null);

/// <param name="Items">One entry per input file, in the same order.</param>
/// <param name="KeepBookmarks">Keeps each file's bookmarks (under its file bookmark when there is one).</param>
/// <param name="BookmarkPerFile">Adds a top-level bookmark named after each file, pointing at its first page.</param>
/// <param name="TableOfContents">Adds table of contents pages at the start, with a link to each file.</param>
/// <param name="TableOfContentsHeading">Heading of the table of contents (localized by the caller).</param>
/// <param name="PageSize">Page size normalization.</param>
/// <param name="StartFilesOnOddPages">Adds blank pages so each file starts on the front of a sheet in two-sided printing.</param>
/// <param name="Title">Document title of the result; null uses the output file name.</param>
/// <param name="OutputName">Output file name without extension, for runs on files (CLI); null uses
/// "&lt;first input&gt;-merged".</param>
public sealed record MergeParams(
    [property: Required] IReadOnlyList<MergeItem> Items,
    bool KeepBookmarks = true,
    bool BookmarkPerFile = true,
    bool TableOfContents = false,
    string? TableOfContentsHeading = null,
    MergePageSize PageSize = MergePageSize.Unchanged,
    bool StartFilesOnOddPages = false,
    string? Title = null,
    string? OutputName = null);
