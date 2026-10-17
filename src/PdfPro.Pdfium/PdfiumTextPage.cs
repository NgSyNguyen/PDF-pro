using PDFiumCore;
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

    public void Dispose() => _handle.Dispose();

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
