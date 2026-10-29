namespace PdfPro.Core.Imaging;

/// <summary>
/// The PNG predictors (filter types 0–4) of 8-bit samples, as Flate streams take them with <c>/Predictor 15</c>: each
/// row is preceded by the filter chosen for it — the one whose output has the smallest sum of absolute values, the
/// heuristic of libpng — which usually makes the deflated image markedly smaller than unfiltered samples.
/// </summary>
public static class PngPredictor
{
    /// <summary>
    /// The rows of an image of <paramref name="width"/> × <paramref name="height"/> pixels of <paramref name="channels"/>
    /// 8-bit samples, each filtered and preceded by its filter type: (width × channels + 1) × height bytes.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var stride = width * channels;
        if (samples.Length < (long)stride * height)
        {
            throw new ArgumentException("The samples are fewer than the image has.", nameof(samples));
        }

        var output = new byte[(stride + 1) * height];
        var candidate = new byte[stride];
        var best = new byte[stride];
        var none = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = samples.Slice(y * stride, stride);
            var above = y == 0 ? none : samples.Slice((y - 1) * stride, stride);
            var bestFilter = 0;
            var bestCost = long.MaxValue;
            for (var filter = 0; filter <= 4; filter++)
            {
                Filter(filter, row, above, channels, candidate);
                long cost = 0;
                foreach (var b in candidate)
                {
                    cost += (sbyte)b is var s && s < 0 ? -s : s;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestFilter = filter;
                    candidate.CopyTo(best, 0);
                }
            }

            var at = y * (stride + 1);
            output[at] = (byte)bestFilter;
            best.CopyTo(output, at + 1);
        }

        return output;
    }

    private static void Filter(int filter, ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, int bpp, byte[] output)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0;
            int up = above[i];
            int upLeft = i >= bpp ? above[i - bpp] : 0;
            output[i] = filter switch
            {
                1 => (byte)(row[i] - left),
                2 => (byte)(row[i] - up),
                3 => (byte)(row[i] - ((left + up) >> 1)),
                4 => (byte)(row[i] - Paeth(left, up, upLeft)),
                _ => row[i],
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
