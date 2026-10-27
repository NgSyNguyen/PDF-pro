using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfPro.App.Services.Editing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Tests.Services;

/// <summary>Pictures copied into the session before they are placed on pages.</summary>
public sealed class ImageImporterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AJpeg_IsKeptByteForByte_AndStoredOnce()
    {
        var source = Write("photo.jpg", Encode(new JpegBitmapEncoder(), 40, 20));
        var folder = _temp.Combine("images");

        var first = ImageImporter.Import(source, folder);
        var again = ImageImporter.Import(source, folder);

        File.ReadAllBytes(first.Path).ShouldBe(File.ReadAllBytes(source));
        Path.GetExtension(first.Path).ShouldBe(".jpg");
        again.Path.ShouldBe(first.Path, "named by content");
        Directory.GetFiles(folder).ShouldHaveSingleItem();
        (first.Width, first.Height).ShouldBe((40, 20));
    }

    [Fact]
    public void OtherFormats_BecomeEightBitPngs()
    {
        var source = Write("scan.bmp", Encode(new BmpBitmapEncoder(), 30, 10));

        var image = ImageImporter.Import(source, _temp.Combine("images"));

        Path.GetExtension(image.Path).ShouldBe(".png");
        var decoded = BitmapDecoder.Create(new Uri(image.Path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        decoded.Format.ShouldBe(PixelFormats.Bgra32);
        (decoded.PixelWidth, decoded.PixelHeight).ShouldBe((30, 10));
    }

    [Fact]
    public void AFileThatIsNoPicture_IsRefused()
    {
        var source = Write("notes.png", "not a picture at all"u8.ToArray());

        Should.Throw<NotSupportedException>(() => ImageImporter.Import(source, _temp.Combine("images")));
    }

    [Fact]
    public void ThePlacedSize_UsesThePicturesResolution_AndShrinksToFit()
    {
        var image = new ImportedImage("x.png", 1000, 500, 96, 96);

        image.SizeWithin(new PdfSize(10000, 10000)).ShouldBe(new PdfSize(750, 375));
        image.SizeWithin(new PdfSize(300, 300)).ShouldBe(new PdfSize(300, 150));
        new ImportedImage("y.jpg", 600, 300, 300, 300).SizeWithin(new PdfSize(10000, 10000)).ShouldBe(new PdfSize(144, 72));
    }

    private static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)200, (byte)100, (byte)50, (byte)255);
        }

        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
