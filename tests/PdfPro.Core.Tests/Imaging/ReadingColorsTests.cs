using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>Pages shown dark for reading at night, or on ivory paper.</summary>
public sealed class ReadingColorsTests
{
    [Fact]
    public void AtNight_WhitePaperGoesDark_BlackTextGoesLight_AndAColourKeepsItsHue()
    {
        byte[] pixels =
        [
            255, 255, 255, 255, // white paper
            0, 0, 0, 255, // black text
            0, 0, 200, 255, // red (BGRA)
        ];

        ReadingColors.Apply(pixels, ReadingColorMode.Night);

        pixels[..3].ShouldBe([ReadingColors.NightPaper, ReadingColors.NightPaper, ReadingColors.NightPaper]);
        pixels[3].ShouldBe((byte)255, "alpha left alone");
        pixels[4..7].ShouldBe([ReadingColors.NightInk, ReadingColors.NightInk, ReadingColors.NightInk]);
        var (b, g, r) = (pixels[8], pixels[9], pixels[10]);
        r.ShouldBeGreaterThan(g, "still red");
        r.ShouldBeGreaterThan(b);
    }

    [Fact]
    public void OnIvoryPaper_WhiteBecomesThePaper_AndBlackStaysBlack()
    {
        byte[] pixels = [255, 255, 255, 0, 0, 0, 0, 0];

        ReadingColors.Apply(pixels, ReadingColorMode.Sepia);

        pixels[..3].ShouldBe([(byte)0xD8, (byte)0xEC, (byte)0xF4], "BGR of #F4ECD8");
        pixels[4..7].ShouldBe([(byte)0, (byte)0, (byte)0]);
    }

    [Fact]
    public void Normal_LeavesThePixels_AndAnOddBufferIsRefused()
    {
        byte[] pixels = [12, 34, 56, 78];

        ReadingColors.Apply(pixels, ReadingColorMode.Normal);

        pixels.ShouldBe([(byte)12, (byte)34, (byte)56, (byte)78]);
        Should.Throw<ArgumentException>(() => ReadingColors.Apply(new byte[3], ReadingColorMode.Night));
    }
}
