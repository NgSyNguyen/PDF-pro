using PdfPro.Core.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Overlay;
using PdfPro.Core.Redaction;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Security;
using PdfPro.Operations.Tests.TestSupport;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Security;

/// <summary>The vector mode: only what lies under the areas goes; the rest of the page keeps its text and drawings.</summary>
public sealed partial class RedactOperationTests
{
    /// <summary>The area of <see cref="WriteDrawings"/>: across the line, the blue band and the right part of the picture.</summary>
    private static readonly PdfRect DrawingArea = new(250, 330, 350, 660);

    private static readonly RgbColor Red = new(200, 0, 0);

    [Fact]
    public async Task InTheVectorMode_LinesShapesAndPictures_LoseOnlyWhatIsUnderTheArea()
    {
        var input = WriteDrawings();

        var output = await RedactAsync(input, new RedactParams([new RedactionArea(1, [DrawingArea])], Red, Mode: RedactionMode.Vector));

        var report = RedactOperation.ReportOf(Last!).ShouldNotBeNull();
        report.PicturePages.ShouldBeEmpty();
        using (var pig = PigDocument.Open(output))
        {
            var page = pig.GetPage(1);
            TextNormalizer.ToNfc(Squeeze(page.Text)).ShouldBe("Giữlạichữnày");

            // Apart from the box itself, no drawn line or shape reaches into the area any more.
            var inner = new PdfRect(DrawingArea.Left + 0.5, DrawingArea.Bottom + 0.5, DrawingArea.Right - 0.5, DrawingArea.Top - 0.5);
            foreach (var subpath in page.Paths.SelectMany(p => p))
            {
                if (subpath.GetBoundingRectangle() is not { } bounds)
                {
                    continue;
                }

                var box = new PdfRect(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
                if (Math.Abs(box.Left - DrawingArea.Left) < 0.01 && Math.Abs(box.Right - DrawingArea.Right) < 0.01
                    && Math.Abs(box.Bottom - DrawingArea.Bottom) < 0.01 && Math.Abs(box.Top - DrawingArea.Top) < 0.01)
                {
                    continue; // the redaction box
                }

                RectClipping.Overlaps(box, inner).ShouldBeFalse($"a drawing at {box} is still under the area");
            }
        }

        // What shows outside the area is as it was: the line, the band, the picture's left part.
        (await PixelAsync(output, 0, new PdfPoint(100, 642))).ShouldBe(await PixelAsync(input, 0, new PdfPoint(100, 642)));
        (await PixelAsync(output, 0, new PdfPoint(450, 642))).ShouldBe(await PixelAsync(input, 0, new PdfPoint(450, 642)));
        (await PixelAsync(output, 0, new PdfPoint(100, 522))).ShouldBe((B: (byte)255, G: (byte)0, R: (byte)0));
        (await PixelAsync(output, 0, new PdfPoint(500, 522))).ShouldBe((B: (byte)255, G: (byte)0, R: (byte)0));
        (await PixelAsync(output, 0, new PdfPoint(150, 392))).ShouldBe((B: (byte)0, G: (byte)255, R: (byte)0));
        (await PixelAsync(output, 0, new PdfPoint(300, 500))).ShouldBe((B: (byte)0, G: (byte)0, R: (byte)200));

        // The picture itself has the area painted over in its pixels, not only covered.
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        var image = (await document.GetPageObjectsAsync(0, cancellationToken: Token)).Where(o => o.Kind == PageObjectKind.Image).ShouldHaveSingleItem();
        var pixels = PngCodec.Decode((await document.ExtractImageAsync(0, image, Token)).Data);
        Pixel(pixels, 5, 10).ShouldBe((B: (byte)0, G: (byte)255, R: (byte)0), "the left of the picture is kept");
        Pixel(pixels, 35, 10).ShouldBe((B: (byte)0, G: (byte)0, R: (byte)200), "the part under the area is painted over");
    }

    [Fact]
    public async Task InTheVectorMode_TextInASharedDrawing_UnderAnArea_TurnsThatPageIntoAPicture()
    {
        var input = _temp.Combine("form-xobject.pdf");
        using (var pdf = new PdfDocument())
        {
            FontLibrary.Register();
            var font = new XFont(FontFamilies.NotoSans, 12);
            var first = pdf.AddPage();
            var form = new XForm(pdf, XUnit.FromPoint(300), XUnit.FromPoint(40));
            using (var graphics = XGraphics.FromForm(form))
            {
                graphics.DrawString("Mã số 001203004567", font, XBrushes.Black, 0, 25);
            }

            using (var graphics = XGraphics.FromPdfPage(first))
            {
                graphics.DrawImage(form, 72, 100);
            }

            using (var graphics = XGraphics.FromPdfPage(pdf.AddPage()))
            {
                graphics.DrawString("Trang hai 079095001234", font, XBrushes.Black, 72, 100);
            }

            pdf.Save(input);
        }

        var hits = await FindAsync(input, RedactionPattern.NationalId);

        var output = await RedactAsync(input, new RedactParams(Areas(hits), Mode: RedactionMode.Vector));

        RedactOperation.ReportOf(Last!).ShouldNotBeNull().PicturePages.ShouldBe([1], "page 2 is rewritten keeping its text");
        using var pig = PigDocument.Open(output);
        pig.GetPage(1).Letters.ShouldBeEmpty();
        TextNormalizer.ToNfc(Squeeze(pig.GetPage(2).Text)).ShouldBe("Tranghai");
    }

    [Fact]
    public async Task InTheVectorMode_TaggedTextNamingTheData_GoesFromTheContentAndTheStructure()
    {
        var input = WriteTagged();
        var hits = await FindAsync(input, RedactionPattern.NationalId);
        hits.ShouldHaveSingleItem();

        var output = await RedactAsync(input, new RedactParams(Areas(hits), Mode: RedactionMode.Vector));

        var report = RedactOperation.ReportOf(Last!).ShouldNotBeNull();
        report.PicturePages.ShouldBeEmpty();
        report.Structure.ShouldBe(1, "the paragraph's alternate text named the number");
        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var content = System.Text.Encoding.Latin1.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
            content.ShouldNotContain("001 203 004 567", Case.Sensitive, "the actual text of the marked note named it");
            content.ShouldNotContain("001203004567");
            content.ShouldContain("BDC", Case.Sensitive, "the marked content itself stays");
        }

        File.ReadAllBytes(output).AsSpan().IndexOf("001203004567"u8).ShouldBe(-1);
        var text = Squeeze(AllText(output));
        text.ShouldContain("Maso", Case.Sensitive);
        text.ShouldContain("Xemghichu", Case.Sensitive);
        text.ShouldNotContain("001203004567");
    }

