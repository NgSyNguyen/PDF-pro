using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Imaging;
using PdfPro.Operations.Converting;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// TIFF both ways with Windows' own codec (WIC): our files decode to exactly the pixels of the PNG of the same pages
/// (LZW with the predictor, CCITT Group 4), and the TIFF files Windows writes become PDF pages of the same pixels.
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

    [Theory]
    [InlineData(TiffCompressOption.None)]
    [InlineData(TiffCompressOption.Lzw)]
    [InlineData(TiffCompressOption.Zip)]
    [InlineData(TiffCompressOption.Rle)]
    public async Task TiffsWrittenByWindows_BecomePdfPages_OfTheSamePixels(TiffCompressOption compression)
    {
        var colour = Bitmap(PixelFormats.Bgr24, (x, y, c) => (byte)((x * 7) + (y * 13) + (c * 80)));
        var grey = Bitmap(PixelFormats.Gray8, (x, y, _) => (byte)(x * 4));
        var input = _temp.Combine($"windows-{compression}.tif");
        Encode(input, compression, colour, grey);
        var output = _temp.Combine($"windows-{compression}.pdf");

        var result = await new FromImagesOperation().WriteAsync([input], output, new FromImagesParams(), null, Token);

        result.PageCount.ShouldBe(2);
        Differences(await RenderAsync(output, 0), Pixels(colour, PixelFormats.Bgra32).Data).ShouldBeLessThanOrEqualTo(1, "colour page");
        Differences(await RenderAsync(output, 1), Pixels(grey, PixelFormats.Bgra32).Data).ShouldBeLessThanOrEqualTo(1, "grey page");
    }

    [Fact]
    public async Task Group4TiffsWrittenByWindows_KeepTheirBlackPixels()
    {
        var pixels = Bitmap(PixelFormats.BlackWhite, (x, y, _) => (byte)((x / 8) % 2 == 0 && y > 10 ? 0x00 : 0xFF));
        var input = _temp.Combine("windows-g4.tif");
        Encode(input, TiffCompressOption.Ccitt4, pixels);
        var output = _temp.Combine("windows-g4.pdf");

        await new FromImagesOperation().WriteAsync([input], output, new FromImagesParams(), null, Token);

        Differences(await RenderAsync(output, 0), Pixels(pixels, PixelFormats.Bgra32).Data).ShouldBe(0);
    }

    private const int Side = 64;

    /// <summary>A 64 × 48 bitmap at 72 DPI whose sample (x, y, channel) is given; for black and white, by bytes of 8 pixels.</summary>
    private static BitmapSource Bitmap(PixelFormat format, Func<int, int, int, byte> sample)
    {
        var (width, height) = (Side, 48);
        var stride = ((width * format.BitsPerPixel) + 7) / 8;
        var data = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var i = 0; i < stride; i++)
            {
                var channels = Math.Max(1, format.BitsPerPixel / 8);
                data[(y * stride) + i] = format.BitsPerPixel == 1 ? sample(i * 8, y, 0) : sample(i / channels, y, i % channels);
            }
        }

        var bitmap = BitmapSource.Create(width, height, 72, 72, format, null, data, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Encode(string path, TiffCompressOption compression, params BitmapSource[] frames)
    {
        var encoder = new TiffBitmapEncoder { Compression = compression };
        foreach (var frame in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }

        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Page <paramref name="page"/> rendered by PDFium at 72 DPI (one pixel per point), as BGRA.</summary>
    private static Task<byte[]> RenderAsync(string path, int page)
        => TestOperations.PdfiumGate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(TestOperations.PdfiumGate, path);
                using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None });
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    bitmap.Pixels.Slice(y * bitmap.Stride, bitmap.Width * 4).CopyTo(pixels.AsSpan(y * bitmap.Width * 4));
                }

                return pixels;
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>The largest difference of a colour channel between two BGRA images of the same size.</summary>
    private static int Differences(byte[] actual, byte[] expected)
    {
        actual.Length.ShouldBe(expected.Length, "the page is the picture's size");
        var largest = 0;
        for (var i = 0; i < actual.Length; i++)
        {
            if (i % 4 != 3)
            {
                largest = Math.Max(largest, Math.Abs(actual[i] - expected[i]));
            }
        }

        return largest;
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
