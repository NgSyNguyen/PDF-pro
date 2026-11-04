using System.Runtime.InteropServices;
using System.Text;
using PDFiumCore;
using PdfPro.Core.Editing;
using PdfPro.Core.Imaging;

namespace PdfPro.Pdfium;

/// <summary>Taking images out of pages at the resolution they are stored in.</summary>
public sealed partial class PdfiumDocument
{
    /// <summary>Largest image data copied out (bytes), against hostile files.</summary>
    private const long MaxImageBytes = 512L << 20;

    /// <summary>
    /// The image object <paramref name="target"/> of page <paramref name="pageIndex"/> as stored: JPEG (DCTDecode) and
    /// JPEG 2000 (JPXDecode) data as they are, anything else decoded to its pixels and written as PNG.
    /// </summary>
    /// <exception cref="InvalidDataException">The object is not on the page any more, or its data cannot be read.</exception>
    public ExtractedImage ExtractImage(int pageIndex, PageObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CheckPageIndex(pageIndex);
        var page = GetCachedPage(pageIndex).Native;
        var item = Find(page, target);
        uint width = 0, height = 0;
        _ = fpdf_edit.FPDFImageObjGetImagePixelSize(item, ref width, ref height);
        var filters = Enumerable.Range(0, Math.Max(0, fpdf_edit.FPDFImageObjGetImageFilterCount(item))).Select(i => Filter(item, i)).ToList();
        if (filters is ["DCTDecode"] or ["JPXDecode"])
        {
            var data = Read((buffer, length) => fpdf_edit.FPDFImageObjGetImageDataRaw(item, buffer, length));
            return new ExtractedImage(filters[0] == "DCTDecode" ? ".jpg" : ".jp2", data, (int)width, (int)height);
        }

        var pixels = Pixels(item);
        return new ExtractedImage(".png", PngCodec.Encode(pixels), pixels.Width, pixels.Height);
    }

    private static string Filter(FpdfPageobjectT item, int index)
    {
        var length = fpdf_edit.FPDFImageObjGetImageFilter(item, index, IntPtr.Zero, 0);
        if (length <= 1 || length > 256)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal((nint)length);
        try
        {
            var written = fpdf_edit.FPDFImageObjGetImageFilter(item, index, buffer, length);
            return Marshal.PtrToStringAnsi(buffer, (int)Math.Min(written, length) - 1);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Read(Func<IntPtr, ulong, ulong> read)
    {
        var length = read(IntPtr.Zero, 0);
        if (length == 0 || length > MaxImageBytes)
        {
            throw new InvalidDataException("The image data is empty or too large.");
        }

        var data = new byte[length];
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var written = read(handle.AddrOfPinnedObject(), length);
            return written == length ? data : throw new InvalidDataException("The image data could not be read.");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>The image's own pixels (not scaled, without its mask) as BGRA.</summary>
    private static BgraImage Pixels(FpdfPageobjectT item)
    {
        var bitmap = fpdf_edit.FPDFImageObjGetBitmap(item);
        if (bitmap is null || bitmap.__Instance == IntPtr.Zero)
        {
            throw new InvalidDataException("The image cannot be decoded.");
        }

        try
        {
            var width = fpdfview.FPDFBitmapGetWidth(bitmap);
            var height = fpdfview.FPDFBitmapGetHeight(bitmap);
            var stride = fpdfview.FPDFBitmapGetStride(bitmap);
            var format = fpdfview.FPDFBitmapGetFormat(bitmap); // 1 grey, 2 BGR, 3 BGRx, 4 BGRA
            var source = fpdfview.FPDFBitmapGetBuffer(bitmap);
            if (width <= 0 || height <= 0 || (long)width * height > PngCodec.MaxPixels || source == IntPtr.Zero)
            {
                throw new InvalidDataException("The image has no size or is too large.");
            }

            var row = new byte[stride];
            var pixels = new byte[(long)width * height * 4];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(source + (y * stride), row, 0, stride);
                for (var x = 0; x < width; x++)
                {
                    var to = (((long)y * width) + x) * 4;
                    switch (format)
                    {
                        case 1:
                            pixels[to] = pixels[to + 1] = pixels[to + 2] = row[x];
                            pixels[to + 3] = 255;
                            break;
                        case 2:
                            pixels[to] = row[x * 3];
                            pixels[to + 1] = row[(x * 3) + 1];
                            pixels[to + 2] = row[(x * 3) + 2];
                            pixels[to + 3] = 255;
                            break;
                        default:
                            pixels[to] = row[x * 4];
                            pixels[to + 1] = row[(x * 4) + 1];
                            pixels[to + 2] = row[(x * 4) + 2];
                            pixels[to + 3] = format == 4 ? row[(x * 4) + 3] : (byte)255;
                            break;
                    }
                }
            }

            return new BgraImage(width, height, pixels);
        }
        finally
        {
            fpdfview.FPDFBitmapDestroy(bitmap);
        }
    }
}
