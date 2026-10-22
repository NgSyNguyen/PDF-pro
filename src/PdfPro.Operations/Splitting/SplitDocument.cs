using PdfPro.Core.Organizing;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Splitting;

/// <summary>
/// The document to split, as displayed: its page plan, the open source documents (read for blank pages, text and
/// bookmarks) and their files (written by the materializer). The split never changes any of them.
/// </summary>
public sealed record SplitDocument
{
    public required PagePlan Plan { get; init; }

    /// <summary>The source whose bookmarks, metadata and forms the files keep.</summary>
    public required string BaseSourceId { get; init; }

    /// <summary>Every source of the plan, opened with PDFium, by source id. Owned by the caller.</summary>
    public required IReadOnlyDictionary<string, IPdfDocument> Documents { get; init; }

    /// <summary>Every source's file, by source id.</summary>
    public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }

    /// <summary>The document's file name without extension ({name} in naming patterns).</summary>
    public required string Name { get; init; }

    /// <summary>Folder for intermediate files.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The PDFium document and page index behind position <paramref name="index"/>; null for a blank page.</summary>
    internal (IPdfDocument Document, int PageIndex)? SourceOf(int index)
    {
        var page = Plan[index];
        return page.IsBlank ? null : (Documents[page.SourceId], page.SourcePageIndex);
    }
}
