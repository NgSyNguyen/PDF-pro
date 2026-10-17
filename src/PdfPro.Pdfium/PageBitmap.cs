using PdfPro.Core.Geometry;
using PdfPro.Pdfium.Native;

namespace PdfPro.Pdfium;

/// <summary>
/// A rendered page (or part of a page) in 32-bit BGRx: byte order B, G, R, unused — the layout of WPF's Bgr32.
/// The pixels live in PDFium memory until <see cref="Dispose"/>; copy them before disposing. Reading the pixels is
/// safe from any thread, the native buffer is released on the PDFium thread.
/// </summary>
public sealed class PageBitmap : IDisposable
{
    private readonly BitmapSafeHandle _handle;
    private readonly IntPtr _scan0;

    internal PageBitmap(BitmapSafeHandle handle, IntPtr scan0, int width, int height, int stride, int pagePixelWidth, int pagePixelHeight, PixelRect region, double scale)
    {
        _handle = handle;
        _scan0 = scan0;
        Width = width;
        Height = height;
        Stride = stride;
        PagePixelWidth = pagePixelWidth;
        PagePixelHeight = pagePixelHeight;
        Region = region;
        Scale = scale;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row (may exceed Width × 4).</summary>
    public int Stride { get; }

    /// <summary>Size of the whole page at <see cref="Scale"/>; equals Width/Height unless a region was rendered.</summary>
    public int PagePixelWidth { get; }

    public int PagePixelHeight { get; }

    /// <summary>The part of the full page bitmap this bitmap holds.</summary>
    public PixelRect Region { get; }

    /// <summary>Device pixels per PDF point.</summary>
    public double Scale { get; }

    public long ByteSize => (long)Stride * Height;

    /// <summary>Pointer to the first row. Valid until disposal.</summary>
    public IntPtr Scan0
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _scan0;
        }
    }

    public unsafe ReadOnlySpan<byte> Pixels => new((void*)Scan0, checked(Stride * Height));

    /// <summary>The BGRx bytes of one pixel, for tests and diagnostics.</summary>
    public (byte B, byte G, byte R) GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        var pixels = Pixels;
        var offset = (y * Stride) + (x * 4);
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    public void Dispose() => _handle.Dispose();
}
