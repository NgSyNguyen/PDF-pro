using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PdfPro.Pdfium.Tests.TestSupport;

/// <summary>
/// Compares renders with reference PNGs in tests/PdfPro.Pdfium.Tests/Golden. Set PDFPRO_UPDATE_GOLDEN=1 to
/// (re)write the references from the current renders — then review the images before committing them.
/// </summary>
internal static class GoldenImages
{
    /// <summary>A pixel differs when any channel differs by more than this.</summary>
    private const int ChannelTolerance = 32;

    /// <summary>Share of differing pixels allowed (anti-aliasing changes between PDFium versions).</summary>
    private const double AllowedDifferentPixels = 0.005;

    public static bool UpdateRequested => Environment.GetEnvironmentVariable("PDFPRO_UPDATE_GOLDEN") == "1";

    public static void AssertMatches(PageBitmap actual, string goldenName)
    {
        var path = Path.Combine(Samples.GoldenDirectory, goldenName);
        if (UpdateRequested)
        {
            Directory.CreateDirectory(Samples.GoldenDirectory);
            SavePng(actual, path);
            return;
        }

        File.Exists(path).ShouldBeTrue($"Golden image {goldenName} is missing; run the tests once with PDFPRO_UPDATE_GOLDEN=1 and review it.");
        var (width, height, expected) = LoadBgr32(path);
        actual.Width.ShouldBe(width, "golden width");
        actual.Height.ShouldBe(height, "golden height");

        var pixels = actual.Pixels;
        var different = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var a = (y * actual.Stride) + (x * 4);
                var e = (y * width * 4) + (x * 4);
                if (Math.Abs(pixels[a] - expected[e]) > ChannelTolerance
                    || Math.Abs(pixels[a + 1] - expected[e + 1]) > ChannelTolerance
                    || Math.Abs(pixels[a + 2] - expected[e + 2]) > ChannelTolerance)
                {
                    different++;
                }
            }
        }

        var ratio = (double)different / (width * height);
        ratio.ShouldBeLessThanOrEqualTo(AllowedDifferentPixels, $"{different} of {width * height} pixels differ from {goldenName}");
    }

    public static void SavePng(PageBitmap bitmap, string path)
    {
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgr32, null, bitmap.Scan0, bitmap.Stride * bitmap.Height, bitmap.Stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static (int Width, int Height, byte[] Pixels) LoadBgr32(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }
}
