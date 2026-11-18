using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

/// <summary>Grey pictures of pages: made from rendered BGRA pixels, written as grey PNGs with their resolution.</summary>
public sealed class GrayImageTests
{
    [Fact]
    public void Grey_IsTheLumaOfTheColour_OverWhite()
    {
        // B, G, R, A: red, green, blue, white, black, fully transparent black.
        byte[] bgra =
        [
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 99, 99,
            255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 0, 77, 77,
        ];

        var image = GrayImage.FromBgra(3, 2, bgra, stride: 14);

        image.Pixels.ShouldBe([76, 150, 29, 255, 0, 255]);
        image[2, 1].ShouldBe((byte)255);
        image.Row(1).ToArray().ShouldBe([(byte)255, (byte)0, (byte)255]);
    }

    [Fact]
    public void AGreyPng_RoundTrips_WithItsResolution()
    {
        var pixels = Enumerable.Range(0, 7 * 5).Select(i => (byte)((i * 37) % 256)).ToArray();
        var image = new GrayImage(7, 5, pixels);

        var png = PngCodec.EncodeGray(image, 300);

        var decoded = PngCodec.Decode(png);
        (decoded.Width, decoded.Height).ShouldBe((7, 5));
        Enumerable.Range(0, 35).Select(i => decoded.Pixels[(i * 4) + 1]).ShouldBe(pixels);
        Enumerable.Range(0, 35).All(i => decoded.Pixels[i * 4] == pixels[i] && decoded.Pixels[(i * 4) + 2] == pixels[i] && decoded.Pixels[(i * 4) + 3] == 255).ShouldBeTrue();
        ImageResolution.ReadPng(png).ShouldNotBeNull().X.ShouldBe(300, 0.01);
        ImageResolution.ReadPng(PngCodec.EncodeGray(image)).ShouldBeNull();
    }

    [Fact]
    public void APicture_MustBeFilled()
    {
        Should.Throw<ArgumentException>(() => new GrayImage(4, 4, new byte[15]));
        Should.Throw<ArgumentOutOfRangeException>(() => new GrayImage(0, 4, []));
        GrayImage.White(3, 2).Pixels.ShouldAllBe(p => p == 255);
    }
}
