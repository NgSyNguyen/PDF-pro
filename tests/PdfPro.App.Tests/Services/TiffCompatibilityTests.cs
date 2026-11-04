using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Converting;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// The TIFF files of "PDF sang ảnh" read by Windows' own decoder (WIC): LZW with the predictor and CCITT Group 4 give
/// back exactly the pixels of the PNG of the same pages.
/// </summary>
public sealed class TiffCompatibilityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ColourPages_AreTheSamePixelsAsThePng()
    {
        var (png, tiff) = await WriteAsync(TiffColor.Rgb);

        tiff.Count.ShouldBe(2);
        for (var page = 0; page < 2; page++)
        {
            var expected = Pixels(png[page], PixelFormats.Bgra32);
            var actual = Pixels(tiff[page], PixelFormats.Bgra32);
            actual.Width.ShouldBe(expected.Width);
            actual.Height.ShouldBe(expected.Height);
            actual.Data.SequenceEqual(expected.Data).ShouldBeTrue($"page {page + 1} decodes to the PNG's pixels");
        }
    }

    [Fact]
    public async Task GreyPages_AreTheLuminanceOfThePng()
    {
        var (png, tiff) = await WriteAsync(TiffColor.Gray);

        var colour = Pixels(png[0], PixelFormats.Bgra32);
        var grey = Pixels(tiff[0], PixelFormats.Gray8);
        for (var i = 0; i < grey.Data.Length; i += 97)
        {
            var at = i * 4;
            var level = ((colour.Data[at + 2] * 77) + (colour.Data[at + 1] * 150) + (colour.Data[at] * 29) + 128) >> 8;
            ((double)grey.Data[i]).ShouldBe(level, 1);
        }
    }

    [Fact]
    public async Task BlackAndWhitePages_AreTheThresholdOfThePng()
    {
        var (png, tiff) = await WriteAsync(TiffColor.BlackWhite);

        var colour = Pixels(png[1], PixelFormats.Bgra32);
        var bilevel = Pixels(tiff[1], PixelFormats.Gray8);
        var black = 0;
        for (var i = 0; i < bilevel.Data.Length; i++)
        {
            var at = i * 4;
            var level = ((colour.Data[at + 2] * 77) + (colour.Data[at + 1] * 150) + (colour.Data[at] * 29) + 128) >> 8;
            bilevel.Data[i].ShouldBe(level < 128 ? (byte)0 : (byte)255, $"pixel {i}");
            black += level < 128 ? 1 : 0;
        }

        black.ShouldBeGreaterThan(1000, "the page has text");
    }

    /// <summary>Pages 1–2 of basic-vi.pdf at 72 DPI as PNG files and as one TIFF, decoded by WIC.</summary>
    private async Task<(List<BitmapSource> Png, List<BitmapSource> Tiff)> WriteAsync(TiffColor color)
    {
        var operation = new ToImagesOperation(TestOperations.PdfiumGate);
        var png = await operation.WriteAsync(Samples.Basic, null, _temp.Combine("png"), "a", new ToImagesParams(Dpi: 72, Pages: "1-2"), null, Token);
        var tiff = await operation.WriteAsync(Samples.Basic, null, _temp.Combine("tif"), "a", new ToImagesParams(PageImageFormat.Tiff, Dpi: 72, Pages: "1-2", TiffColor: color), null, Token);
        var pngs = png.Files.Select(f => Decode(f, path => new PngBitmapDecoder(path, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)).Single()).ToList();
        var frames = Decode(tiff.Files.Single(), path => new TiffBitmapDecoder(path, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        return (pngs, frames);
    }

    private static List<BitmapSource> Decode(string path, Func<Stream, BitmapDecoder> open)
    {
        using var stream = File.OpenRead(path);
        return [.. open(stream).Frames.Select(f => (BitmapSource)f)];
    }

    private static (int Width, int Height, byte[] Data) Pixels(BitmapSource source, PixelFormat format)
    {
        var converted = new FormatConvertedBitmap(source, format, null, 0);
        var stride = ((converted.PixelWidth * format.BitsPerPixel) + 7) / 8;
        var data = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(data, stride, 0);
        return (converted.PixelWidth, converted.PixelHeight, data);
    }
}
