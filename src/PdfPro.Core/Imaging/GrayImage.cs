namespace PdfPro.Core.Imaging;

/// <summary>
/// An 8-bit grey picture: rows top to bottom, one byte per pixel, 0 black and 255 white. The picture of a page that
/// OCR reads and the scan tools look at.
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.LongLength < (long)width * height)
        {
            throw new ArgumentException("The pixels do not fill the picture.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Width × Height bytes, row after row.</summary>
    public byte[] Pixels { get; }

    /// <summary>A white picture.</summary>
    public static GrayImage White(int width, int height)
    {
        var pixels = new byte[(long)width * height];
        Array.Fill(pixels, (byte)255);
        return new GrayImage(width, height, pixels);
    }

    /// <summary>The grey of BGRA pixels (ITU-R BT.601 luma, alpha over white).</summary>
    public static GrayImage FromBgra(int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);
        var pixels = new byte[(long)width * height];
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            var at = y * width;
            for (var x = 0; x < width; x++)
            {
                var b = row[x * 4];
                var g = row[(x * 4) + 1];
                var r = row[(x * 4) + 2];
                var a = row[(x * 4) + 3];
                var luma = ((299 * r) + (587 * g) + (114 * b) + 500) / 1000;
                pixels[at + x] = (byte)(((luma * a) + (255 * (255 - a)) + 127) / 255);
            }
        }

        return new GrayImage(width, height, pixels);
    }

    /// <summary>The pixel at (x, y).</summary>
    public byte this[int x, int y] => Pixels[(y * Width) + x];

    /// <summary>One row of pixels.</summary>
    public ReadOnlySpan<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);
}
