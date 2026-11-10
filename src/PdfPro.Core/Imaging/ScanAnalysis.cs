using PdfPro.Core.Geometry;

namespace PdfPro.Core.Imaging;

/// <summary>How far each edge of a picture is covered by a dark band (scanner border, shadow of a book's spine), in pixels.</summary>
public readonly record struct PixelInsets(int Left, int Top, int Right, int Bottom)
{
    public static PixelInsets None => default;

    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;

    /// <summary>What is left of a picture of this size inside the bands.</summary>
    public PixelRect Inside(int width, int height) => new(Left, Top, Math.Max(0, width - Left - Right), Math.Max(0, height - Top - Bottom));
}

/// <summary>The tilt of a page's lines of text.</summary>
/// <param name="Degrees">The angle the lines rise to the right (counterclockwise on screen); turn the picture clockwise by it to straighten it.</param>
/// <param name="Confidence">How clearly the lines stand out at that angle (0–1; see <see cref="SkewDetector.ReliableConfidence"/>).</param>
public sealed record SkewEstimate(double Degrees, double Confidence)
{
    /// <summary>Whether the estimate is clear enough to straighten the page by.</summary>
    public bool IsReliable => Confidence >= SkewDetector.ReliableConfidence;
}

/// <summary>Finds dark bands along the edges of scans: black borders of the scanner, the grey shadow of a book's spine.</summary>
public static class ScanBorders
{
    /// <summary>A band is never taken as wider than this share of the picture (a darker picture is content).</summary>
    public const double MaxShare = 0.25;

    /// <summary>
    /// The bands: from each edge inwards, rows or columns whose mean is darker than 60% of the paper's grey (its 90th
    /// percentile) with most pixels dark, as long as they follow each other (gaps of 0.5% are bridged).
    /// </summary>
    public static PixelInsets Find(GrayImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var paper = GrayTransforms.Percentile(GrayTransforms.Histogram(image), 0.9);
        if (paper < 96)
        {
            return PixelInsets.None; // a dark picture: no paper to tell a border from
        }

        var dark = (int)(paper * 0.6);
        var (w, h) = (image.Width, image.Height);
        var columnMeans = new double[w];
        var columnDark = new int[w];
        var rowMeans = new double[h];
        var rowDark = new int[h];
        for (var y = 0; y < h; y++)
        {
            var row = image.Row(y);
            long sum = 0;
            var count = 0;
            for (var x = 0; x < w; x++)
            {
                var value = row[x];
                sum += value;
                columnMeans[x] += value;
                if (value < dark)
                {
                    count++;
                    columnDark[x]++;
                }
            }

            rowMeans[y] = (double)sum / w;
            rowDark[y] = count;
        }

        bool IsColumnBand(int x) => columnMeans[x] / h < dark && columnDark[x] > h / 2;
        bool IsRowBand(int y) => rowMeans[y] < dark && rowDark[y] > w / 2;
        return new PixelInsets(
            Band(w, IsColumnBand, fromStart: true),
            Band(h, IsRowBand, fromStart: true),
            Band(w, IsColumnBand, fromStart: false),
            Band(h, IsRowBand, fromStart: false));
    }

    private static int Band(int length, Func<int, bool> isBand, bool fromStart)
    {
        var limit = (int)(length * MaxShare);
        var gap = Math.Max(1, (int)(length * 0.005));
        var width = 0;
        var missed = 0;
        for (var i = 0; i < limit; i++)
        {
            var index = fromStart ? i : length - 1 - i;
            if (isBand(index))
            {
                width = i + 1;
                missed = 0;
            }
            else if (++missed > gap)
            {
                break;
            }
        }

        return width;
    }
}

/// <summary>How much of a page is ink: what tells a blank sheet from a page with text.</summary>
public static class InkCoverage
{
    /// <summary>A pixel darker than this is ink (on pictures rendered at low resolution, which blurs dust away).</summary>
    public const int DarkLevel = 160;

    /// <summary>Each side left out besides the bands (punch holes, the scanner's edge).</summary>
    public const double MarginShare = 0.05;

    /// <summary>
    /// The share (in %) of dark pixels inside the page: <see cref="MarginShare"/> of each side and the dark bands along
    /// the edges (<see cref="ScanBorders"/>) left out, so a blank sheet with a black scanner border or a spine shadow is
    /// still blank.
    /// </summary>
    public static double DarkPercent(GrayImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var marginX = (int)Math.Round(image.Width * MarginShare);
        var marginY = (int)Math.Round(image.Height * MarginShare);
        var bands = ScanBorders.Find(image);
        var inside = new PixelInsets(Math.Max(marginX, bands.Left), Math.Max(marginY, bands.Top), Math.Max(marginX, bands.Right), Math.Max(marginY, bands.Bottom))
            .Inside(image.Width, image.Height);
        if (inside.IsEmpty)
        {
            return 0;
        }

        long dark = 0;
        for (var y = inside.Y; y < inside.Bottom; y++)
        {
            foreach (var value in image.Row(y).Slice(inside.X, inside.Width))
            {
                if (value < DarkLevel)
                {
                    dark++;
                }
            }
        }

        return dark * 100.0 / inside.Area;
    }
}

/// <summary>
/// Measures how the lines of a page are tilted. The page is shrunk to about 1000 pixels wide; ink is what is clearly
/// darker than the paper (below Otsu's threshold and 60% of the way from the paper's grey to the ink's), so paper noise,
/// JPEG ringing and the bands along the edges do not count. Each character (a connected patch of ink of a character's
/// size) gives the middle of its bottom edge; these points are projected along lines of each angle and the angle whose
/// projection has the sharpest profile (sum of squared differences between neighbouring rows) wins — first by quarter
/// degrees, then by fiftieths. Projecting characters rather than pixels keeps the axis-aligned 8×8 grid of JPEG scans and
/// the staircases of low-resolution scans from pulling the answer to 0°.
/// </summary>
public static class SkewDetector
{
    /// <summary>Estimates at least this confident are worth acting on (text pages give 0.85 and more, blank ones about 0.5).</summary>
    public const double ReliableConfidence = 0.7;

