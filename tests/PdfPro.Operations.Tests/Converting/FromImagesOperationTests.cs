using PdfPro.Core.Imaging;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Converting;

/// <summary>
/// "Ảnh sang PDF" (PROMPTS P9 C2): JPEG byte for byte, turned by EXIF; PNG lossless with its transparency; TIFF pages
/// with Group 4 kept as coded; pages fitting the picture or A4.
/// </summary>
public sealed class FromImagesOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AJpeg_GoesIn_ByteForByte()
    {
        var jpeg = JpegCodec.Encode(new PixelImage(ImagePdf.Photo(300, 200), 300, 200, 3), 85);
        var input = Write("ảnh.jpg", jpeg);
        var output = _temp.Combine("ảnh.pdf");

        var result = await new FromImagesOperation().WriteAsync([input], output, new FromImagesParams(), null, Token);

        result.PageCount.ShouldBe(1);
        result.JpegsKept.ShouldBe(1);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var image = Images(document.Pages[0]).ShouldHaveSingleItem();
        Name(image, "/Filter").ShouldBe("/DCTDecode");
        image.Stream.Value.ShouldBe(jpeg, "the stream is the JPEG file, byte for byte (PROMPTS P9 acceptance)");
        document.Pages[0].Width.Point.ShouldBe(300 * 72 / 96.0, 0.01, "no resolution in the file: 96 DPI");
        document.Pages[0].Height.Point.ShouldBe(200 * 72 / 96.0, 0.01);
    }

    [Fact]
    public async Task AnExifTurnedJpeg_IsShownUpright_WithoutRecoding()
    {
        // 200 × 100 stored, red on the left half; orientation 6 = shown turned a quarter clockwise: red on top.
        var stored = new byte[200 * 100 * 3];
        for (var i = 0; i < 200 * 100; i++)
        {
            var left = i % 200 < 100;
            stored[i * 3] = 255;
            stored[(i * 3) + 1] = (byte)(left ? 0 : 255);
            stored[(i * 3) + 2] = (byte)(left ? 0 : 255);
        }

        var jpeg = TestImages.WithOrientation(JpegCodec.Encode(new PixelImage(stored, 200, 100, 3), 95), 6);
        var output = _temp.Combine("xoay.pdf");

        await new FromImagesOperation().WriteAsync([Write("xoay.jpg", jpeg)], output, new FromImagesParams(DefaultDpi: 72), null, Token);

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            document.Pages[0].Width.Point.ShouldBe(100, 0.01, "the page is the upright picture: 100 wide");
            document.Pages[0].Height.Point.ShouldBe(200, 0.01);
            Images(document.Pages[0]).Single().Stream.Value.ShouldBe(jpeg);
        }

        var (top, bottom) = await Pixels(output, (50, 40), (50, 160));
        Near(top, (255, 0, 0)).ShouldBeTrue($"red on top (JPEG is lossy), not {top}");
        Near(bottom, (255, 255, 255)).ShouldBeTrue($"white below, not {bottom}");
    }

    [Fact]
    public async Task APngWithTransparency_KeepsItAsASoftMask_AndAGreyPngIsGrey()
    {
        var rgba = TestImages.Png(40, 20, (x, _) => x < 20 ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)0));
        var grey = TestImages.Png(10, 10, (x, y) => ((byte)(x * 20), (byte)(x * 20), (byte)(x * 20), (byte)255));
        var output = _temp.Combine("png.pdf");

        var result = await new FromImagesOperation().WriteAsync([Write("a.png", rgba), Write("b.png", grey)], output, new FromImagesParams(DefaultDpi: 72), null, Token);

        result.PageCount.ShouldBe(2);
        result.JpegsKept.ShouldBe(0);
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var transparent = Images(document.Pages[0]).Single();
            Name(transparent, "/ColorSpace").ShouldBe("/DeviceRGB");
            AsDictionary(transparent.Elements["/SMask"]).ShouldNotBeNull("the transparency is kept");
            Name(Images(document.Pages[1]).Single(), "/ColorSpace").ShouldBe("/DeviceGray");
        }

        var (opaque, clear) = await Pixels(output, (10, 10), (30, 10));
        opaque.ShouldBe((0, 0, 255));
        clear.ShouldBe((255, 255, 255), "the transparent half shows the white page");
    }

    [Theory]
    [InlineData(ImagePageSize.A4Fit, 0)]
    [InlineData(ImagePageSize.A4Margins, 10)]
    public async Task OnA4_TheLandscapePicture_FitsATurnedSheet_Centred(ImagePageSize size, double margin)
    {
        var black = TestImages.Png(400, 200, (_, _) => ((byte)0, (byte)0, (byte)0, (byte)255));
        var output = _temp.Combine("a4.pdf");

        await new FromImagesOperation().WriteAsync([Write("đen.png", black)], output, new FromImagesParams(size, margin), null, Token);

        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        document.Pages[0].Width.Point.ShouldBe(841.89, 0.01, "landscape A4 for a landscape picture");
        document.Pages[0].Height.Point.ShouldBe(595.276, 0.01);
        var (left, right) = await DarkColumns(output);
        var expected = margin * 72 / 25.4;
        ((double)left).ShouldBe(expected, 1.5);
        ((double)right).ShouldBe(841.89 - expected, 1.5);
    }

    [Fact]
    public async Task AMultiPageTiff_GivesItsPages_WithGroup4KeptAsCoded()
    {
        var rows = new byte[(64 / 8) * 40];
        Array.Fill(rows, (byte)0xFF);
        for (var y = 10; y < 30; y++)
        {
            rows[(y * 8) + 2] = 0x00; // a black bar in the middle columns
        }

        using var tiff = new MemoryStream();
        var writer = new TiffWriter(tiff, 2);
        writer.AddBilevel(rows, 64, 40, 72);
        writer.AddGray(ImagePdf.Photo(10, 10).Where((_, i) => i % 3 == 0).ToArray(), 10, 10, 72);
        var output = _temp.Combine("scan.pdf");

        var result = await new FromImagesOperation().WriteAsync([Write("scan.tif", tiff.ToArray())], output, new FromImagesParams(), null, Token);

        result.PageCount.ShouldBe(2);
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var fax = Images(document.Pages[0]).Single();
            Name(fax, "/Filter").ShouldBe("/CCITTFaxDecode");
            fax.Stream.Value.ShouldBe(CcittG4.Encode(rows, 64, 40));
            document.Pages[0].Width.Point.ShouldBe(64, 0.01, "72 DPI from the file");
        }

        var (bar, paper) = await Pixels(output, (20, 20), (5, 5));
        bar.ShouldBe((0, 0, 0));
        paper.ShouldBe((255, 255, 255));
    }

    [Fact]
    public async Task APictureThatCannotBeRead_IsNamed_AndNothingIsWritten()
    {
        var good = Write("tốt.jpg", JpegCodec.Encode(new PixelImage(new byte[300], 10, 10, 3), 80));
        var bad = Write("hỏng.png", "không phải ảnh"u8.ToArray());
        var output = _temp.Combine("x.pdf");

        var error = await Should.ThrowAsync<ImageFileException>(() => new FromImagesOperation().WriteAsync([good, bad], output, new FromImagesParams(), null, Token));

        error.FileIndex.ShouldBe(1);
        error.Problem.ShouldBe(ImageFileProblem.Unreadable);
        File.Exists(output).ShouldBeFalse();
        Directory.GetFiles(_temp.Path, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task AGroup3Tiff_IsNotSupported()
    {
        byte[] tiff = [.. "II*\0"u8, 8, 0, 0, 0, 3, 0, .. Entry(256, 8), .. Entry(257, 1), .. Entry(259, 3), 0, 0, 0, 0];

        var error = await Should.ThrowAsync<ImageFileException>(() => new FromImagesOperation().WriteAsync([Write("g3.tif", tiff)], _temp.Combine("g3.pdf"), new FromImagesParams(), null, Token));

        error.Problem.ShouldBe(ImageFileProblem.Unreadable, "the page has no strips either");

        static byte[] Entry(ushort tag, ushort value) => [(byte)tag, (byte)(tag >> 8), 3, 0, 1, 0, 0, 0, (byte)value, (byte)(value >> 8), 0, 0];
    }

    private static bool Near((int R, int G, int B) actual, (int R, int G, int B) expected)
        => Math.Abs(actual.R - expected.R) <= 10 && Math.Abs(actual.G - expected.G) <= 10 && Math.Abs(actual.B - expected.B) <= 10;

    private string Write(string name, byte[] data)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static List<PdfDictionary> Images(PdfPage page)
        => [.. AsDictionary(AsDictionary(page.Elements["/Resources"])!.Elements["/XObject"])!.Elements.Values.Select(AsDictionary).OfType<PdfDictionary>()];

    /// <summary>The colours PDFium renders at two points (x, y from the top left, in points at 72 DPI) of page 1.</summary>
    private Task<((int, int, int) First, (int, int, int) Second)> Pixels(string path, (int X, int Y) first, (int X, int Y) second)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(0, new PageRenderOptions { Scale = 1 });
                var pixels = bitmap.Pixels.ToArray();
                (int, int, int) At((int X, int Y) p)
                {
                    var at = (p.Y * bitmap.Stride) + (p.X * 4);
                    return (pixels[at + 2], pixels[at + 1], pixels[at]);
                }

                return (At(first), At(second));
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>The first and last columns (points) of page 1 with a dark pixel on its middle row.</summary>
    private Task<(int Left, int Right)> DarkColumns(string path)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(0, new PageRenderOptions { Scale = 1 });
                var pixels = bitmap.Pixels.ToArray();
                var row = bitmap.Height / 2;
                var dark = Enumerable.Range(0, bitmap.Width).Where(x => pixels[(row * bitmap.Stride) + (x * 4)] < 128).ToList();
                return (dark[0], dark[^1] + 1);
            },
            PdfiumPriority.Normal,
            Token);
}
