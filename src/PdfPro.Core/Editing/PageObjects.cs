using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Core.Editing;

/// <summary>What a drawing of a page's content is, as PDFium numbers them (FPDF_PAGEOBJ_*).</summary>
public enum PageObjectKind
{
    Unknown = 0,
    Text = 1,
    Path = 2,
    Image = 3,
    Shading = 4,

    /// <summary>A form XObject drawn as a whole (its own drawings are not listed).</summary>
    Form = 5,
}

/// <summary>
/// One drawing of a page's content (the page's top-level page objects, in drawing order), as read for editing: where it
/// is and what it is. Text is not normalized.
/// </summary>
/// <param name="Index">Its place among the page's objects (stable while the page does not change).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Bounds">The upright box around it, in the page's PDF user space.</param>
/// <param name="Corners">The four corners of its own (possibly rotated) box, in PDF user space.</param>
/// <param name="Matrix">Its transform (for text, the text matrix).</param>
/// <param name="Fill">Its fill colour, when it has one.</param>
/// <param name="Text">What a text object writes, and with what font.</param>
/// <param name="Image">The pixel size of an image.</param>
public sealed record PageObjectInfo(
    int Index,
    PageObjectKind Kind,
    PdfRect Bounds,
    IReadOnlyList<PdfPoint> Corners,
    PdfMatrix Matrix,
    RgbColor? Fill,
    PageTextObject? Text = null,
    PageImageObject? Image = null);

/// <summary>The words of a text object and its font.</summary>
/// <param name="Text">The characters it shows.</param>
/// <param name="FontName">The font's base name ("ABCDEF+TimesNewRomanPSMT"), or empty.</param>
/// <param name="FontSize">The font size of the text object (in text space; the matrix scales it on the page).</param>
/// <param name="IsEmbedded">The font program is in the file.</param>
/// <param name="IsBold">The font is bold (weight 600 or more, or forced bold).</param>
/// <param name="IsItalic">The font is italic.</param>
/// <param name="IsSerif">The font has serifs.</param>
/// <param name="IsInvisible">The text is not painted (render mode 3: the hidden text of OCR).</param>
public sealed record PageTextObject(string Text, string FontName, double FontSize, bool IsEmbedded, bool IsBold, bool IsItalic, bool IsSerif, bool IsInvisible)
{
    /// <summary>The font size as seen on the page: <see cref="FontSize"/> scaled by the text matrix.</summary>
    public double SizeOnPage(PdfMatrix matrix) => FontSize * Math.Sqrt(Math.Abs(matrix.Determinant));
}

/// <summary>An image object: its pixel size.</summary>
public sealed record PageImageObject(int PixelWidth, int PixelHeight);