    private const int WorkWidth = 1000;
    private const int MinCharacters = 30;

    /// <summary>The tilt within ±<paramref name="maxDegrees"/>, or null when the page has too little ink or contrast to tell.</summary>
    public static SkewEstimate? Estimate(GrayImage image, double maxDegrees = 10)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegrees);
        var small = GrayTransforms.ShrinkToWidth(image, WorkWidth).Image;
        var inside = ScanBorders.Find(small).Inside(small.Width, small.Height);
        if (inside.Width < 16 || inside.Height < 16)
        {
            return null;
        }

        var histogram = GrayTransforms.Histogram(small, inside);
        var paper = GrayTransforms.Percentile(histogram, 0.9);
        var dark = GrayTransforms.Percentile(histogram, 0.002);
        if (paper - dark < 40)
        {
            return null; // no ink: a blank or flat page
        }

        var threshold = Math.Min(GrayTransforms.OtsuThreshold(histogram), paper - (int)(0.6 * (paper - dark)));
        var points = CharacterBottoms(small, inside, threshold, Math.Max(8, small.Width / 15));
        if (points.Count < MinCharacters)
        {
            return null;
        }

        var scores = new Dictionary<double, double>();
        double Score(double degrees)
        {
            if (!scores.TryGetValue(degrees, out var score))
            {
                score = Sharpness(points, small.Height, small.Width, degrees);
                scores[degrees] = score;
            }

            return score;
        }

        var best = 0.0;
        var steps = (int)Math.Round(maxDegrees / 0.25);
        for (var i = -steps; i <= steps; i++)
        {
            var degrees = i * 0.25;
            if (Score(degrees) > Score(best))
            {
                best = degrees;
            }
        }

        var coarse = scores.Values.Order().ToList();
        var median = coarse[coarse.Count / 2];
        var center = best;
        for (var i = -12; i <= 12; i++)
        {
            var degrees = Math.Round(center + (i * 0.02), 2);
            if (Math.Abs(degrees) <= maxDegrees && Score(degrees) > Score(best))
            {
                best = degrees;
            }
        }

        var top = Score(best);
        return new SkewEstimate(best, top <= 0 ? 0 : Math.Clamp((top - median) / top, 0, 1));
    }

    /// <summary>
    /// The middle of the bottom edge of every patch of ink (8-connected) of a character's size that lies wholly inside
    /// <paramref name="inside"/>.
    /// </summary>
    private static List<(double X, double Y)> CharacterBottoms(GrayImage image, PixelRect inside, int threshold, int maxSize)
    {
        var (w, pixels) = (image.Width, image.Pixels);
        var seen = new bool[pixels.Length];
        var stack = new int[pixels.Length];
        var bottoms = new List<(double X, double Y)>();
        for (var y = inside.Y; y < inside.Bottom; y++)
        {
            for (var x = inside.X; x < inside.Right; x++)
            {
                var start = (y * w) + x;
                if (seen[start] || pixels[start] >= threshold)
                {
                    continue;
                }

                var (minX, maxX, minY, maxY, count, top) = (x, x, y, y, 0, 0);
                seen[start] = true;
                stack[top++] = start;
                while (top > 0)
                {
                    var at = stack[--top];
                    var (px, py) = (at % w, at / w);
                    count++;
                    (minX, maxX, minY, maxY) = (Math.Min(minX, px), Math.Max(maxX, px), Math.Min(minY, py), Math.Max(maxY, py));
                    for (var ny = Math.Max(inside.Y, py - 1); ny <= Math.Min(inside.Bottom - 1, py + 1); ny++)
                    {
                        for (var nx = Math.Max(inside.X, px - 1); nx <= Math.Min(inside.Right - 1, px + 1); nx++)
                        {
                            var next = (ny * w) + nx;
                            if (!seen[next] && pixels[next] < threshold)
                            {
                                seen[next] = true;
                                stack[top++] = next;
                            }
                        }
                    }
                }

                var (height, width) = (maxY - minY + 1, maxX - minX + 1);
                var touchesEdge = minX == inside.X || minY == inside.Y || maxX == inside.Right - 1 || maxY == inside.Bottom - 1;
                if (height >= 3 && height <= maxSize && width <= 2 * maxSize && count >= 3 && !touchesEdge)
                {
                    bottoms.Add(((minX + maxX + 1) / 2.0, maxY + 1));
                }
            }
        }

        return bottoms;
    }

    /// <summary>The profile of the points projected along lines rising to the right at <paramref name="degrees"/>.</summary>
    private static double Sharpness(List<(double X, double Y)> points, int height, int width, double degrees)
    {
        var tan = Math.Tan(degrees * Math.PI / 180);
        var offset = tan < 0 ? -tan * width : 0;
        var bins = new double[height + (int)Math.Ceiling(Math.Abs(tan) * width) + 3];
        foreach (var (x, y) in points)
        {
            // Each point is shared between the two rows it falls between, so the profile moves smoothly with the angle.
            var position = y + (x * tan) + offset;
            var row = (int)position;
            var share = position - row;
            bins[row] += 1 - share;
            bins[row + 1] += share;
        }

        double sum = 0;
        for (var i = 1; i < bins.Length; i++)
        {
            var difference = bins[i] - bins[i - 1];
            sum += difference * difference;
        }

        return sum;
    }
}
