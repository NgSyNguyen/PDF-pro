using PdfPro.Core.Geometry;

namespace PdfPro.Core.Imaging;

/// <summary>
/// A picture made from another one, and where each of its points comes from: <see cref="ToSource"/> maps a point of
/// <see cref="Image"/> (pixels, y down, (0, 0) the top-left corner of the top-left pixel) to the source picture.
/// </summary>
public sealed record TransformedImage(GrayImage Image, PdfMatrix ToSource);

/// <summary>Turning, shrinking and measuring grey pictures of pages.</summary>
public static class GrayTransforms
{
    /// <summary>The picture turned clockwise by 0, 90, 180 or 270 degrees (exactly, pixel for pixel).</summary>
    public static TransformedImage RotateQuarter(GrayImage image, int clockwiseDegrees)
    {
        ArgumentNullException.ThrowIfNull(image);
        var turn = ((clockwiseDegrees % 360) + 360) % 360;
        if (turn % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clockwiseDegrees), clockwiseDegrees, "Only quarter turns.");
        }

        var (w, h) = (image.Width, image.Height);
        var source = image.Pixels;
        switch (turn)
        {
            case 0:
                return new TransformedImage(image, PdfMatrix.Identity);
            case 180:
            {
                var pixels = new byte[source.Length];
                var count = (long)w * h;
                for (long i = 0; i < count; i++)
                {
                    pixels[count - 1 - i] = source[i];
                }

                return new TransformedImage(new GrayImage(w, h, pixels), new PdfMatrix(-1, 0, 0, -1, w, h));
            }

            case 90:
            {
                // New (x', y') shows old (y', h - 1 - x').
                var pixels = new byte[source.Length];
                for (var y = 0; y < w; y++)
                {
                    for (var x = 0; x < h; x++)
                    {
                        pixels[(y * h) + x] = source[((h - 1 - x) * w) + y];
                    }
                }

                return new TransformedImage(new GrayImage(h, w, pixels), new PdfMatrix(0, -1, 1, 0, 0, h));
            }

            default:
            {
                // 270: new (x', y') shows old (w - 1 - y', x').
                var pixels = new byte[source.Length];
                for (var y = 0; y < w; y++)
                {
                    for (var x = 0; x < h; x++)
                    {
                        pixels[(y * h) + x] = source[(x * w) + (w - 1 - y)];
                    }
                }

                return new TransformedImage(new GrayImage(h, w, pixels), new PdfMatrix(0, 1, -1, 0, w, 0));
            }
        }
    }

    /// <summary>
    /// The picture turned clockwise by any angle around its centre (bilinear), on a canvas large enough to hold all of
    /// it; what the canvas adds is white.
    /// </summary>
    public static TransformedImage Rotate(GrayImage image, double clockwiseDegrees)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (Math.Abs(clockwiseDegrees) < 1e-9)
        {
            return new TransformedImage(image, PdfMatrix.Identity);
        }

        var (sin, cos) = Math.SinCos(clockwiseDegrees * Math.PI / 180);
        var (w, h) = (image.Width, image.Height);
        var newWidth = (int)Math.Ceiling((Math.Abs(w * cos) + Math.Abs(h * sin)) - 1e-6);
        var newHeight = (int)Math.Ceiling((Math.Abs(w * sin) + Math.Abs(h * cos)) - 1e-6);
        double cx = w / 2.0, cy = h / 2.0, ncx = newWidth / 2.0, ncy = newHeight / 2.0;

        // Old = centre + R(-θ)·(new - new centre); y points down, so R(θ) turns clockwise on screen.
        var toSource = new PdfMatrix(cos, -sin, sin, cos, cx - ((ncx * cos) + (ncy * sin)), cy - ((-ncx * sin) + (ncy * cos)));
        var pixels = new byte[(long)newWidth * newHeight];
        var source = image.Pixels;
        Parallel.For(0, newHeight, y =>
        {
            var row = y * newWidth;
            for (var x = 0; x < newWidth; x++)
            {
                // Sample at the pixel centre; the source pixel (i, j) has its centre at (i + 0.5, j + 0.5).
                var sx = (toSource.A * (x + 0.5)) + (toSource.C * (y + 0.5)) + toSource.E - 0.5;
                var sy = (toSource.B * (x + 0.5)) + (toSource.D * (y + 0.5)) + toSource.F - 0.5;
                pixels[row + x] = Sample(source, w, h, sx, sy);
            }
        });

        return new TransformedImage(new GrayImage(newWidth, newHeight, pixels), toSource);
    }

    /// <summary>The picture shrunk (area averaging) so it is at most <paramref name="maxWidth"/> pixels wide.</summary>
    public static TransformedImage ShrinkToWidth(GrayImage image, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        if (image.Width <= maxWidth)
        {
            return new TransformedImage(image, PdfMatrix.Identity);
        }

        var newWidth = maxWidth;
        var newHeight = Math.Max(1, (int)Math.Round(image.Height * (double)maxWidth / image.Width));
        var pixels = AreaResampler.Shrink(image.Pixels, image.Width, image.Height, 1, newWidth, newHeight);
        return new TransformedImage(
            new GrayImage(newWidth, newHeight, pixels),
            PdfMatrix.Scaling((double)image.Width / newWidth, (double)image.Height / newHeight));
    }

    /// <summary>The number of pixels of each grey level.</summary>
    public static long[] Histogram(GrayImage image, PixelRect? area = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rect = area ?? new PixelRect(0, 0, image.Width, image.Height);
        var counts = new long[256];
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            var row = image.Row(y).Slice(rect.X, rect.Width);
            foreach (var value in row)
            {
                counts[value]++;
            }
        }

        return counts;
    }

    /// <summary>Otsu's threshold of a histogram: the level splitting it into the two most distinct classes.</summary>
    public static int OtsuThreshold(long[] histogram)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        long total = 0;
        double sum = 0;
        for (var i = 0; i < histogram.Length; i++)
        {
            total += histogram[i];
            sum += i * (double)histogram[i];
        }

        if (total == 0)
        {
            return 128;
        }

        double sumBelow = 0, best = -1;
        long below = 0;
        var threshold = 128;
        for (var t = 0; t < histogram.Length; t++)
        {
            below += histogram[t];
            if (below == 0)
            {
                continue;
            }

            var above = total - below;
            if (above == 0)
            {
                break;
            }

            sumBelow += t * (double)histogram[t];
            var meanBelow = sumBelow / below;
            var meanAbove = (sum - sumBelow) / above;
            var between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = t + 1;
            }
        }

        return threshold;
    }

    /// <summary>The grey level below which <paramref name="share"/> (0–1) of the pixels of a histogram lie.</summary>
    public static int Percentile(long[] histogram, double share)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        var total = histogram.Sum();
        var target = (long)Math.Ceiling(total * Math.Clamp(share, 0, 1));
        long seen = 0;
        for (var level = 0; level < histogram.Length; level++)
        {
            seen += histogram[level];
            if (seen >= target && seen > 0)
            {
                return level;
            }
        }

        return histogram.Length - 1;
    }

    private static byte Sample(byte[] source, int width, int height, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        double At(int px, int py) => px < 0 || py < 0 || px >= width || py >= height ? 255 : source[(py * width) + px];
        var top = (At(x0, y0) * (1 - fx)) + (At(x0 + 1, y0) * fx);
        var bottom = (At(x0, y0 + 1) * (1 - fx)) + (At(x0 + 1, y0 + 1) * fx);
        return (byte)Math.Clamp(Math.Round((top * (1 - fy)) + (bottom * fy)), 0, 255);
    }
}
