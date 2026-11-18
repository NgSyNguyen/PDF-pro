using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Operations.Optimizing;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Optimizing;

/// <summary>
/// "Thang xám" (PROMPTS P9 B): colours made grey where they are written (ISO 32000 10.4.2), text kept; what cannot be
/// converted becomes a grey picture of its page; every page of the result is checked by rendering it with PDFium.
/// </summary>
public sealed class GrayscaleOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task VectorColours_BecomeTheirGrey_AndTextStaysText()
    {
        var input = Build(pdf =>
        {
            var page = pdf.Page();
            Helvetica(pdf, page);
            pdf.Raw(page, "1 0 0 rg 100 600 100 100 re f");                 // red: 0.3
            pdf.Raw(page, "0 1 0 0 k 300 600 100 100 re f");               // magenta: 1 − 0.59 = 0.41
            pdf.Raw(page, "0 0 1 RG 20 w 120 420 60 60 re S");             // blue stroke: 0.11
            pdf.Raw(page, "BT /F1 24 Tf 0 0.5 0 rg 100 300 Td (Hello grey) Tj ET");
        });
        var output = _temp.Combine("xám.pdf");

        var report = await Operation().WriteAsync(input, output, new GrayscaleParams(), null, Token);

        report.PicturePages.ShouldBeEmpty();
        report.ColoredPages.ShouldBeEmpty();
        (await ColoredPages(output)).ShouldBeEmpty();
        ((double)await GrayAt(output, 0, 150, 650)).ShouldBe(0.3 * 255, 3);
        ((double)await GrayAt(output, 0, 350, 650)).ShouldBe(0.41 * 255, 3);
        ((double)await GrayAt(output, 0, 115, 450)).ShouldBe(0.11 * 255, 3, "the blue border");
        using var pig = PigDocument.Open(output);
        pig.GetPage(1).Text.ShouldContain("Hello grey", Case.Sensitive, "text stays text");
    }

    [Fact]
    public async Task RgbJpegAndIndexedImages_BecomeGreyImages_OfTheSameKind()
    {
        var input = Build(pdf =>
        {
            var page = pdf.Page();
            pdf.Draw(page, pdf.Samples(ImagePdf.Photo(120, 120), 120, 120, 3), new PdfMatrix(120, 0, 0, 120, 50, 600));
            pdf.Draw(page, pdf.Jpeg(JpegCodec.Encode(new PixelImage(ImagePdf.Photo(120, 120, seed: 4), 120, 120, 3), 80), 120, 120, 3), new PdfMatrix(120, 0, 0, 120, 250, 600));
            var indexed = pdf.Samples([0, 1, 2, 1], 2, 2, 1, image => image.Elements["/ColorSpace"] = IndexedRgb(pdf.Document, [255, 0, 0, 0, 255, 0, 0, 0, 255]));
            pdf.Draw(page, indexed, new PdfMatrix(120, 0, 0, 120, 450, 600));
        });
        var output = _temp.Combine("ảnh xám.pdf");

        var report = await Operation().WriteAsync(input, output, new GrayscaleParams(), null, Token);

        report.ImagesConverted.ShouldBe(3);
        report.PicturePages.ShouldBeEmpty();
        (await ColoredPages(output)).ShouldBeEmpty();
        using var document = ImagePdf.Read(output);
        var images = ImagePdf.Images(document);
        images.Count.ShouldBe(3);
        Name(images[0], "/ColorSpace").ShouldBe("/DeviceGray");
        Name(images[1], "/ColorSpace").ShouldBe("/DeviceGray");
        Name(images[1], "/Filter").ShouldBe("/DCTDecode", "a JPEG stays a JPEG");
        var table = AsArray(images[2].Elements["/ColorSpace"]).ShouldNotBeNull();
        Resolve(table.Elements[1]).ShouldBeOfType<PdfName>().Value.ShouldBe("/DeviceGray", "a colour table becomes a grey table, the indexes stay");
        StreamData.Decode(AsDictionary(table.Elements[3])!).ShouldBe(new byte[] { 76, 150, 28 });
    }

    [Fact]
    public async Task SeparationsDeviceNAndShadings_AreGreyThroughTheirFunctions()
    {
        var input = Build(pdf =>
        {
            var page = pdf.Page();
            var document = pdf.Document;
            var spaces = new PdfDictionary(document);
            spaces.Elements["/Spot"] = Array(document, new PdfName("/Separation"), new PdfName("/Red"), new PdfName("/DeviceRGB"), Exponential(document, [1, 1, 1], [1, 0, 0]));
            var calculator = new PdfDictionary(document);
            calculator.Elements.SetInteger("/FunctionType", 4);
            calculator.Elements["/Domain"] = Numbers(document, 0, 1, 0, 1);
            calculator.Elements["/Range"] = Numbers(document, 0, 1, 0, 1, 0, 1, 0, 1);
            calculator.CreateStream(Encoding.ASCII.GetBytes("{ 0 0 }"));
            document.Internals.AddObject(calculator);
            spaces.Elements["/Inks"] = Array(document, new PdfName("/DeviceN"), Array(document, new PdfName("/A"), new PdfName("/B")), new PdfName("/DeviceCMYK"), calculator.Reference!);
            pdf.Resources(page).Elements["/ColorSpace"] = spaces;
            var shading = new PdfDictionary(document);
            shading.Elements.SetInteger("/ShadingType", 2);
            shading.Elements.SetName("/ColorSpace", "/DeviceRGB");
            shading.Elements["/Coords"] = Numbers(document, 100, 0, 500, 0);
            shading.Elements["/Function"] = Exponential(document, [1, 0, 0], [0, 0, 1]);
            shading.Elements["/Extend"] = Array(document, new PdfBoolean(true), new PdfBoolean(true));
            var shadings = new PdfDictionary(document);
            shadings.Elements["/Sh0"] = shading;
            pdf.Resources(page).Elements["/Shading"] = shadings;
            pdf.Raw(page, "/Spot cs 1 sc 100 600 100 100 re f");           // the spot ink's red: 0.3
            pdf.Raw(page, "/Inks cs 0 1 sc 300 600 100 100 re f");         // magenta through the calculator: 0.41
            pdf.Raw(page, "q 100 300 400 100 re W n /Sh0 sh Q");           // red to blue
        });
        var output = _temp.Combine("mực.pdf");

        var report = await Operation().WriteAsync(input, output, new GrayscaleParams(), null, Token);

        report.PicturePages.ShouldBeEmpty();
        (await ColoredPages(output)).ShouldBeEmpty();
        ((double)await GrayAt(output, 0, 150, 650)).ShouldBe(0.3 * 255, 3);
        ((double)await GrayAt(output, 0, 350, 650)).ShouldBe(0.41 * 255, 3);
        ((double)await GrayAt(output, 0, 101, 350)).ShouldBe(0.3 * 255, 4, "the shading starts red");
        ((double)await GrayAt(output, 0, 499, 350)).ShouldBe(0.11 * 255, 4, "and ends blue");
    }

    [Fact]
    public async Task AnInlineImage_AndAnAnnotation_BecomeGrey()
    {
        var input = Build(pdf =>
        {
            var page = pdf.Page();
            pdf.Raw(page, "q 200 0 0 200 100 500 cm BI /W 2 /H 2 /CS /RGB /BPC 8 /F /AHx ID FF0000 00FF00 0000FF FFFFFF> EI Q");
            var document = pdf.Document;
            var appearance = new PdfDictionary(document);
            appearance.Elements.SetName("/Type", "/XObject");
            appearance.Elements.SetName("/Subtype", "/Form");
            appearance.Elements["/BBox"] = Numbers(document, 0, 0, 100, 100);
            appearance.CreateStream(Encoding.ASCII.GetBytes("1 0 0 RG 6 w 0 0 1 rg 10 10 80 80 re B"));
            document.Internals.AddObject(appearance);
            var states = new PdfDictionary(document);
            states.Elements["/N"] = appearance.Reference!;
            var square = new PdfDictionary(document);
            square.Elements.SetName("/Type", "/Annot");
            square.Elements.SetName("/Subtype", "/Square");
            square.Elements["/Rect"] = Numbers(document, 350, 300, 450, 400);
            square.Elements["/C"] = Numbers(document, 1, 0, 0);
            square.Elements["/IC"] = Numbers(document, 0, 0, 1);
            square.Elements["/AP"] = states;
            document.Internals.AddObject(square);
            page.Elements["/Annots"] = Array(document, square.Reference!);
        });
        var output = _temp.Combine("chú thích.pdf");

        var report = await Operation().WriteAsync(input, output, new GrayscaleParams(), null, Token);

        report.PicturePages.ShouldBeEmpty();
        report.ImagesConverted.ShouldBe(1, "the inline image");
        (await ColoredPages(output)).ShouldBeEmpty();
        ((double)await GrayAt(output, 0, 150, 650)).ShouldBe(0.3 * 255, 3, "the inline image's red pixel");
        ((double)await GrayAt(output, 0, 400, 350)).ShouldBe(0.11 * 255, 3, "the square's blue inside");
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var annotation = AsDictionary(Annotations(document.Pages[0])!.Elements[0])!;
        PdfFunction.Numbers(annotation.Elements["/C"]).ShouldBe([0.3, 0.3, 0.3]);
        PdfFunction.Numbers(annotation.Elements["/IC"]).ShouldBe([0.11, 0.11, 0.11]);
    }

    [Fact]
    public async Task APageWithSomethingNotHandled_BecomesAGreyPicture_AndTheOthersKeepTheirText()
    {
        var input = Build(pdf =>
        {
            var masked = pdf.Samples(ImagePdf.Photo(60, 60), 60, 60, 3, image => image.Elements["/Mask"] = Numbers(pdf.Document, 0, 20, 0, 20, 0, 20));
            pdf.Draw(pdf.Page(), masked, new PdfMatrix(200, 0, 0, 200, 100, 400));
            var second = pdf.Page();
            Helvetica(pdf, second);
            pdf.Raw(second, "BT /F1 24 Tf 1 0 0 rg 100 500 Td (Second page) Tj ET");
        });
        var output = _temp.Combine("một trang ảnh.pdf");

        var report = await Operation().WriteAsync(input, output, new GrayscaleParams(RasterDpi: 100), null, Token);

        report.PicturePages.ShouldBe([0], "colour-key masking compares exact samples: the page is made a picture");
        report.ColoredPages.ShouldBeEmpty();
        (await ColoredPages(output)).ShouldBeEmpty();
        using var pig = PigDocument.Open(output);
        pig.GetPage(2).Text.ShouldContain("Second page");
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        AsDictionary(AsDictionary(document.Pages[0].Elements["/Resources"])!.Elements["/XObject"])!.Elements.Keys.ShouldBe(["/PdfProGray"]);
    }

    [Fact]
    public async Task VietnameseText_StaysTheSameText()
    {
        var output = _temp.Combine("văn bản.pdf");

        var report = await Operation().WriteAsync(Samples.BasicVi, output, new GrayscaleParams(), null, Token);

        report.PageCount.ShouldBe(6);
        report.PicturePages.ShouldBeEmpty();
        (await ColoredPages(output)).ShouldBeEmpty();
        PageTexts(output).ShouldBe(PageTexts(Samples.BasicVi));
    }

    [Fact]
    public async Task NotesWithoutAnAppearance_GetAGreyIcon_InsteadOfTheViewersYellow()
    {
        var output = _temp.Combine("ghi chú.pdf");

        var report = await Operation().WriteAsync(Samples.LinksAnnots, output, new GrayscaleParams(), null, Token);

        report.PicturePages.ShouldBeEmpty("the notes are given grey appearances, the page need not become a picture");
        (await ColoredPages(output)).ShouldBeEmpty();
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var notes = document.Pages.Cast<PdfPage>()
            .SelectMany(p => Annotations(p)?.Elements.Select(AsDictionary).OfType<PdfDictionary>() ?? [])
            .Where(a => Name(a, "/Subtype") == "/Text")
            .ToList();
        notes.ShouldNotBeEmpty();
        notes.ShouldAllBe(n => n.Elements.ContainsKey("/AP"));
    }

    [Fact]
    public async Task TheRasterMode_MakesEveryPageAGreyPicture()
    {
        var output = _temp.Combine("ảnh trang.pdf");

        var report = await Operation().WriteAsync(Samples.BasicVi, output, new GrayscaleParams(GrayscaleMode.Raster, RasterDpi: 96), null, Token);

        report.PicturePages.ShouldBe([0, 1, 2, 3, 4, 5]);
        (await ColoredPages(output)).ShouldBeEmpty();
        PageTexts(output).ShouldAllBe(t => t.Trim().Length == 0, "the pages are pictures: no text is left");
    }

    [Fact]
    public async Task AProtectedFile_IsRefused()
    {
        await Should.ThrowAsync<InvalidDataException>(() => Operation().WriteAsync(Samples.Encrypted, _temp.Combine("x.pdf"), new GrayscaleParams(), null, Token));
        File.Exists(_temp.Combine("x.pdf")).ShouldBeFalse();
    }

    private GrayscaleOperation Operation() => new(pdfium.Gate);

    private string Build(Action<ImagePdf> build)
    {
        using var pdf = new ImagePdf();
        build(pdf);
        return pdf.Save(_temp.Combine($"in-{Guid.NewGuid():N}.pdf"));
    }

    private static void Helvetica(ImagePdf pdf, PdfPage page)
    {
        var font = new PdfDictionary(pdf.Document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(pdf.Document);
        fonts.Elements["/F1"] = font;
        pdf.Resources(page).Elements["/Font"] = fonts;
    }

    private static PdfArray Array(PdfDocument document, params PdfItem[] items) => new(document, items);

    private static PdfArray Numbers(PdfDocument document, params double[] numbers) => new(document, [.. numbers.Select(n => new PdfReal(n))]);

    private static PdfDictionary Exponential(PdfDocument document, double[] c0, double[] c1)
    {
        var function = new PdfDictionary(document);
        function.Elements.SetInteger("/FunctionType", 2);
        function.Elements["/Domain"] = Numbers(document, 0, 1);
        function.Elements["/C0"] = Numbers(document, c0);
        function.Elements["/C1"] = Numbers(document, c1);
        function.Elements.SetInteger("/N", 1);
        return function;
    }

    private static PdfArray IndexedRgb(PdfDocument document, byte[] table)
    {
        var lookup = new PdfDictionary(document);
        lookup.CreateStream(table);
        document.Internals.AddObject(lookup);
        return Array(document, new PdfName("/Indexed"), new PdfName("/DeviceRGB"), new PdfInteger((table.Length / 3) - 1), lookup.Reference!);
    }

    private static string[] PageTexts(string path)
    {
        using var pig = PigDocument.Open(path);
        return [.. pig.GetPages().Select(p => p.Text)];
    }

    /// <summary>The pages PDFium renders with a coloured pixel.</summary>
    private Task<List<int>> ColoredPages(string path)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                var colored = new List<int>();
                for (var i = 0; i < document.PageCount; i++)
                {
                    using var bitmap = document.RenderPage(i, new PageRenderOptions { Scale = 0.5 });
                    var pixels = bitmap.Pixels;
                    for (var y = 0; y < bitmap.Height && !colored.Contains(i); y++)
                    {
                        var row = pixels.Slice(y * bitmap.Stride, bitmap.Width * 4);
                        for (var x = 0; x < row.Length; x += 4)
                        {
                            if (Math.Max(row[x], Math.Max(row[x + 1], row[x + 2])) - Math.Min(row[x], Math.Min(row[x + 1], row[x + 2])) > 6)
                            {
                                colored.Add(i);
                                break;
                            }
                        }
                    }
                }

                return colored;
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>The grey level PDFium renders at (x, y) in PDF points of an unrotated page, at 72 DPI.</summary>
    private Task<int> GrayAt(string path, int page, double x, double y)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                using var bitmap = document.RenderPage(page, new PageRenderOptions { Scale = 1 });
                var row = (int)(bitmap.Height - y);
                var at = (row * bitmap.Stride) + ((int)x * 4);
                var pixels = bitmap.Pixels;
                return (int)pixels[at + 1];
            },
            PdfiumPriority.Normal,
            Token);
}
