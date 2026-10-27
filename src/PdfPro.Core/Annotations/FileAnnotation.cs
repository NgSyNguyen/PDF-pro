using PdfPro.Core.Geometry;

namespace PdfPro.Core.Annotations;

/// <summary>The kinds of annotation of the PDF standard (12.5.6), as PDFium numbers them.</summary>
public enum FileAnnotationKind
{
    Unknown = 0,
    Text = 1,
    Link = 2,
    FreeText = 3,
    Line = 4,
    Square = 5,
    Circle = 6,
    Polygon = 7,
    PolyLine = 8,
    Highlight = 9,
    Underline = 10,
    Squiggly = 11,
    StrikeOut = 12,
    Stamp = 13,
    Caret = 14,
    Ink = 15,
    Popup = 16,
    FileAttachment = 17,
    Sound = 18,
    Movie = 19,
    Widget = 20,
    Screen = 21,
    PrinterMark = 22,
    TrapNet = 23,
    Watermark = 24,
    ThreeD = 25,
    RichMedia = 26,
    XfaWidget = 27,
    Redact = 28,
}

/// <summary>
/// An annotation already in a file, as read for the comments list (PDF_Pro does not draw it: the page image has it).
/// Text is not normalized; callers normalize to NFC before showing, searching or comparing.
/// </summary>
/// <param name="Index">Its position in the page's /Annots (stable while the file does not change).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Rect">Where it is, in the page's PDF user space.</param>
/// <param name="Name">Its unique name (/NM), or null.</param>
/// <param name="Author">Who wrote it (/T), or null.</param>
/// <param name="Contents">Its comment (/Contents), or null.</param>
/// <param name="Modified">When it last changed (/M), or null when absent or unreadable.</param>
/// <param name="InReplyTo">The <see cref="Index"/> of the annotation it answers (/IRT), or null.</param>
/// <param name="State">A review state it sets on the annotation it answers (/State: Accepted, Rejected…), or null.</param>
/// <param name="StateModel">The model of <see cref="State"/> (/StateModel: Review, Marked), or null.</param>
/// <param name="Flags">Its /F flags (2 = hidden).</param>
/// <param name="Quads">The marked lines of text markup, or empty.</param>
public sealed record FileAnnotation(
    int Index,
    FileAnnotationKind Kind,
    PdfRect Rect,
    string? Name,
    string? Author,
    string? Contents,
    DateTimeOffset? Modified,
    int? InReplyTo,
    string? State,
    string? StateModel,
    int Flags,
    IReadOnlyList<TextQuad> Quads)
{
    /// <summary>The /F flag of annotations that must not be shown.</summary>
    public const int HiddenFlag = 2;

    /// <summary>
    /// A comment a reader writes or answers: notes, text boxes, markup, drawings, stamps, attachments. Links, form
    /// fields, pop-up windows and media are parts of the document, not comments.
    /// </summary>
    public bool IsComment => Kind is FileAnnotationKind.Text or FileAnnotationKind.FreeText or FileAnnotationKind.Line
        or FileAnnotationKind.Square or FileAnnotationKind.Circle or FileAnnotationKind.Polygon or FileAnnotationKind.PolyLine
        or FileAnnotationKind.Highlight or FileAnnotationKind.Underline or FileAnnotationKind.Squiggly or FileAnnotationKind.StrikeOut
        or FileAnnotationKind.Stamp or FileAnnotationKind.Caret or FileAnnotationKind.Ink or FileAnnotationKind.FileAttachment
        or FileAnnotationKind.Redact;

    /// <summary>An answer in another comment's conversation (a reply, or a review state set on it).</summary>
    public bool IsReply => InReplyTo is not null;

    public bool IsHidden => (Flags & HiddenFlag) != 0;
}
