using PdfPro.Core.Geometry;

namespace PdfPro.Operations.Comparing;

/// <summary>What a change is, from the old document to the new one.</summary>
public enum ChangeKind
{
    /// <summary>Text only in the new document (shown green).</summary>
    Added,

    /// <summary>Text only in the old document (shown red).</summary>
    Deleted,

    /// <summary>Text replaced by other text (shown yellow).</summary>
    Modified,
}

/// <summary>A highlighted place of one document: a page (0-based), a box in its PDF space, and what happened there.</summary>
public sealed record ChangeMark(int Page, PdfRect Box, ChangeKind Kind);

/// <summary>One change between the documents.</summary>
/// <param name="Kind">Added, deleted or modified.</param>
/// <param name="OldText">The old words that changed ("" when added).</param>
/// <param name="NewText">The new words ("" when deleted).</param>
/// <param name="OldPage">Where it is in the old document (0-based): the page of its words, or of the text it comes after.</param>
/// <param name="NewPage">Where it is in the new document.</param>
/// <param name="OldMarks">Its words in the old document.</param>
/// <param name="NewMarks">Its words in the new document.</param>
public sealed record DocumentChange(ChangeKind Kind, string OldText, string NewText, int OldPage, int NewPage, IReadOnlyList<ChangeMark> OldMarks, IReadOnlyList<ChangeMark> NewMarks);

/// <summary>Pages shown side by side: an old page and the new page with the same content (null when the other side has none).</summary>
public sealed record PagePair(int? OldPage, int? NewPage);

/// <summary>What to leave out of a text comparison.</summary>
/// <param name="IgnoreRunningLines">Headers and footers repeated on most pages (page numbers…): they change with every inserted page.</param>
/// <param name="IgnoreCase">Letter case.</param>
public sealed record TextCompareOptions(bool IgnoreRunningLines = true, bool IgnoreCase = false);

/// <summary>The differences of two documents' text.</summary>
/// <param name="Changes">In the order of the new document.</param>
/// <param name="Pages">The pages paired by content, in order (inserted and deleted pages on one side only).</param>
/// <param name="OldPageCount">Pages of the old document.</param>
/// <param name="NewPageCount">Pages of the new document.</param>
public sealed record TextComparison(IReadOnlyList<DocumentChange> Changes, IReadOnlyList<PagePair> Pages, int OldPageCount, int NewPageCount)
{
    public bool AreSame => Changes.Count == 0;
}
