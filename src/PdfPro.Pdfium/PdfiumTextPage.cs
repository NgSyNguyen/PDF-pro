using PDFiumCore;
using PdfPro.Core.Geometry;
using PdfPro.Core.Text;
using PdfPro.Pdfium.Native;

namespace PdfPro.Pdfium;

/// <summary>Text layer of one page (characters in content order). Gate-affine; dispose when done.</summary>
public sealed class PdfiumTextPage : IDisposable
{
    private readonly PdfiumGate _gate;
    private readonly TextPageSafeHandle _handle;

    internal PdfiumTextPage(PdfiumGate gate, TextPageSafeHandle handle)
    {
        _gate = gate;
        _handle = handle;
    }

    public int CharCount => fpdf_text.FPDFTextCountChars(Native);

    /// <summary>
    /// All characters of the page as PDFium extracts them. Not normalized: callers normalize to NFC before
    /// searching, comparing or copying (SPEC "Tiếng Việt").
    /// </summary>
    public string GetText()
    {
        var native = Native;
        var count = fpdf_text.FPDFTextCountChars(native);
        if (count <= 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[count + 1];
        var written = fpdf_text.FPDFTextGetText(native, 0, count, ref buffer[0]);
        var length = Math.Clamp(written - 1, 0, count); // written includes the terminator
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)buffer[i];
        }

        return new string(chars);
    }

    /// <summary>
    /// Every character with its box in the page's PDF user space (the loose box: the font's ascent to descent, so a
    /// line's characters share their top and bottom) and whether the extractor generated it (spaces and line ends
    /// between words and lines) or it is a hyphen ending a line. Not normalized.
    /// </summary>
    public PageText GetLayer()
    {
        var native = Native;
        var count = fpdf_text.FPDFTextCountChars(native);
        if (count <= 0)
        {
            return PageText.Empty;
        }

        var chars = new TextChar[count];
        using var box = new FS_RECTF_();
        for (var i = 0; i < count; i++)
        {
            var code = fpdf_text.FPDFTextGetUnicode(native, i);
            var generated = fpdf_text.FPDFTextIsGenerated(native, i) == 1;
            var hyphen = fpdf_text.FPDFTextIsHyphen(native, i) == 1;
            var rect = !generated && fpdf_text.FPDFTextGetLooseCharBox(native, i, box) != 0
                ? new PdfRect(box.Left, box.Bottom, box.Right, box.Top)
                : default;
            chars[i] = new TextChar(CharText(code), rect, generated, hyphen);
        }

        return new PageText(chars);
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// A character code as text. PDFium keeps UTF-16 units on Windows, so a character outside the basic plane arrives as
    /// two surrogate halves, kept as they are (together they form the character again).
    /// </summary>
    private static string CharText(uint code) => code switch
    {
        <= 0xFFFF => ((char)code).ToString(),
        <= 0x10FFFF => char.ConvertFromUtf32((int)code),
        _ => "\uFFFD",
    };

    private FpdfTextpageT Native
    {
        get
        {
            _gate.VerifyAccess();
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Native;
        }
    }
}
