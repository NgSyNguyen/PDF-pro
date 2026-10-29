using PdfPro.Core.Imaging;
using Shouldly;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>
/// The pieces of image compression that need no PDF library: CCITT G4 coding, area-average shrinking, PNG predictors and
/// reading JPEG headers. G4 data is also decoded by PDFium in PdfPro.Operations.Tests (CompressOperationTests).
/// </summary>
public sealed class CompressionCodecTests
{
    [Fact]
    public void G4_CodeTables_AreComplete_AndPrefixFree()
    {
        var (white, black, extended) = (CcittG4.Tables[0], CcittG4.Tables[1], CcittG4.Tables[2]);
        white.Count.ShouldBe(64 + 27, "terminating codes 0–63, make-up codes 64–1728");
        black.Count.ShouldBe(64 + 27);
        extended.Count.ShouldBe(13, "1792–2560");

        // A decoder reads a run code of one colour among that colour's codes and the extended ones (and EOL).
        foreach (var codes in new[] { white.Concat(extended).Append("000000000001").ToList(), black.Concat(extended).Append("000000000001").ToList() })
        {
            codes.Distinct().Count().ShouldBe(codes.Count);
            foreach (var code in codes)
            {
                codes.ShouldNotContain(other => other != code && other.StartsWith(code, StringComparison.Ordinal), $"{code} is a prefix of another code");
            }
        }
    }

    [Fact]
    public void G4_WhiteRows_AreOneBitEach_ThenTheEndOfBlock()
    {
        // Two white rows: V0, V0, then EOL EOL: 11 000000000001 000000000001, padded with zeros.
        var data = CcittG4.Encode([0xFF, 0xFF], width: 8, height: 2);

        data.ShouldBe([0xC0, 0x04, 0x00, 0x40]);
    }