    [Theory]
    [InlineData("vanban-vi.pdf", "Số:")]
    [InlineData("basic-vi.pdf", "Việt Nam")]
    public async Task InTheVectorMode_DocumentsWithSeveralFonts_KeepTheirTextOnEveryPage(string sample, string phrase)
    {
        var input = Path.Combine(Path.GetDirectoryName(Samples.Pii)!, sample);
        var areas = new List<RedactionArea>();
        await using (var document = await PdfInspector.OpenAsync(pdfium.Gate, input))
        {
            for (var page = 0; page < Math.Min(document.PageCount, 6); page++)
            {
                var layer = await document.GetPageTextLayerAsync(page, cancellationToken: Token);
                TextSearch.TryCreate(phrase, new TextSearchOptions(), out var search, out _).ShouldBeTrue();
                var rects = search!.FindAll(layer).SelectMany(layer.Rects).ToList();
                if (rects.Count > 0)
                {
                    areas.Add(new RedactionArea(page + 1, rects));
                }
            }
        }

        areas.ShouldNotBeEmpty();
        var output = await RedactAsync(input, new RedactParams(areas, Mode: RedactionMode.Vector));

        RedactOperation.ReportOf(Last!).ShouldNotBeNull().PicturePages.ShouldBeEmpty("every page could be rewritten exactly");
        using var pig = PigDocument.Open(output);
        foreach (var area in areas)
        {
            var letters = pig.GetPage(area.Page).Letters;
            letters.ShouldNotBeEmpty();
            letters.ShouldNotContain(l => l.Value != " " && area.Rects.Any(r => r.Contains(new PdfPoint(l.BoundingBox.Centroid.X, l.BoundingBox.Centroid.Y))));
        }
    }

    /// <summary>
    /// A page with a black line across it (PDF y 642), a blue band (y 502–542), a picture (x 100–300, y 342–442; green on
    /// its left half, yellow on its right) and a line of text at the top.
    /// </summary>
    private string WriteDrawings()
    {
        var path = _temp.Combine("drawings.pdf");
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        FontLibrary.Register();
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            graphics.DrawString("Giữ lại chữ này", new XFont(FontFamilies.NotoSans, 12), XBrushes.Black, 50, 100);
            graphics.DrawLine(new XPen(XColors.Black, 4), 50, 200, 545, 200);
            graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(0, 0, 255)), 50, 300, 495, 40);
            var png = TestImages.Png(40, 20, (x, _) => x < 20 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)0, (byte)255));
            using var image = XImage.FromStream(new MemoryStream(png));
            graphics.DrawImage(image, 100, 400, 200, 100);
        }

        pdf.Save(path);
        return path;
    }

    /// <summary>
    /// A tagged page in Helvetica: "Ma so 001203004567", then a marked note "Xem ghi chu" whose /ActualText names the
    /// number (spaced out, so that the search does not find it there), and a structure element whose /Alt names it too.
    /// </summary>
    private string WriteTagged()
    {
        var path = _temp.Combine("tagged.pdf");
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();
        var font = new PdfDictionary(pdf);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        pdf.Internals.AddObject(font);
        var fonts = new PdfDictionary(pdf);
        fonts.Elements["/F1"] = font.Reference!;
        var resources = new PdfDictionary(pdf);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;
        var content = new PdfDictionary(pdf);
        content.CreateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Ma so 001203004567) Tj ET\n"
            + "/Span <</ActualText (Lien he 001 203 004 567) /MCID 0>> BDC BT /F1 12 Tf 72 650 Td (Xem ghi chu) Tj ET EMC\n"));
        pdf.Internals.AddObject(content);
        page.Elements["/Contents"] = content.Reference!;

        var element = new PdfDictionary(pdf);
        element.Elements.SetName("/Type", "/StructElem");
        element.Elements.SetName("/S", "/P");
        element.Elements.SetString("/Alt", "So 001203004567");
        element.Elements.SetInteger("/K", 0);
        element.Elements["/Pg"] = page.Reference!;
        pdf.Internals.AddObject(element);
        var root = new PdfDictionary(pdf);
        root.Elements.SetName("/Type", "/StructTreeRoot");
        root.Elements["/K"] = new PdfArray(pdf, element.Reference!);
        pdf.Internals.AddObject(root);
        element.Elements["/P"] = root.Reference!;
        pdf.Internals.Catalog.Elements["/StructTreeRoot"] = root.Reference!;
        pdf.Save(path);
        return path;
    }

    private static (byte B, byte G, byte R) Pixel(BgraImage image, int x, int y)
    {
        var at = (y * image.Stride) + (x * BgraImage.BytesPerPixel);
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }
}
