using PdfPro.Core.Imaging;

namespace PdfPro.Core.Tests.Imaging;

public sealed class SignatureImagesTests
{
    [Fact]
    public void Paper_BecomesTransparent_InkStays_EdgesFade()
    {
        byte[] pixels =
        [
            250, 250, 250, 255, // paper
            20, 20, 30, 255, // ink
            205, 205, 205, 255, // the soft edge (luminance 205, threshold 225, softness 40)
            180, 180, 180, 255, // just dark enough to stay
        ];

        SignatureImages.MakeWhiteTransparent(pixels);

        pixels[3].ShouldBe((byte)0);
        pixels[7].ShouldBe((byte)255);
        pixels[11].ShouldBe((byte)127);
        pixels[15].ShouldBe((byte)255);
    }

    [Fact]
    public void Bounds_HoldEveryOpaquePixel()
    {
        var pixels = new byte[5 * 4 * 4];
        pixels[(((1 * 5) + 2) * 4) + 3] = 255;
        pixels[(((3 * 5) + 3) * 4) + 3] = 200;

        SignatureImages.OpaqueBounds(pixels, 5, 4).ShouldBe(new PixelBounds(2, 1, 2, 3));
        SignatureImages.OpaqueBounds(new byte[16], 2, 2).ShouldBeNull();
    }

    [Fact]
    public void Crop_KeepsTheRectangleWithAMarginInsideThePicture()
    {
        var pixels = Enumerable.Range(0, 4 * 3).SelectMany(i => new byte[] { (byte)i, 0, 0, 255 }).ToArray();

        var (cropped, bounds) = SignatureImages.Crop(pixels, 4, 3, new PixelBounds(1, 1, 2, 1), margin: 1);

        bounds.ShouldBe(new PixelBounds(0, 0, 4, 3));
        cropped.ShouldBe(pixels);
        SignatureImages.Crop(pixels, 4, 3, new PixelBounds(2, 1, 1, 1)).Pixels.ShouldBe(new byte[] { 6, 0, 0, 255 });
    }
}