    [Fact]
    public void G4_ABlackFirstRow_IsCodedHorizontally()
    {
        // 8 pixels: 2 white, 6 black. Against the white row b1 = b2 = 8: a1 = 2, |a1 − b1| = 6 > 3 → horizontal mode:
        // 001, white 2 (0111), black 6 (0010); then a0 = 8 ends the row. Then EOL EOL.
        var data = CcittG4.Encode([0b1100_0000], width: 8, height: 1);

        var bits = string.Concat(data.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        bits.ShouldStartWith("001" + "0111" + "0010" + "000000000001" + "000000000001");
    }

    [Fact]
    public void Shrink_AveragesWhatEachNewPixelCovers()
    {
        // 4×2 grey to 2×1: each new pixel is the mean of a 2×2 block.
        byte[] samples = [0, 100, 200, 40, 20, 80, 0, 0];
        AreaResampler.Shrink(samples, 4, 2, 1, 2, 1).ShouldBe(new byte[] { 50, 60 });

        // 3 → 2 columns: the middle pixel is shared by both, half each (in units of 1.5 old pixels).
        AreaResampler.Shrink([0, 90, 180], 3, 1, 1, 2, 1).ShouldBe(new byte[] { 30, 150 });

        // Rows the same way, and channels kept apart.
        byte[] rgb = [255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255, 0, 255, 0, 0, 255, 0];
        AreaResampler.Shrink(rgb, 2, 3, 3, 1, 2).ShouldBe(new byte[] { 170, 0, 85, 0, 170, 85 });
    }

    [Fact]
    public void Shrink_KeepsAFlatImageFlat_AtAnyRatio()
    {
        var samples = Enumerable.Repeat((byte)137, 997 * 613).ToArray();

        var shrunk = AreaResampler.Shrink(samples, 997, 613, 1, 151, 89);

        shrunk.Length.ShouldBe(151 * 89);
        shrunk.ShouldAllBe(v => v == 137);
    }

    [Fact]
    public void Predictors_RoundTrip_AndPickTheCheapestFilter()
    {
        var random = new Random(7);
        var samples = new byte[37 * 11 * 3];
        random.NextBytes(samples);

        var filtered = PngPredictor.Encode(samples, 37, 11, 3);

        filtered.Length.ShouldBe(((37 * 3) + 1) * 11);
        Unfilter(filtered, 37 * 3, 3).ShouldBe(samples);

        // Rows that repeat the one above: "Up" makes them zeros.
        var striped = new byte[20 * 4];
        for (var i = 0; i < striped.Length; i++)
        {
            striped[i] = (byte)((i % 20) * 13);
        }

        var striping = PngPredictor.Encode(striped, 20, 4, 1);
        Enumerable.Range(1, 3).Select(row => striping[row * 21]).ShouldAllBe(filter => filter == 2);
        Unfilter(striping, 20, 1).ShouldBe(striped);
    }

    [Fact]
    public void JpegHeader_ReadsTheFrame_TheAdobeMarker_AndTheQuality()
    {
        var jpeg = Jpeg(quality: 50, width: 640, height: 480, components: 3, adobeTransform: 1);

        var header = JpegHeader.Read(jpeg).ShouldNotBeNull();

        header.Width.ShouldBe(640);
        header.Height.ShouldBe(480);
        header.Components.ShouldBe(3);
        header.BitsPerSample.ShouldBe(8);
        header.Progressive.ShouldBeFalse();
        header.AdobeTransform.ShouldBe(1);
        header.Quality.ShouldBe(50);

        JpegHeader.Read(Jpeg(quality: 90, width: 10, height: 20, components: 1, adobeTransform: null))!.Quality.ShouldBe(90);
        JpegHeader.Read(Jpeg(quality: 15, width: 10, height: 20, components: 1, adobeTransform: null))!.Quality.ShouldBe(15);
        JpegHeader.Read("not a jpeg"u8).ShouldBeNull();
    }

    /// <summary>The headers of a baseline JPEG whose luminance table is the IJG one at <paramref name="quality"/> (no scan data).</summary>
    private static byte[] Jpeg(int quality, int width, int height, int components, int? adobeTransform)
    {
        int[] standard =
        [
            16, 11, 12, 14, 12, 10, 16, 14, 13, 14, 18, 17, 16, 19, 24, 40, 26, 24, 22, 22, 24, 49, 35, 37, 29, 40, 58, 51, 61, 60, 57, 51,
            56, 55, 64, 72, 92, 78, 64, 68, 87, 69, 55, 56, 80, 109, 81, 87, 95, 98, 103, 104, 103, 62, 77, 113, 121, 112, 100, 120, 92, 101, 103, 99,
        ];
        var scale = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        var output = new List<byte> { 0xFF, 0xD8 };
        if (adobeTransform is { } transform)
        {
            output.AddRange([0xFF, 0xEE, 0, 14, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, (byte)transform]);
        }

        output.AddRange([0xFF, 0xDB, 0, 67, 0x00]);
        output.AddRange(standard.Select(v => (byte)Math.Clamp(((v * scale) + 50) / 100, 1, 255)));
        output.AddRange([0xFF, 0xC0, 0, (byte)(8 + (3 * components)), 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components]);
        for (var c = 0; c < components; c++)
        {
            output.AddRange([(byte)(c + 1), 0x11, 0]);
        }

        output.AddRange([0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 63, 0, 0xFF, 0xD9]);
        return [.. output];
    }

    /// <summary>Undoes PNG predictors (each row preceded by its filter type).</summary>
    private static byte[] Unfilter(byte[] filtered, int stride, int bpp)
    {
        var rows = filtered.Length / (stride + 1);
        var output = new byte[stride * rows];
        for (var y = 0; y < rows; y++)
        {
            var filter = filtered[y * (stride + 1)];
            for (var i = 0; i < stride; i++)
            {
                var value = filtered[(y * (stride + 1)) + 1 + i];
                int left = i >= bpp ? output[(y * stride) + i - bpp] : 0;
                int up = y > 0 ? output[((y - 1) * stride) + i] : 0;
                int upLeft = i >= bpp && y > 0 ? output[((y - 1) * stride) + i - bpp] : 0;
                var p = left + up - upLeft;
                var (pa, pb, pc) = (Math.Abs(p - left), Math.Abs(p - up), Math.Abs(p - upLeft));
                var paeth = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
                output[(y * stride) + i] = (byte)(filter switch
                {
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) >> 1),
                    4 => value + paeth,
                    _ => value,
                });
            }
        }

        return output;
    }
}
