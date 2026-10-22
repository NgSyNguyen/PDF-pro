using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Pdfium;
using PdfSharp.Drawing;

namespace PdfPro.SampleGenerator.Samples;

/// <summary>
/// scan-vi.pdf: the fake documents of vanban-vi.pdf as a one-sided office scan would give them — 18 A4 pages of grey
/// JPEG images at 90 DPI, no text layer: light paper tone, dust specks, slightly skewed pages, one page upside down
/// (page 12), and a blank separator sheet after each document (pages 4, 7, 10, 14; page 7 shows faint bleed-through).
/// Used by split by marks / blank separators (P4), and later by OCR, deskew and compression (P10).
/// </summary>
/// <param name="textSourcePath">vanban-vi.pdf, generated first.</param>
internal sealed class ScanSample(string textSourcePath) : ISample
{
    /// <summary>Pages (1-based) that start a document: what a user marks before splitting.</summary>
    public static readonly int[] FirstPages = [1, 5, 8, 11, 15];

    /// <summary>Blank separator sheets (1-based).</summary>
    public static readonly int[] SeparatorPages = [4, 7, 10, 14];

    private const double Scale = 90.0 / 72.0;
    private const int JpegQuality = 40;
    private const byte PaperTone = 243;

    public string FileName => "scan-vi.pdf";

    public void Generate(string path)
    {
        using var gate = new PdfiumGate(NullLogger<PdfiumGate>.Instance);
        var source = new PdfDocumentLoader(gate).LoadAsync(textSourcePath, password: null).GetAwaiter().GetResult();
        try
        {
            using var document = SampleDocument.Create("Bản scan tập văn bản mẫu", "Bản scan giả lập: tách theo dấu, trang trắng phân cách, OCR (dữ liệu giả)");
            var random = new Random(20260115);
            var (width, height) = (Render(source, 0).Width, Render(source, 0).Height);
            var sourcePage = 0;
            var documentPages = LegalBundle.Documents.Select(d => d.Pages.Length).ToArray();
            for (var d = 0; d < documentPages.Length; d++)
            {
                for (var p = 0; p < documentPages[d]; p++, sourcePage++)
                {
                    var image = Render(source, sourcePage);
                    var skewed = Rotate(image, (random.NextDouble() - 0.5) * 2.4);
                    if (d == 3 && p == 1)
                    {
                        skewed = skewed.Rotate180();
                    }

                    AddPage(document, Scan(skewed, random));
                }

                if (d < documentPages.Length - 1)
                {
                    var blank = GrayImage.White(width, height);
                    if (d == 1)
                    {
                        blank = BleedThrough(Render(source, sourcePage - 1));
                    }

                    AddPage(document, Scan(blank, random));
                }
            }

            document.Save(path);
        }
        finally
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static GrayImage Render(IPdfDocument document, int pageIndex)
    {
        var options = new PageRenderOptions { Scale = Scale, Flags = PdfRenderFlags.Grayscale };
        using var bitmap = document.RenderPageAsync(pageIndex, options).GetAwaiter().GetResult();
        var image = new GrayImage(bitmap.Width, bitmap.Height);
        var pixels = bitmap.Pixels;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                image[x, y] = pixels[(y * bitmap.Stride) + (x * 4) + 1];
            }
        }

        return image;
    }

    /// <summary>Turns the page by a small angle (degrees) around its centre, like a sheet fed at a slant.</summary>
    private static GrayImage Rotate(GrayImage image, double degrees)
    {
        var result = GrayImage.White(image.Width, image.Height);
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        double cx = image.Width / 2.0, cy = image.Height / 2.0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var sx = (int)Math.Round(cx + ((x - cx) * cos) + ((y - cy) * sin));
                var sy = (int)Math.Round(cy - ((x - cx) * sin) + ((y - cy) * cos));
                if (sx >= 0 && sy >= 0 && sx < image.Width && sy < image.Height)
                {
                    result[x, y] = image[sx, sy];
                }
            }
        }

        return result;
    }

    /// <summary>Faint mirrored text of the other side of the sheet, far lighter than ink.</summary>
    private static GrayImage BleedThrough(GrayImage page)
    {
        var result = GrayImage.White(page.Width, page.Height);
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                result[x, y] = (byte)(255 - ((255 - page[page.Width - 1 - x, y]) * 12 / 100));
            }
        }

        return result;
    }

    /// <summary>Paper tone, a darker scanner edge at the top and a sprinkle of dust specks.</summary>
    private static GrayImage Scan(GrayImage page, Random random)
    {
        var result = new GrayImage(page.Width, page.Height);
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var value = page[x, y] * PaperTone / 255;
                result[x, y] = (byte)(y < 5 ? value * 60 / 100 : value);
            }
        }

        for (var i = 0; i < 120; i++)
        {
            var size = random.Next(1, 3);
            var sx = random.Next(0, page.Width - size);
            var sy = random.Next(0, page.Height - size);
            var shade = (byte)random.Next(40, 120);
            for (var y = sy; y < sy + size; y++)
            {
                for (var x = sx; x < sx + size; x++)
                {
                    result[x, y] = shade;
                }
            }
        }

        return result;
    }

    private static void AddPage(PdfSharp.Pdf.PdfDocument document, GrayImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 90, 90, PixelFormats.Gray8, null, image.Pixels, image.Width);
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var jpeg = new MemoryStream();
        encoder.Save(jpeg);
        jpeg.Position = 0;
        var page = SampleDocument.AddA4Page(document);
        using var graphics = XGraphics.FromPdfPage(page);
        using var xImage = XImage.FromStream(jpeg);
        graphics.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
    }

    /// <summary>An 8-bit grey image, row by row.</summary>
    private sealed class GrayImage(int width, int height)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public byte[] Pixels { get; } = new byte[width * height];

        public byte this[int x, int y]
        {
            get => Pixels[(y * Width) + x];
            set => Pixels[(y * Width) + x] = value;
        }

        public static GrayImage White(int width, int height)
        {
            var image = new GrayImage(width, height);
            Array.Fill(image.Pixels, (byte)255);
            return image;
        }

        public GrayImage Rotate180()
        {
            var result = new GrayImage(Width, Height);
            for (var i = 0; i < Pixels.Length; i++)
            {
                result.Pixels[Pixels.Length - 1 - i] = Pixels[i];
            }

            return result;
        }
    }
}
