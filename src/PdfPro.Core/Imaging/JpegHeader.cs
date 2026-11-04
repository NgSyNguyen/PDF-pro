namespace PdfPro.Core.Imaging;

/// <summary>What the header of a JPEG file says about its image.</summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Components">1 (grey), 3 (YCbCr or RGB) or 4 (CMYK or YCCK).</param>
/// <param name="BitsPerSample">8 for nearly every JPEG; 12 or 16 for a few medical or lossless ones.</param>
/// <param name="Progressive">Coded progressively (SOF2) rather than as one baseline/extended scan.</param>
/// <param name="AdobeTransform">The colour transform of an Adobe APP14 marker (0 none, 1 YCbCr, 2 YCCK), or null.</param>
/// <param name="Quality">
/// The quality 1–100 of the IJG (libjpeg) scale whose luminance table is closest to the file's, or null without
/// quantization tables. Encoders of other scales (Photoshop's) still map onto it roughly.
/// </param>
public sealed record JpegHeader(int Width, int Height, int Components, int BitsPerSample, bool Progressive, int? AdobeTransform, int? Quality)
{
    /// <summary>The standard luminance quantization table (ITU-T T.81 K.1), in zigzag order like DQT.</summary>
    private static readonly int[] StandardLuminance =
    [
        16, 11, 12, 14, 12, 10, 16, 14, 13, 14, 18, 17, 16, 19, 24, 40,
        26, 24, 22, 22, 24, 49, 35, 37, 29, 40, 58, 51, 61, 60, 57, 51,
        56, 55, 64, 72, 92, 78, 64, 68, 87, 69, 55, 56, 80, 109, 81, 87,
        95, 98, 103, 104, 103, 62, 77, 113, 121, 112, 100, 120, 92, 101, 103, 99,
    ];

    /// <summary>The header of <paramref name="data"/>, or null when it is not a JPEG file whose frame header can be read.</summary>
    public static JpegHeader? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        int[]? luminance = null;
        int? adobe = null;
        (int Width, int Height, int Components, int Bits, bool Progressive)? frame = null;
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
            {
                return null;
            }

            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }

            var length = (data[at + 2] << 8) | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length)
            {
                break;
            }

            var segment = data.Slice(at + 4, length - 2);
            switch (marker)
            {
                case 0xDB:
                    luminance ??= LuminanceTable(segment);
                    break;
                case 0xEE when segment.Length >= 12 && segment[..5].SequenceEqual("Adobe"u8):
                    adobe = segment[11];
                    break;
                case >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC when segment.Length >= 6:
                    frame = ((segment[3] << 8) | segment[4], (segment[1] << 8) | segment[2], segment[5], segment[0], marker is 0xC2 or 0xC6 or 0xCA or 0xCE);
                    break;
            }

            if (marker == 0xDA)
            {
                break; // the scan data follows: every table and the frame header come before it
            }

            at += 2 + length;
        }

        return frame is { } f && f.Width > 0 && f.Height > 0 && f.Components > 0
            ? new JpegHeader(f.Width, f.Height, f.Components, f.Bits, f.Progressive, adobe, luminance is null ? null : QualityOf(luminance))
            : null;
    }

    /// <summary>The first table of the DQT segment with destination 0 (luminance), in zigzag order, or null.</summary>
    private static int[]? LuminanceTable(ReadOnlySpan<byte> segment)
    {
        var at = 0;
        while (at < segment.Length)
        {
            var precision = segment[at] >> 4;
            var destination = segment[at] & 0x0F;
            var size = precision == 0 ? 64 : 128;
            if (at + 1 + size > segment.Length)
            {
                return null;
            }

            if (destination == 0)
            {
                var table = new int[64];
                for (var i = 0; i < 64; i++)
                {
                    table[i] = precision == 0 ? segment[at + 1 + i] : (segment[at + 1 + (2 * i)] << 8) | segment[at + 2 + (2 * i)];
                }

                return table;
            }

            at += 1 + size;
        }

        return null;
    }

    /// <summary>The IJG quality whose scaled standard table is closest (sum of absolute differences) to <paramref name="table"/>.</summary>
    private static int QualityOf(int[] table)
    {
        var best = 50;
        var bestDistance = long.MaxValue;
        for (var quality = 1; quality <= 100; quality++)
        {
            var scale = quality < 50 ? 5000 / quality : 200 - (2 * quality);
            long distance = 0;
            for (var i = 0; i < 64; i++)
            {
                var expected = Math.Clamp(((StandardLuminance[i] * scale) + 50) / 100, 1, 255);
                distance += Math.Abs(expected - table[i]);
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = quality;
            }
        }

        return best;
    }
}
