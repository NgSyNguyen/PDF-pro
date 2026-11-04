using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>
/// A one-page A4 PDF holding a JPEG photograph of 800 × 800 pixels drawn 2 inches wide (400 DPI): every compression
/// preset but "Lưu trữ" brings it down, so the compressed file is much smaller.
/// </summary>
internal static class PhotoPdf
{
    public const int PhotoPixels = 800;

    public static byte[] Create()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var graphics = XGraphics.FromPdfPage(page))
        using (var photo = new MemoryStream(Jpeg()))
        using (var image = XImage.FromStream(photo))
        {
            graphics.DrawImage(image, 72, 72, 144, 144);
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    /// <summary>A colour gradient with noise (it does not compress away), JPEG quality 92.</summary>
    private static byte[] Jpeg()
    {
        var random = new Random(7);
        var pixels = new byte[PhotoPixels * PhotoPixels * 4];
        for (var y = 0; y < PhotoPixels; y++)
        {
            for (var x = 0; x < PhotoPixels; x++)
            {
                var i = ((y * PhotoPixels) + x) * 4;
                pixels[i] = (byte)Math.Clamp((x * 255 / PhotoPixels) + random.Next(-40, 40), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp((y * 255 / PhotoPixels) + random.Next(-40, 40), 0, 255);
                pixels[i + 2] = (byte)random.Next(256);
                pixels[i + 3] = 255;
            }
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(PhotoPixels, PhotoPixels, 96, 96, PixelFormats.Bgra32, null, pixels, PhotoPixels * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
