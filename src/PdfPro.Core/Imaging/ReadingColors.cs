namespace PdfPro.Core.Imaging;

/// <summary>The colours pages are shown in while reading (never what is saved, printed or exported).</summary>
public enum ReadingColorMode
{
    /// <summary>As the document is.</summary>
    Normal,

    /// <summary>Dark paper, light text: lightness turned over, colours kept (red stays red, a photo stays readable).</summary>
    Night,

    /// <summary>Warm, ivory paper: easier on the eyes than white.</summary>
    Sepia,
}

/// <summary>Turns the pixels of a rendered page into the reading colours.</summary>
public static class ReadingColors
{
    /// <summary>The paper of <see cref="ReadingColorMode.Night"/> (white becomes this grey, not black: softer).</summary>
    public const byte NightPaper = 30;

    /// <summary>The ink of <see cref="ReadingColorMode.Night"/> (black text becomes this light grey).</summary>
    public const byte NightInk = 225;

    private static readonly (byte R, byte G, byte B) SepiaPaper = (0xF4, 0xEC, 0xD8);

    private static readonly byte[] SepiaRed = Ramp(SepiaPaper.R);
    private static readonly byte[] SepiaGreen = Ramp(SepiaPaper.G);
    private static readonly byte[] SepiaBlue = Ramp(SepiaPaper.B);

    /// <summary>
    /// Changes <paramref name="pixels"/> (32-bit BGRA or BGRX, 4 bytes a pixel, the fourth left alone) in place to
    /// <paramref name="mode"/>.
    /// </summary>
    public static void Apply(Span<byte> pixels, ReadingColorMode mode)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("Pixels take 4 bytes each.", nameof(pixels));
        }

        switch (mode)
        {
            case ReadingColorMode.Normal:
                return;
            case ReadingColorMode.Night:
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    var luma = ((299 * r) + (587 * g) + (114 * b) + 500) / 1000;

                    // Lightness turned over between the paper and the ink; the colour (its difference from grey) kept.
                    var target = NightInk - (((NightInk - NightPaper) * luma) / 255);
                    var shift = target - luma;
                    pixels[i] = Clamp(b + shift);
                    pixels[i + 1] = Clamp(g + shift);
                    pixels[i + 2] = Clamp(r + shift);
                }

                return;
            case ReadingColorMode.Sepia:
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = SepiaBlue[pixels[i]];
                    pixels[i + 1] = SepiaGreen[pixels[i + 1]];
                    pixels[i + 2] = SepiaRed[pixels[i + 2]];
                }

                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>A channel multiplied by the paper's: white becomes the paper, black stays black.</summary>
    private static byte[] Ramp(byte paper)
    {
        var ramp = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            ramp[v] = (byte)(((v * paper) + 127) / 255);
        }

        return ramp;
    }
}
