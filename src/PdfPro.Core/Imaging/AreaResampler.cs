namespace PdfPro.Core.Imaging;

/// <summary>
/// Shrinks 8-bit images by area averaging: each new pixel is the mean of the old pixels it covers, partly covered ones
/// weighted by the part covered. For reductions this is the filter of choice (no aliasing, no ringing, every old pixel
/// counted once); it works a row at a time, so only two rows of sums are kept besides the images.
/// </summary>
public static class AreaResampler
{
    /// <summary>
    /// The image of <paramref name="width"/> × <paramref name="height"/> pixels of <paramref name="channels"/> interleaved
    /// samples, shrunk to <paramref name="newWidth"/> × <paramref name="newHeight"/> (each no larger than before).
    /// </summary>
    public static byte[] Shrink(ReadOnlySpan<byte> samples, int width, int height, int channels, int newWidth, int newHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(newWidth, width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(newHeight, height);
        var stride = width * channels;
        if (samples.Length < (long)stride * height)
        {
            throw new ArgumentException("The samples are fewer than the image has.", nameof(samples));
        }

        var columns = Spans(width, newWidth);
        var newStride = newWidth * channels;
        var output = new byte[newStride * newHeight];
        var row = new float[newStride];
        var sums = new float[newStride];
        var yScale = (double)height / newHeight;
        var outputRow = 0;
        var covered = 0.0; // how much of the current output row the rows added so far cover
        for (var y = 0; y < height && outputRow < newHeight; y++)
        {
            ShrinkRow(samples.Slice(y * stride, stride), channels, columns, row);

            // The source row [y, y + 1) spreads over output rows of height yScale.
            var top = (double)y;
            while (top < y + 1 && outputRow < newHeight)
            {
                var bottom = Math.Min(y + 1, (outputRow + 1) * yScale);
                var weight = (float)(bottom - top);
                for (var i = 0; i < newStride; i++)
                {
                    sums[i] += row[i] * weight;
                }

                covered += weight;
                top = bottom;
                if (bottom >= ((outputRow + 1) * yScale) - 1e-9)
                {
                    Emit(sums, (float)covered, output.AsSpan(outputRow * newStride, newStride));
                    Array.Clear(sums);
                    covered = 0;
                    outputRow++;
                }
            }
        }

        if (outputRow < newHeight && covered > 0)
        {
            Emit(sums, (float)covered, output.AsSpan(outputRow * newStride, newStride));
        }

        return output;
    }

    /// <summary>For each new column: the first old column it covers and the weights of the old columns it covers.</summary>
    private static (int First, float[] Weights)[] Spans(int width, int newWidth)
    {
        var scale = (double)width / newWidth;
        var spans = new (int First, float[] Weights)[newWidth];
        for (var x = 0; x < newWidth; x++)
        {
            var left = x * scale;
            var right = Math.Min(width, (x + 1) * scale);
            var first = (int)Math.Floor(left);
            var last = Math.Min(width - 1, (int)Math.Ceiling(right) - 1);
            var weights = new float[last - first + 1];
            for (var i = first; i <= last; i++)
            {
                weights[i - first] = (float)((Math.Min(i + 1, right) - Math.Max(i, left)) / scale);
            }

            spans[x] = (first, weights);
        }

        return spans;
    }

    /// <summary>A source row averaged across into new columns (the weights of each new column add up to 1).</summary>
    private static void ShrinkRow(ReadOnlySpan<byte> source, int channels, (int First, float[] Weights)[] columns, float[] row)
    {
        for (var x = 0; x < columns.Length; x++)
        {
            var (first, weights) = columns[x];
            for (var c = 0; c < channels; c++)
            {
                var sum = 0f;
                for (var i = 0; i < weights.Length; i++)
                {
                    sum += source[((first + i) * channels) + c] * weights[i];
                }

                row[(x * channels) + c] = sum;
            }
        }
    }

    private static void Emit(float[] sums, float covered, Span<byte> output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (byte)Math.Clamp((int)MathF.Round(sums[i] / covered), 0, 255);
        }
    }
}
