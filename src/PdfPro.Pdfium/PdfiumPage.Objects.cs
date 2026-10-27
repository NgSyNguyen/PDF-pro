using System.Text;
using PDFiumCore;
using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;

namespace PdfPro.Pdfium;

/// <summary>The drawings of a page's content (page objects), read for editing.</summary>
public sealed partial class PdfiumPage
{
    /// <summary>Upper bound for the objects listed of one page (defends against hostile files).</summary>
    private const int MaxObjects = 200_000;

    /// <summary>Upper bound for the characters of one text object.</summary>
    private const int MaxObjectTextBytes = 1 << 20;

    /// <summary>Font descriptor flags (9.8.2): Serif, Italic, ForceBold.</summary>
    private const int SerifFlag = 1 << 1;
    private const int ItalicFlag = 1 << 6;
    private const int ForceBoldFlag = 1 << 18;

    /// <summary>
    /// The page's top-level objects in drawing order: kind, place, transform, fill colour; for text its characters and
    /// font, for images their pixel size. Objects without a box (empty paths, clipping-only text) are skipped.
    /// </summary>
    public IReadOnlyList<PageObjectInfo> GetObjects()
    {
        var native = Native;
        var count = Math.Min(fpdf_edit.FPDFPageCountObjects(native), MaxObjects);
        var result = new List<PageObjectInfo>(Math.Max(0, count));
        PdfiumTextPage? text = null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var item = fpdf_edit.FPDFPageGetObject(native, i);
                if (item is null || item.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                float left = 0, bottom = 0, right = 0, top = 0;
                if (fpdf_edit.FPDFPageObjGetBounds(item, ref left, ref bottom, ref right, ref top) == 0)
                {
                    continue;
                }

                var kind = fpdf_edit.FPDFPageObjGetType(item) is var type && Enum.IsDefined((PageObjectKind)type) ? (PageObjectKind)type : PageObjectKind.Unknown;
                var bounds = new PdfRect(left, bottom, right, top);
                PageTextObject? words = null;
                PageImageObject? image = null;
                if (kind == PageObjectKind.Text)
                {
                    text ??= LoadText();
                    words = ReadTextObject(item, text.Native);
                }
                else if (kind == PageObjectKind.Image)
                {
                    uint width = 0, height = 0;
                    if (fpdf_edit.FPDFImageObjGetImagePixelSize(item, ref width, ref height) != 0)
                    {
                        image = new PageImageObject((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));
                    }
                }

                result.Add(new PageObjectInfo(i, kind, bounds, Corners(item, bounds), Matrix(item), Fill(item), words, image));
            }
        }
        finally
        {
            text?.Dispose();
        }

        return result;
    }

    internal static PdfMatrix Matrix(FpdfPageobjectT item)
    {
        using var matrix = new FS_MATRIX_();
        return fpdf_edit.FPDFPageObjGetMatrix(item, matrix) != 0
            ? new PdfMatrix(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F)
            : PdfMatrix.Identity;
    }

    internal static RgbColor? Fill(FpdfPageobjectT item)
    {
        uint red = 0, green = 0, blue = 0, alpha = 0;
        return fpdf_edit.FPDFPageObjGetFillColor(item, ref red, ref green, ref blue, ref alpha) != 0
            ? new RgbColor((byte)Math.Min(red, 255), (byte)Math.Min(green, 255), (byte)Math.Min(blue, 255))
            : null;
    }

    internal static PageTextObject ReadTextObject(FpdfPageobjectT item, FpdfTextpageT text)
    {
        var font = fpdf_edit.FPDFTextObjGetFont(item);
        float size = 0;
        _ = fpdf_edit.FPDFTextObjGetFontSize(item, ref size);
        var hasFont = font is not null && font.__Instance != IntPtr.Zero;
        var flags = hasFont ? fpdf_edit.FPDFFontGetFlags(font) : 0;
        var weight = hasFont ? fpdf_edit.FPDFFontGetWeight(font) : -1;
        return new PageTextObject(
            ObjectText(item, text),
            hasFont ? FontName(font!) : string.Empty,
            size,
            hasFont && fpdf_edit.FPDFFontGetIsEmbedded(font) != 0,
            weight >= 600 || (flags & ForceBoldFlag) != 0,
            (flags & ItalicFlag) != 0,
            (flags & SerifFlag) != 0,
            fpdf_edit.FPDFTextObjGetTextRenderMode(item) == FPDF_TEXT_RENDERMODE.FPDF_TEXTRENDERMODE_INVISIBLE);
    }

    private static IReadOnlyList<PdfPoint> Corners(FpdfPageobjectT item, PdfRect bounds)
    {
        using var quad = new FS_QUADPOINTSF();
        return fpdf_edit.FPDFPageObjGetRotatedBounds(item, quad) != 0
            ? [new PdfPoint(quad.X1, quad.Y1), new PdfPoint(quad.X2, quad.Y2), new PdfPoint(quad.X3, quad.Y3), new PdfPoint(quad.X4, quad.Y4)]
            : [new PdfPoint(bounds.Left, bounds.Bottom), new PdfPoint(bounds.Right, bounds.Bottom), new PdfPoint(bounds.Right, bounds.Top), new PdfPoint(bounds.Left, bounds.Top)];
    }

    private static string ObjectText(FpdfPageobjectT item, FpdfTextpageT text)
    {
        ushort probe = 0;
        var length = (long)fpdf_edit.FPDFTextObjGetText(item, text, ref probe, 0);
        if (length <= 2 || length > MaxObjectTextBytes)
        {
            return string.Empty;
        }

        var buffer = new ushort[(length + 1) / 2];
        var written = (long)fpdf_edit.FPDFTextObjGetText(item, text, ref buffer[0], (ulong)length);
        var chars = (int)(Math.Min(written, length) / 2) - 1; // drop the terminator
        if (chars <= 0)
        {
            return string.Empty;
        }

        var result = new char[chars];
        for (var i = 0; i < chars; i++)
        {
            result[i] = (char)buffer[i];
        }

        return new string(result);
    }

    private static unsafe string FontName(FpdfFontT font)
    {
        var length = (long)fpdf_edit.FPDFFontGetBaseFontName(font, null, 0);
        if (length <= 1 || length > 1024)
        {
            return string.Empty;
        }

        var buffer = new sbyte[length];
        fixed (sbyte* pointer = buffer)
        {
            var written = (long)fpdf_edit.FPDFFontGetBaseFontName(font, pointer, (ulong)length);
            var bytes = (int)Math.Min(written, length) - 1; // drop the terminator
            return bytes <= 0 ? string.Empty : Encoding.Latin1.GetString((byte*)pointer, bytes);
        }
    }
}
