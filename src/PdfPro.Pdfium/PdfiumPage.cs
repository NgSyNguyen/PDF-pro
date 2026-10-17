using PDFiumCore;
using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Native;

namespace PdfPro.Pdfium;

/// <summary>A loaded page. Gate-affine: use only on the PDFium thread, dispose when done.</summary>
public sealed class PdfiumPage : IDisposable
{
    private readonly PdfiumGate _gate;
    private readonly PageSafeHandle _handle;

    internal PdfiumPage(PdfiumGate gate, PageSafeHandle handle, int index)
    {
        _gate = gate;
        _handle = handle;
        Index = index;
    }

    public int Index { get; }

    internal FpdfPageT Native
    {
        get
        {
            _gate.VerifyAccess();
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle.Native;
        }
    }

    internal PageSafeHandle Handle => _handle;

    /// <summary>Size in points as displayed (after /Rotate, limited to the visible box).</summary>
    public PdfSize DisplaySize
    {
        get
        {
            var native = Native;
            return new PdfSize(fpdfview.FPDF_GetPageWidthF(native), fpdfview.FPDF_GetPageHeightF(native));
        }
    }

    /// <summary>The visible box exactly as PDFium displays it (CropBox ∩ MediaBox) and the page's /Rotate.</summary>
    public PageGeometry Geometry
    {
        get
        {
            var native = Native;
            using var box = new FS_RECTF_();
            var rotation = fpdf_edit.FPDFPageGetRotation(native) * 90;
            if (fpdfview.FPDF_GetPageBoundingBox(native, box) == 0)
            {
                // Should not happen for a loaded page; fall back to the display size at the origin.
                var size = DisplaySize;
                var unrotated = rotation is 90 or 270 ? new PdfSize(size.Height, size.Width) : size;
                return new PageGeometry(new PdfRect(0, 0, unrotated.Width, unrotated.Height), rotation);
            }

            return new PageGeometry(new PdfRect(box.Left, box.Bottom, box.Right, box.Top), rotation);
        }
    }

    /// <summary>The raw /MediaBox, or null when the page dictionary has none.</summary>
    public PdfRect? MediaBox
    {
        get
        {
            float left = 0, bottom = 0, right = 0, top = 0;
            return fpdf_transformpage.FPDFPageGetMediaBox(Native, ref left, ref bottom, ref right, ref top) != 0
                ? new PdfRect(left, bottom, right, top)
                : null;
        }
    }

    /// <summary>The raw /CropBox, or null when the page dictionary has none.</summary>
    public PdfRect? CropBox
    {
        get
        {
            float left = 0, bottom = 0, right = 0, top = 0;
            return fpdf_transformpage.FPDFPageGetCropBox(Native, ref left, ref bottom, ref right, ref top) != 0
                ? new PdfRect(left, bottom, right, top)
                : null;
        }
    }

    /// <summary>PDFium's own page→device mapping; used by tests to validate PageCoordinateMapper.</summary>
    internal (int X, int Y) PageToDevice(int sizeX, int sizeY, int quarterTurns, double x, double y)
    {
        int deviceX = 0, deviceY = 0;
        fpdfview.FPDF_PageToDevice(Native, 0, 0, sizeX, sizeY, quarterTurns, x, y, ref deviceX, ref deviceY);
        return (deviceX, deviceY);
    }

    /// <summary>Loads the text layer of the page. Dispose the result.</summary>
    public PdfiumTextPage LoadText()
    {
        var native = fpdf_text.FPDFTextLoadPage(Native);
        if (native is null || native.__Instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDFium could not load the text layer of the page.");
        }

        return new PdfiumTextPage(_gate, new TextPageSafeHandle(_gate, native, _handle));
    }

    public void Dispose() => _handle.Dispose();
}
