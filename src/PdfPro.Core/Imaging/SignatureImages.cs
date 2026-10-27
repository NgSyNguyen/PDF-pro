namespace PdfPro.Core.Imaging;

/// <summary>A rectangle of pixels.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Pixel work on photographed or scanned signatures, on 32-bit BGRA pixels (straight alpha, 4 bytes per pixel, rows
/// without padding): the paper becomes transparent and the picture is cut down to the ink.
/// </summary>
public static class SignatureImages
{
    /// <summary>
    /// Pixels as light as paper become transparent: at or above <paramref name="threshold"/> (luminance 0–255) fully,
    /// and within <paramref name="softness"/> below it partly, so the edges of strokes stay smooth.
    /// </summary>
    public static void MakeWhiteTransparent(Span<byte> bgra, byte threshold = 225, byte softness = 40)
    {
        if (bgra.Length % 4 != 0)
        {
            throw new ArgumentException("Pixels are 4 bytes each.", nameof(bgra));
        }

        var low = threshold - softness;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var luminance = ((299 * bgra[i + 2]) + (587 * bgra[i + 1]) + (114 * bgra[i])) / 1000;
            if (luminance >= threshold)
            {
                bgra[i + 3] = 0;
            }
            else if (luminance > low && softness > 0)
            {
                bgra[i + 3] = (byte)(bgra[i + 3] * (threshold - luminance) / softness);
            }
        }
    }

    /// <summary>The smallest rectangle holding every pixel more opaque than <paramref name="minimumAlpha"/>, or null when there is none.</summary>
    public static PixelBounds? OpaqueBounds(ReadOnlySpan<byte> bgra, int width, int height, byte minimumAlpha = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Fewer pixels than width × height.", nameof(bgra));
        }

        var (left, top, right, bottom) = (width, height, -1, -1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bgra[(((y * width) + x) * 4) + 3] > minimumAlpha)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : new PixelBounds(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The pixels of <paramref name="bounds"/>, grown by <paramref name="margin"/> within the picture.</summary>
    public static (byte[] Pixels, PixelBounds Bounds) Crop(ReadOnlySpan<byte> bgra, int width, int height, PixelBounds bounds, int margin = 0)
    {
        var x = Math.Max(0, bounds.X - margin);
        var y = Math.Max(0, bounds.Y - margin);
        var right = Math.Min(width, bounds.X + bounds.Width + margin);
        var bottom = Math.Min(height, bounds.Y + bounds.Height + margin);
        var result = new PixelBounds(x, y, right - x, bottom - y);
        var pixels = new byte[result.Width * result.Height * 4];
        for (var row = 0; row < result.Height; row++)
        {
            bgra.Slice((((y + row) * width) + x) * 4, result.Width * 4).CopyTo(pixels.AsSpan(row * result.Width * 4));
        }

        return (pixels, result);
    }
}
