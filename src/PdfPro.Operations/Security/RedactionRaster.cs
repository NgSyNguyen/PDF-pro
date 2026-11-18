using System.IO.Compression;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Security;

/// <summary>
/// A page as a picture, its removed areas painted over: what replaces the page in the safe redaction mode. Pixels are
/// 8-bit RGB, or grey when the page has no colour, compressed for /FlateDecode; row 0 is the top of the page as shown
/// (its /Rotate applied), covering its visible box.
/// </summary>
internal sealed record RasterPage(int PageIndex, int Width, int Height, bool IsGray, byte[] FlateData);

/// <summary>What a page shows under the areas being removed, and the page as a picture with them painted over.</summary>
internal sealed record RedactedPage(int PageIndex, IReadOnlyList<string> RemovedTexts, RasterPage? Raster);

/// <summary>Renders pages with PDFium for the safe redaction mode (on the PDFium thread).</summary>
internal static class RedactionRaster
{
    /// <summary>Dots per inch of the pictures that replace redacted pages.</summary>
    public const double Dpi = 300;

    /// <summary>Above this many pixels a page is rendered at a lower resolution (a 300 DPI A2 sheet stays below).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>The text under <paramref name="rects"/> on a page: what else must go (bookmarks, comments, metadata naming it).</summary>
    public static IReadOnlyList<string> TextUnder(PageText text, IReadOnlyList<PdfRect> rects)
    {
        var found = new List<string>();
        foreach (var rect in rects)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var character in text.Chars)
            {
                if (character.IsDrawn && Intersects(character.Box, rect))
                {
                    builder.Append(character.Text);
                }
            }

            var under = TextNormalizer.ToNfc(builder.ToString()).Trim();
            if (under.Length > 0)
            {
                found.Add(under);
            }
        }

        return found;
    }

    /// <summary>Renders a page without its annotations and paints <paramref name="rects"/> (PDF space) in <paramref name="fill"/>.</summary>
    public static RasterPage Render(PdfiumDocument document, int pageIndex, IReadOnlyList<PdfRect> rects, RgbColor fill)
    {
        var geometry = document.GetPageGeometry(pageIndex);
        var size = geometry.DisplaySize;
        var scale = Dpi / 72;
        var pixels = size.Width * scale * size.Height * scale;
        if (pixels > MaxPixels)
        {
            scale *= Math.Sqrt(MaxPixels / pixels);
        }

        using var bitmap = document.RenderPage(pageIndex, new PageRenderOptions { Scale = scale, Flags = PdfRenderFlags.None });
        var (width, height, stride) = (bitmap.Width, bitmap.Height, bitmap.Stride);
        var source = bitmap.Pixels;
        var rgb = new byte[width * height * 3];
        var isGray = true;
        for (var y = 0; y < height; y++)
        {
            var row = source.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                // BGRx → RGB.
                var (b, g, r) = (row[x * 4], row[(x * 4) + 1], row[(x * 4) + 2]);
                var at = ((y * width) + x) * 3;
                rgb[at] = r;
                rgb[at + 1] = g;
                rgb[at + 2] = b;
                isGray &= r == g && g == b;
            }
        }

        // The areas, in the bitmap's pixels (rounded outwards, one pixel more on each side so anti-aliased edges go too).
        var mapper = new PageCoordinateMapper(geometry, scale);
        foreach (var rect in rects)
        {
            var shown = mapper.PdfRectToScreen(rect);
            var left = Math.Clamp((int)Math.Floor(shown.X) - 1, 0, width);
            var top = Math.Clamp((int)Math.Floor(shown.Y) - 1, 0, height);
            var right = Math.Clamp((int)Math.Ceiling(shown.X + shown.Width) + 1, 0, width);
            var bottom = Math.Clamp((int)Math.Ceiling(shown.Y + shown.Height) + 1, 0, height);
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++)
                {
                    var at = ((y * width) + x) * 3;
                    rgb[at] = fill.R;
                    rgb[at + 1] = fill.G;
                    rgb[at + 2] = fill.B;
                }
            }
        }

        isGray &= fill.R == fill.G && fill.G == fill.B;
        var data = isGray ? GrayOf(rgb) : rgb;
        return new RasterPage(pageIndex, width, height, isGray, Compress(data));
    }

    private static bool Intersects(PdfRect a, PdfRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;

    private static byte[] GrayOf(byte[] rgb)
    {
        var gray = new byte[rgb.Length / 3];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = rgb[i * 3];
        }

        return gray;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
