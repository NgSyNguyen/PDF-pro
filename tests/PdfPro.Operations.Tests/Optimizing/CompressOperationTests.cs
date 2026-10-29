using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.ExternalTools;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using Shouldly;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// "Nén" (PROMPTS P9 A): images brought down to the preset's resolution by where they are drawn, photographs as JPEG,
/// 1-bit scans as CCITT G4 (decoded back by PDFium bit for bit), masks lossless, duplicates shared, a JPEG already
/// good enough never coded again, and the original kept when nothing smaller comes out.
/// </summary>
public sealed class CompressOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly Lazy<bool> QpdfInstalled = new(() =>
    {
        try
        {
            return new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance).RunAsync(new ExternalToolRequest("qpdf", ["--version"])).GetAwaiter().GetResult().Succeeded;
        }
        catch (ExternalToolException)
        {
            return false;
        }
    });

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task APhotographDrawnFinerThanTheTarget_IsBroughtDown_AsJpeg_AndLooksTheSame()
    {
        // 1200 × 1200 pixels on 2 × 2 inches: 600 DPI.
        var input = Build(pdf => pdf.Draw(pdf.Page(), pdf.Samples(ImagePdf.Photo(1200, 1200), 1200, 1200, 3), Square(144, 72, 500)));
        var output = _temp.Combine("ebook.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Ebook), null, Token);

        using (var document = ImagePdf.Read(output))
        {
            var image = ImagePdf.Images(document).ShouldHaveSingleItem();
            (Integer(image, "/Width"), Integer(image, "/Height")).ShouldBe((300, 300), "150 DPI on 2 inches");
            Name(image, "/Filter").ShouldBe("/DCTDecode");
            JpegHeader.Read(image.Stream.Value)!.Quality.ShouldBe(75, "the eBook quality");
        }

        report.ImagesResampled.ShouldBe(1);
        report.ImagesRecompressed.ShouldBe(1);
        report.KeptOriginal.ShouldBeFalse();
        report.CompressedSize.ShouldBe(new FileInfo(output).Length);
        report.OriginalSize.ShouldBe(new FileInfo(input).Length);
        report.CompressedSize.ShouldBeLessThan(report.OriginalSize / 10);
        (await MeanDifferenceAsync(input, output, 0)).ShouldBeLessThan(4, "the page looks the same at screen size");
    }

    [Fact]
    public async Task AGreyScanAt300Dpi_ShrinksForEbook_AndStaysLegible()
    {
        // vanban-vi.pdf page 1 as a scanner would give it: 300 DPI grey JPEG of good quality, on an A4 page.
        var scan = await ScannedGreyAsync();
        var jpeg = JpegCodec.Encode(scan, 92);
        var input = Build(pdf => pdf.Draw(pdf.Page(595.28, 841.89), pdf.Jpeg(jpeg, scan.Width, scan.Height, 1), new PdfMatrix(595.28, 0, 0, 841.89, 0, 0)));
        var output = _temp.Combine("scan.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Ebook), null, Token);

        using (var document = ImagePdf.Read(output))
        {
            var image = ImagePdf.Images(document).ShouldHaveSingleItem();
            Integer(image, "/Width").ShouldBe((int)Math.Round(scan.Width / 2.0), "300 → 150 DPI");
            Name(image, "/ColorSpace").ShouldBe("/DeviceGray");
            JpegHeader.Read(image.Stream.Value)!.Components.ShouldBe(1, "grey stays grey");
        }

        report.CompressedSize.ShouldBeLessThan(report.OriginalSize * 35 / 100);
        (await MeanDifferenceAsync(input, output, 0, scale: 100 / 72.0)).ShouldBeLessThan(6, "the text reads the same at 100 DPI");
    }

    [Fact]
    public async Task AnImageDrawnTwice_KeepsTheResolutionOfItsLargestUse_AndPresetsSetTheTarget()
    {
        var input = Build(pdf =>
        {
            var image = pdf.Samples(ImagePdf.Photo(1200, 1200), 1200, 1200, 3);
            var page = pdf.Page();
            pdf.Draw(page, image, Square(72, 72, 600)); // 1 inch: 1200 DPI
            pdf.Draw(page, image, Square(288, 72, 200)); // 4 inches: 300 DPI
        });

        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Screen))).ShouldBe((384, 384), "96 DPI on 4 inches");
        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Ebook))).ShouldBe((600, 600), "150 DPI on 4 inches");
        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Print))).ShouldBe((1200, 1200), "300 DPI is what it has");
        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Custom, Dpi: 100))).ShouldBe((400, 400));
        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Archive))).ShouldBe((1200, 1200), "nothing lossy");
    }

    [Fact]
    public async Task ImagesInsideForms_RotatedOrScaled_AreMeasuredWhereTheyAreDrawn()
    {
        var input = Build(pdf =>
        {
            var image = pdf.Samples(ImagePdf.Photo(1200, 600), 1200, 600, 3);

            // The form doubles, then the image is drawn 72 × 36 inside it, turned a quarter: 2 × 1 inches on the page.
            var form = pdf.Form(PdfMatrix.Scaling(2, 2), image, new PdfMatrix(0, 72, -36, 0, 0, 0));
            pdf.Draw(pdf.Page(), form, new PdfMatrix(1, 0, 0, 1, 300, 300));
        });

        (await SizeAfterAsync(input, new CompressParams(CompressionPreset.Ebook))).ShouldBe((300, 150), "1200 px on 2 inches, 600 on 1");
    }

    [Fact]
    public async Task OneBitScans_StayOneBit_AsG4_BitForBit()
    {
        // A page of text scanned at 300 DPI in black and white: vanban-vi.pdf page 1, rendered and thresholded.
        var (rows, width, height) = await ScannedTextAsync();
        var input = Build(pdf => pdf.Draw(pdf.Page(width, height), pdf.Bilevel(rows, width, height), new PdfMatrix(width, 0, 0, height, 0, 0)));
        var output = _temp.Combine("g4.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Screen), null, Token);

        using (var document = ImagePdf.Read(output))
        {
            var image = ImagePdf.Images(document).ShouldHaveSingleItem();
            Name(image, "/Filter").ShouldBe("/CCITTFaxDecode", "G4 is about half of Flate on scanned text");
            (Integer(image, "/Width"), Integer(image, "/Height"), Integer(image, "/BitsPerComponent")).ShouldBe((width, height, 1), "never resampled, never grey");
            Integer(AsDictionary(image.Elements["/DecodeParms"])!, "/K").ShouldBe(-1);
        }

        report.ImagesRecompressed.ShouldBe(1);
        report.ImagesResampled.ShouldBe(0);
        (await MeanDifferenceAsync(input, output, 0, scale: 1)).ShouldBe(0, "PDFium decodes the G4 data into the very same pixels");
    }

    [Fact]
    public async Task G4Data_DecodesBackExactly_WithNoise_AndWhiteRunsLongerThan2560()
    {
        const int width = 3000;
        const int height = 400;
        var rows = ImagePdf.Scan(width, height);
        var path = Build(pdf =>
        {
            var image = pdf.Bilevel(rows, width, height);
            ImageStreams.Write(pdf.Document, image, new ImageOutcome(CcittG4.Encode(rows, width, height), ImageEncoding.G4, width, height, Resampled: false), 1);
            pdf.Draw(pdf.Page(width, height), image, new PdfMatrix(width, 0, 0, height, 0, 0));
        });

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        using var page = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1 }, cancellationToken: Token);
        var stride = (width + 7) / 8;
        var wrong = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var black = (rows[(y * stride) + (x >> 3)] & (0x80 >> (x & 7))) == 0;
                wrong += black == page.GetPixel(x, y).R < 128 ? 0 : 1;
            }
        }

        wrong.ShouldBe(0);
    }

    [Fact]
    public async Task AJpegAsGoodAsAsked_IsNotCodedAgain_AndCompressingTwiceChangesNothingMore()
    {
        var jpeg = Encode(ImagePdf.Photo(300, 300), 300, 300, quality: 70);
        var input = Build(pdf =>
        {
            pdf.Draw(pdf.Page(), pdf.Jpeg(jpeg, 300, 300, 3), Square(144, 72, 500)); // 150 DPI, quality 70
            pdf.Draw(pdf.Page(), pdf.Samples(ImagePdf.Photo(1200, 1200, seed: 2), 1200, 1200, 3), Square(144, 72, 500));
        });
        var once = _temp.Combine("1.pdf");
        var twice = _temp.Combine("2.pdf");

        await Operation().WriteAsync(input, once, new CompressParams(CompressionPreset.Ebook), null, Token);
        var second = await Operation().WriteAsync(once, twice, new CompressParams(CompressionPreset.Ebook), null, Token);

        using var first = ImagePdf.Read(once);
        using var again = ImagePdf.Read(twice);
        var firstImages = ImagePdf.Images(first);
        firstImages[0].Stream.Value.ShouldBe(jpeg, "a JPEG of quality 70 at 150 DPI is what eBook asks for already");
        ImagePdf.Images(again).Select(i => i.Stream.Value).ShouldBe(firstImages.Select(i => i.Stream.Value), "the second time no image is touched");
        second.ImagesRecompressed.ShouldBe(0);
        second.CompressedSize.ShouldBeLessThanOrEqualTo(second.OriginalSize);
    }

    [Fact]
    public async Task SoftMasks_FollowTheirImage_AndStayLossless()
    {
        var mask = new byte[1200 * 1200];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = (byte)(i % 1200 * 255 / 1200);
        }

        var input = Build(pdf =>
        {
            var softMask = pdf.Samples(mask, 1200, 1200, 1);
            var image = pdf.Samples(ImagePdf.Photo(1200, 1200), 1200, 1200, 3, i => i.Elements["/SMask"] = softMask.Reference!);
            pdf.Draw(pdf.Page(), image, Square(144, 72, 500));
        });
        var output = _temp.Combine("mask.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Ebook), null, Token);

        using var document = ImagePdf.Read(output);
        var image = ImagePdf.Images(document).ShouldHaveSingleItem();
        var softMask = AsDictionary(image.Elements["/SMask"])!;
        (Integer(softMask, "/Width"), Integer(softMask, "/Height")).ShouldBe((300, 300));
        Name(softMask, "/Filter").ShouldBe("/FlateDecode", "an alpha channel is never JPEG");
        Integer(AsDictionary(softMask.Elements["/DecodeParms"])!, "/Predictor").ShouldBe(15);
        report.ImagesResampled.ShouldBe(2);
        (await MeanDifferenceAsync(input, output, 0)).ShouldBeLessThan(4);
    }

    [Fact]
    public async Task DuplicatePictures_BecomeOne_AndColourKeyedImagesAreLeftAlone()
    {
        var picture = ImagePdf.Photo(200, 200);
        var keyed = Encoding.ASCII.GetBytes(new string('x', 30_000));
        var input = Build(pdf =>
        {
            pdf.Draw(pdf.Page(), pdf.Samples(picture, 200, 200, 3), Square(200, 72, 72));
            pdf.Draw(pdf.Page(), pdf.Samples(picture, 200, 200, 3), Square(200, 72, 72));
            pdf.Draw(pdf.Page(), pdf.Samples(keyed, 100, 100, 3, i => i.Elements["/Mask"] = new PdfArray(pdf.Document, new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10), new PdfInteger(0), new PdfInteger(10))), Square(200, 72, 72));
        });
        var output = _temp.Combine("shared.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Archive), null, Token);

        using var compressed = ImagePdf.Read(output);
        var images = ImagePdf.Images(compressed);
        images.Count.ShouldBe(2, "pages 1 and 2 share one picture");
        report.SharedCopies.ShouldBeGreaterThanOrEqualTo(1);
        StreamData.Decode(images[1]).ShouldBe(keyed, "colour-key masking needs the exact samples");
        Name(images[1], "/Filter").ShouldBe("/FlateDecode");
        images[1].Elements["/Mask"].ShouldBeOfType<PdfArray>();
    }

    [Fact]
    public async Task Text_ReadsTheSame_AfterCompression()
    {
        var output = _temp.Combine("vb.pdf");

        var report = await Operation().WriteAsync(Samples.VanBanVi, output, new CompressParams(CompressionPreset.Screen), null, Token);

        using var before = PigDocument.Open(Samples.VanBanVi);
        using var after = PigDocument.Open(output);
        after.NumberOfPages.ShouldBe(before.NumberOfPages);
        for (var page = 1; page <= before.NumberOfPages; page++)
        {
            after.GetPage(page).Text.ShouldBe(before.GetPage(page).Text);
        }

        report.CompressedSize.ShouldBeLessThanOrEqualTo(report.OriginalSize);
    }

    [Fact]
    public async Task AFileThatCannotGetSmaller_IsWrittenAsItWas()
    {
        var input = _temp.Combine("gọn.pdf");
        await File.WriteAllBytesAsync(input, CompactPdf(), Token);
        var output = _temp.Combine("ra.pdf");

        var report = await Operation().WriteAsync(input, output, new CompressParams(CompressionPreset.Screen), null, Token);

        report.KeptOriginal.ShouldBeTrue();
        report.CompressedSize.ShouldBe(report.OriginalSize);
        report.Saving.ShouldBe(0);
        (await File.ReadAllBytesAsync(output, Token)).ShouldBe(await File.ReadAllBytesAsync(input, Token));
    }

    [Fact]
    public async Task APreview_IsOnePage_BeforeAndAfter()
    {
        var input = Build(pdf =>
        {
            pdf.Page();
            pdf.Draw(pdf.Page(), pdf.Samples(ImagePdf.Photo(1200, 1200), 1200, 1200, 3), Square(144, 72, 500));
        });

        var preview = await Operation().PreviewAsync(input, 1, new CompressParams(CompressionPreset.Screen), Token);

        using var before = ImagePdf.Read(Save(preview.Before, "before.pdf"));
        using var after = ImagePdf.Read(Save(preview.After, "after.pdf"));
        (before.PageCount, after.PageCount).ShouldBe((1, 1));
        Integer(ImagePdf.Images(after).ShouldHaveSingleItem(), "/Width").ShouldBe(192);
        preview.After.Length.ShouldBeLessThan(preview.Before.Length / 10);
        preview.Report.ImagesResampled.ShouldBe(1);
    }

    [Fact]
    public async Task WrongSettings_AndUnreadableFiles_AreRefused()
    {
        var garbage = _temp.Combine("rác.pdf");
        await File.WriteAllBytesAsync(garbage, Encoding.ASCII.GetBytes("%PDF-1.7\nnot a PDF\n"), Token);
        var output = _temp.Combine("ra.pdf");

        await Should.ThrowAsync<ArgumentException>(() => Operation().WriteAsync(Samples.BasicVi, output, new CompressParams(CompressionPreset.Custom, Dpi: 10), null, Token));
        await Should.ThrowAsync<InvalidDataException>(() => Operation().WriteAsync(garbage, output, new CompressParams(), null, Token));
        await Should.ThrowAsync<InvalidDataException>(() => Operation().WriteAsync(Samples.Encrypted, output, new CompressParams(), null, Token));
        File.Exists(output).ShouldBeFalse("nothing is left behind");
        Directory.GetFiles(_temp.Path).ShouldBe([garbage]);
    }

    [Fact]
    public async Task Qpdf_PacksTheObjects_WhenInstalled()
    {
        Assert.SkipUnless(QpdfInstalled.Value, "qpdf is not installed");
        var output = _temp.Combine("qpdf.pdf");

        var report = await new CompressOperation(Qpdf()).WriteAsync(Samples.Big1000, output, new CompressParams(CompressionPreset.Archive), null, Token);

        report.StructureOptimized.ShouldBeTrue();
        Encoding.Latin1.GetString(await File.ReadAllBytesAsync(output, Token)).ShouldContain("/ObjStm");
        using var document = ImagePdf.Read(output);
        document.PageCount.ShouldBe(1000);
        Directory.GetFiles(_temp.Path).ShouldBe([output], "qpdf's working files are gone");
    }

    private static CompressOperation Operation() => new(Missing());

    private static Qpdf Qpdf() => new(new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance), new QpdfOptions("qpdf"));

    private static Qpdf Missing() => new(new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance), new QpdfOptions("qpdf-that-is-not-installed"));

    /// <summary>A square image of <paramref name="side"/> points at (x, y).</summary>
    private static PdfMatrix Square(double side, double x, double y) => new(side, 0, 0, side, x, y);

    private static int Integer(PdfDictionary dictionary, string key) => StreamData.Integer(dictionary, key) ?? -1;

    private string Build(Action<ImagePdf> build)
    {
        using var pdf = new ImagePdf();
        build(pdf);
        return pdf.Save(_temp.Combine($"in-{Guid.NewGuid():N}.pdf"));
    }

    private string Save(byte[] data, string name)
    {
        var path = _temp.Combine(name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<(int Width, int Height)> SizeAfterAsync(string input, CompressParams parameters)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        await Operation().WriteAsync(input, output, parameters, null, Token);
        using var document = ImagePdf.Read(output);
        var image = ImagePdf.Images(document).ShouldHaveSingleItem();
        return (Integer(image, "/Width"), Integer(image, "/Height"));
    }

    /// <summary>The mean difference of the colour channels of a page rendered from both files.</summary>
    private async Task<double> MeanDifferenceAsync(string first, string second, int page, double scale = 0.5)
    {
        await using var a = await PdfInspector.OpenAsync(pdfium.Gate, first);
        await using var b = await PdfInspector.OpenAsync(pdfium.Gate, second);
        using var left = await a.RenderPageAsync(page, new PageRenderOptions { Scale = scale }, cancellationToken: Token);
        using var right = await b.RenderPageAsync(page, new PageRenderOptions { Scale = scale }, cancellationToken: Token);
        (left.Width, left.Height).ShouldBe((right.Width, right.Height));
        long total = 0;
        for (var y = 0; y < left.Height; y++)
        {
            for (var x = 0; x < left.Width; x++)
            {
                var (b1, g1, r1) = left.GetPixel(x, y);
                var (b2, g2, r2) = right.GetPixel(x, y);
                total += Math.Abs(b1 - b2) + Math.Abs(g1 - g2) + Math.Abs(r1 - r2);
            }
        }

        return total / (3.0 * left.Width * left.Height);
    }

    /// <summary>Page 1 of vanban-vi.pdf rendered at 300 DPI in grey.</summary>
    private async Task<PixelImage> ScannedGreyAsync()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.VanBanVi);
        using var page = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 300 / 72.0 }, cancellationToken: Token);
        var grey = new byte[page.Width * page.Height];
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var (b, g, r) = page.GetPixel(x, y);
                grey[(y * page.Width) + x] = (byte)((r + g + b) / 3);
            }
        }

        return new PixelImage(grey, page.Width, page.Height, 1);
    }

    /// <summary>Page 1 of vanban-vi.pdf rendered at 300 DPI and thresholded: rows packed, 0 = black.</summary>
    private async Task<(byte[] Rows, int Width, int Height)> ScannedTextAsync()
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, Samples.VanBanVi);
        using var page = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 300 / 72.0 }, cancellationToken: Token);
        var stride = (page.Width + 7) / 8;
        var rows = Enumerable.Repeat((byte)0xFF, stride * page.Height).ToArray();
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var (b, g, r) = page.GetPixel(x, y);
                if (r + g + b < 384)
                {
                    rows[(y * stride) + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                }
            }
        }

        return (rows, page.Width, page.Height);
    }

    private static byte[] Encode(byte[] rgb, int width, int height, int quality)
        => JpegCodec.Encode(new PixelImage(rgb, width, height, 3), quality);

    /// <summary>A one-page PDF written by hand with nothing to spare (no stream to compress, no image).</summary>
    private static byte[] CompactPdf()
    {
        var objects = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>",
        };
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append(i + 1).Append(" 0 obj").Append(objects[i]).Append("endobj\n");
        }

        var xref = text.Length;
        text.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        text.Append("trailer<</Size 4/Root 1 0 R>>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
