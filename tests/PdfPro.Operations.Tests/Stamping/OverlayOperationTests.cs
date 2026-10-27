using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Text;
using PdfPro.Operations.Fonts;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Stamping;

/// <summary>edit.overlay on real pages: positions on rotated and cropped pages, text, pictures, shapes, lines, opacity.</summary>
public sealed class OverlayOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    private readonly TempDirectory _temp = new();
    private readonly OverlayOperation _operation = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operation_IsAContentChange()
    {
        _operation.Id.ShouldBe("edit.overlay");
        _operation.Kind.ShouldBe(OperationKind.Content);
    }

    [Fact]
    public async Task Shapes_LandWhereTheyAreShown_OnPagesWithRotateAndOffsetCropBoxes()
    {
        var geometries = await GeometriesAsync(Samples.RotatedCropBox);
        var items = new List<OverlayItem>();
        for (var i = 0; i < geometries.Length; i++)
        {
            var shown = geometries[i].DisplaySize;
            items.Add(Shape(i, geometries[i], new ViewPoint(shown.Width * 0.4, shown.Height * 0.3), new PdfSize(60, 40), 0, Red));

            // Turned a quarter clockwise: shown 20 wide and 80 tall.
            items.Add(Shape(i, geometries[i], new ViewPoint(shown.Width * 0.6, shown.Height * 0.6), new PdfSize(80, 20), 90, Blue));
        }

        var output = await ApplyAsync(Samples.RotatedCropBox, [.. items]);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        for (var i = 0; i < geometries.Length; i++)
        {
            using var bitmap = await document.RenderPageAsync(i, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
            var shown = geometries[i].DisplaySize;
            var (rx, ry) = (shown.Width * 0.4, shown.Height * 0.3);
            IsColor(bitmap, rx, ry, Red).ShouldBeTrue($"page {i + 1}: centre of the red box");
            IsColor(bitmap, rx + 26, ry + 16, Red).ShouldBeTrue($"page {i + 1}: inside the red box");
            IsColor(bitmap, rx + 36, ry, Red).ShouldBeFalse($"page {i + 1}: right of the red box");
            var (bx, by) = (shown.Width * 0.6, shown.Height * 0.6);
            IsColor(bitmap, bx, by + 35, Blue).ShouldBeTrue($"page {i + 1}: the turned box is tall");
            IsColor(bitmap, bx + 15, by, Blue).ShouldBeFalse($"page {i + 1}: and narrow");
        }
    }

    [Fact]
    public async Task Text_IsWrittenAtItsBaseline_AndBreaksLikeTheEditorShowsIt()
    {
        var blank = Blank(new PdfSize(595, 842));
        var mapper = new PageCoordinateMapper(new PageGeometry(new PdfRect(0, 0, 595, 842), 0), 1);
        const string text = "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc";
        var style = TextStyle.Default with { FontSize = 11 };
        var box = new OverlayBox(mapper.ScreenToPdf(new ViewPoint(300, 60)), new PdfSize(200, 60));

        var output = await ApplyAsync(blank, new TextOverlay(0, box, text, style));

        var metrics = FontLibrary.Instance.Metrics(style.FontFamily, false, false);
        var layout = TextLayout.Layout(text, metrics, style.FontSize, 200 - (2 * OverlayLimits.DefaultPadding));
        layout.Lines.Count.ShouldBeGreaterThan(1);
        using var pig = PigDocument.Open(output);
        var letters = pig.GetPage(1).Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToArray();
        var lines = letters.GroupBy(l => Math.Round(l.StartBaseLine.Y, 1)).OrderByDescending(g => g.Key).ToArray();
        lines.Select(g => string.Concat(g.OrderBy(l => l.StartBaseLine.X).Select(l => l.Value)).Normalize(NormalizationForm.FormC))
            .ShouldBe(layout.Lines.Select(l => l.Text.Replace(" ", string.Empty, StringComparison.Ordinal)));

        // Top of the box at y = 30 (shown), so the first baseline is padding + ascent below it.
        var firstBaseline = 842 - (30 + OverlayLimits.DefaultPadding + layout.Ascent);
        lines[0].Key.ShouldBe(firstBaseline, 0.1);
        lines[0].Min(l => l.StartBaseLine.X).ShouldBe(200 + OverlayLimits.DefaultPadding, 0.1);
        (lines[0].Key - lines[1].Key).ShouldBe(layout.LineHeight, 0.1);
    }

    [Fact]
    public async Task Text_OnATurnedPage_ReadsBack_AndStaysUpright()
    {
        var page = (await GeometriesAsync(Samples.RotatedCropBox))[1];
        page.RotationDegrees.ShouldBe(90);
        var mapper = new PageCoordinateMapper(page, 1);
        var shown = page.DisplaySize;
        var area = new ViewRect(shown.Width * 0.15, shown.Height * 0.4, shown.Width * 0.7, 30);
        var box = new OverlayBox(mapper.ScreenToPdf(new ViewPoint(area.X + (area.Width / 2), area.Y + 15)), new PdfSize(area.Width, 30));
        var style = TextStyle.Default with { FontSize = 16 };

        var output = await ApplyAsync(Samples.RotatedCropBox, new TextOverlay(1, box, "Đóng dấu tiếng Việt", style));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.RotatedCropBox);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.TextsAsync(document, 1))[0].Normalize(NormalizationForm.FormC).ShouldContain("Đóng dấu tiếng Việt");
        using var original = await before.RenderPageAsync(1, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        using var bitmap = await document.RenderPageAsync(1, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        Changed(original, bitmap, area).ShouldBeGreaterThan(80, "the letters run left to right inside the box");
        Changed(original, bitmap, new ViewRect(area.X, area.Bottom + 4, area.Width, 60)).ShouldBe(0, "nothing runs downwards from it");
    }

    [Fact]
    public async Task APng_KeepsItsTransparency()
    {
        var png = _temp.Combine("half.png");
        await File.WriteAllBytesAsync(png, TestImages.Png(40, 20, (x, _) => x < 20 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)0)), Token);
        var blank = Blank(new PdfSize(300, 200));
        var mapper = new PageCoordinateMapper(new PageGeometry(new PdfRect(0, 0, 300, 200), 0), 1);

        var output = await ApplyAsync(blank, new ImageOverlay(0, new OverlayBox(mapper.ScreenToPdf(new ViewPoint(150, 100)), new PdfSize(80, 40)), png));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        IsColor(bitmap, 130, 100, Red).ShouldBeTrue("the opaque half");
        IsColor(bitmap, 170, 100, RgbColor.White).ShouldBeTrue("the transparent half shows the page");
        Images(output).ShouldHaveSingleItem().Elements.ContainsKey("/SMask").ShouldBeTrue();
    }

    [Fact]
    public async Task AJpeg_IsEmbeddedAsItIs()
    {
        var jpeg = TestImages.ScanJpeg();
        var file = _temp.Combine("scan.jpg");
        await File.WriteAllBytesAsync(file, jpeg, Token);
        var page = (await GeometriesAsync(Samples.BasicVi))[0];

        var output = await ApplyAsync(Samples.BasicVi, new ImageOverlay(0, new OverlayBox(new PdfPoint(300, 400), new PdfSize(100, 140)), file, IsSignature: true));

        var image = Images(output).ShouldHaveSingleItem();
        Name(image, "/Filter").ShouldBe("/DCTDecode");
        image.Stream.Value.ShouldBe(jpeg, "not decoded and compressed again");
        page.Box.Width.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task APhotoWithExifOrientation_IsDrawnUpright()
    {
        var jpeg = TestImages.ScanJpeg();
        var plain = _temp.Combine("plain.jpg");
        var turned = _temp.Combine("turned.jpg");
        await File.WriteAllBytesAsync(plain, jpeg, Token);
        await File.WriteAllBytesAsync(turned, TestImages.WithOrientation(jpeg, 6), Token);
        var blank = Blank(new PdfSize(500, 400));
        var mapper = new PageCoordinateMapper(new PageGeometry(new PdfRect(0, 0, 500, 400), 0), 1);
        var center = mapper.ScreenToPdf(new ViewPoint(250, 200));
        var (w, h) = (200.0, 200.0 * 1.414);

        // The tagged photo shown upright (landscape) must look like the untagged one turned a quarter clockwise, and not
        // like the untagged one turned the other way.
        var tagged = await ApplyAsync(blank, new ImageOverlay(0, new OverlayBox(center, new PdfSize(h, w)), turned));
        var rotated = await ApplyAsync(blank, new ImageOverlay(0, new OverlayBox(center, new PdfSize(w, h), 90), plain));
        var wrongWay = await ApplyAsync(blank, new ImageOverlay(0, new OverlayBox(center, new PdfSize(w, h), 270), plain));

        var area = new ViewRect(250 - (h / 2), 200 - (w / 2), h, w);
        var shown = await CellsAsync(tagged, area);
        var expected = await CellsAsync(rotated, area);
        var wrong = await CellsAsync(wrongWay, area);
        shown.Zip(expected, (a, b) => Math.Abs(a - b)).Max().ShouldBeLessThan(6, "upright like the photo turned clockwise");
        shown.Zip(wrong, (a, b) => Math.Abs(a - b)).Max().ShouldBeGreaterThan(20, "the test can tell the two ways apart");
    }

    [Fact]
    public async Task Opacity_LetsThePageShowThrough()
    {
        var blank = Blank(new PdfSize(200, 200));
        var page = new PageGeometry(new PdfRect(0, 0, 200, 200), 0);

        var output = await ApplyAsync(blank, Shape(0, page, new ViewPoint(100, 100), new PdfSize(50, 50), 0, Red) with { Opacity = 0.5 });

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        var (b, g, r) = bitmap.GetPixel(100, 100);
        ((double)r).ShouldBe(255, 6);
        ((double)g).ShouldBe(128, 8);
        ((double)b).ShouldBe(128, 8);
    }

    [Fact]
    public async Task Lines_HaveArrowHeads_AndRoundEnds()
    {
        var blank = Blank(new PdfSize(400, 200));
        var mapper = new PageCoordinateMapper(new PageGeometry(new PdfRect(0, 0, 400, 200), 0), 1);
        var line = new LineOverlay(0, mapper.ScreenToPdf(new ViewPoint(100, 100)), mapper.ScreenToPdf(new ViewPoint(300, 100)), new StrokeStyle(RgbColor.Black, 4), ArrowAtEnd: true);

        var output = await ApplyAsync(blank, line);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        IsColor(bitmap, 200, 100, RgbColor.Black).ShouldBeTrue("the line");
        IsColor(bitmap, 297, 100, RgbColor.Black).ShouldBeTrue("the arrow tip");
        IsColor(bitmap, 288, 104, RgbColor.Black).ShouldBeTrue("the head is wider than the line");
        IsColor(bitmap, 200, 105, RgbColor.White).ShouldBeTrue("the line is 4 points thick");
    }

    [Fact]
    public async Task AWhiteout_HidesWhatIsUnderIt()
    {
        var page = (await GeometriesAsync(Samples.BasicVi))[0];
        var mapper = new PageCoordinateMapper(page, 1);
        using (var pig = PigDocument.Open(Samples.BasicVi))
        {
            var word = pig.GetPage(1).GetWords().First(w => w.Text.Length > 4);
            var area = mapper.PdfRectToScreen(new PdfRect(word.BoundingBox.Left - 2, word.BoundingBox.Bottom - 2, word.BoundingBox.Right + 2, word.BoundingBox.Top + 2));
            var whiteout = new ShapeOverlay(0, OverlayBox.FromView(new RotatedBox(new ViewPoint(area.X + (area.Width / 2), area.Y + (area.Height / 2)), area.Width, area.Height, 0), mapper), ShapeKind.Whiteout);
            await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
            using var original = await before.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
            Ink(original, Inset(area, 2)).ShouldBeGreaterThan(0);

            var output = await ApplyAsync(Samples.BasicVi, whiteout);

            await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
            using var bitmap = await after.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
            Ink(bitmap, Inset(area, 2)).ShouldBe(0, "the word is hidden (visually only — true redaction is Phase 8)");
        }
    }

    [Fact]
    public async Task ThePageKeepsItsTextAnnotationsAndPages()
    {
        var page = (await GeometriesAsync(Samples.LinksAnnots))[0];
        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.LinksAnnots);
        var texts = await PdfInspector.AllTextsAsync(before);

        var output = await ApplyAsync(Samples.LinksAnnots, new TextOverlay(0, new OverlayBox(new PdfPoint(300, 100), new PdfSize(150, 20)), "Ghi chú thêm", TextStyle.Default));

        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        after.PageCount.ShouldBe(6);
        var newTexts = await PdfInspector.AllTextsAsync(after);
        newTexts[0].ShouldContain(texts[0]);
        newTexts[0].Normalize(NormalizationForm.FormC).ShouldContain("Ghi chú thêm");
        newTexts[1..].ShouldBe(texts[1..]);
        PdfInspector.Annotations(output).Select(a => a.Length).ShouldBe(PdfInspector.Annotations(Samples.LinksAnnots).Select(a => a.Length));
        page.Box.Width.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PagesSharingResources_EachGetOnlyTheirOwnObjects()
    {
        var shared = _temp.Combine("shared.pdf");
        using (var document = new PdfDocument())
        {
            var first = document.AddPage();
            var second = document.AddPage();
            using (var graphics = XGraphics.FromPdfPage(first))
            {
                FontLibrary.Register();
                graphics.DrawString("Trang một", new XFont(FontFamilies.NotoSans, 12), XBrushes.Black, 50, 50);
            }

            var resources = AsDictionary(first.Elements["/Resources"])!;
            if (resources.Reference is null)
            {
                document.Internals.AddObject(resources);
            }

            first.Elements["/Resources"] = resources.Reference!;
            second.Elements["/Resources"] = resources.Reference!;
            document.Save(shared);
        }

        var output = await ApplyAsync(
            shared,
            new TextOverlay(0, new OverlayBox(new PdfPoint(200, 400), new PdfSize(150, 20)), "AAAA", TextStyle.Default),
            new TextOverlay(1, new OverlayBox(new PdfPoint(200, 400), new PdfSize(150, 20)), "BBBB", TextStyle.Default));

        using var pig = PigDocument.Open(output);
        pig.GetPage(1).Text.ShouldContain("AAAA");
        pig.GetPage(1).Text.ShouldNotContain("BBBB");
        pig.GetPage(2).Text.ShouldContain("BBBB");
        pig.GetPage(2).Text.ShouldNotContain("AAAA");
    }

    [Fact]
    public async Task ContentThatLeavesTheGraphicsStateChanged_DoesNotMoveTheStamp()
    {
        var page = Blank(new PdfSize(200, 200), content: "1 0 0 1 60 60 cm\n");
        var geometry = new PageGeometry(new PdfRect(0, 0, 200, 200), 0);

        var output = await ApplyAsync(page, Shape(0, geometry, new ViewPoint(40, 40), new PdfSize(20, 20), 0, Red));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        IsColor(bitmap, 40, 40, Red).ShouldBeTrue();
    }

    [Fact]
    public async Task Validation_NamesTheObjectsThatCannotBeWritten()
    {
        var box = new OverlayBox(new PdfPoint(100, 100), new PdfSize(50, 20));
        OverlayItem[] items =
        [
            new TextOverlay(0, box, "x", TextStyle.Default with { FontFamily = "No Such Font" }),
            new ImageOverlay(0, box, _temp.Combine("missing.png")),
            new TextOverlay(9, box, "x", TextStyle.Default),
        ];

        var result = _operation.Validate(new OverlayParams(items), new Core.Documents.DocumentInfo(6));

        result.Errors.Select(e => e.MemberName).ShouldBe(["Items[0]", "Items[1]", "Items[2]"]);
        await Should.ThrowAsync<OperationValidationException>(() => ApplyAsync(Samples.BasicVi, items));
    }

    private static ShapeOverlay Shape(int pageIndex, PageGeometry page, ViewPoint shownCenter, PdfSize size, double rotation, RgbColor fill)
        => new(pageIndex, new OverlayBox(new PageCoordinateMapper(page, 1).ScreenToPdf(shownCenter), size, rotation), ShapeKind.Rectangle, Fill: fill);

    private static bool IsColor(PageBitmap bitmap, double x, double y, RgbColor color)
    {
        var (b, g, r) = bitmap.GetPixel((int)Math.Round(x), (int)Math.Round(y));
        return Math.Abs(r - color.R) <= 40 && Math.Abs(g - color.G) <= 40 && Math.Abs(b - color.B) <= 40;
    }

    /// <summary>Dark pixels (any channel under 128) inside a rectangle of the rendered page.</summary>
    private static int Ink(PageBitmap bitmap, ViewRect area)
    {
        var count = 0;
        for (var y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            for (var x = (int)area.X; x < (int)area.Right; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                count += r < 128 || g < 128 || b < 128 ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Pixels that differ between two renderings inside a rectangle.</summary>
    private static int Changed(PageBitmap before, PageBitmap after, ViewRect area)
    {
        var count = 0;
        for (var y = (int)area.Y; y < (int)area.Bottom; y++)
        {
            for (var x = (int)area.X; x < (int)area.Right; x++)
            {
                var (b1, g1, r1) = before.GetPixel(x, y);
                var (b2, g2, r2) = after.GetPixel(x, y);
                count += Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2) > 30 ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Mean brightness of 8 × 8 cells of a rectangle of the first page, rendered at 2 pixels per point.</summary>
    private async Task<double[]> CellsAsync(string path, ViewRect area)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 2, Flags = PdfRenderFlags.None }, cancellationToken: Token);
        const int Cells = 8;
        var means = new double[Cells * Cells];
        for (var row = 0; row < Cells; row++)
        {
            for (var column = 0; column < Cells; column++)
            {
                var (sum, count) = (0.0, 0);
                for (var y = area.Y + (row * area.Height / Cells); y < area.Y + ((row + 1) * area.Height / Cells); y += 0.5)
                {
                    for (var x = area.X + (column * area.Width / Cells); x < area.X + ((column + 1) * area.Width / Cells); x += 0.5)
                    {
                        var (b, g, r) = bitmap.GetPixel((int)(x * 2), (int)(y * 2));
                        sum += (r + g + b) / 3.0;
                        count++;
                    }
                }

                means[(row * Cells) + column] = sum / count;
            }
        }

        return means;
    }

    private static ViewRect Inset(ViewRect rect, double by) => new(rect.X + by, rect.Y + by, rect.Width - (2 * by), rect.Height - (2 * by));

    /// <summary>The image XObjects of a file, soft masks (transparency of another image) left out.</summary>
    private static PdfDictionary[] Images(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var all = document.Internals.GetAllObjects().OfType<PdfDictionary>().ToArray();
        var masks = all.Select(d => AsDictionary(d.Elements["/SMask"])).OfType<PdfDictionary>().ToHashSet(ReferenceEqualityComparer.Instance);
        return [.. all.Where(d => Name(d, "/Subtype") == "/Image" && d.Stream is not null && !masks.Contains(d))];
    }

    private async Task<PageGeometry[]> GeometriesAsync(string path)
    {
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, path);
        return await PdfInspector.GeometriesAsync(document);
    }

    private string Blank(PdfSize size, string? content = null)
    {
        var path = _temp.Combine($"blank-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height));
        if (content is not null)
        {
            var stream = new PdfContent(document);
            stream.CreateStream(Encoding.ASCII.GetBytes(content));
            document.Internals.AddObject(stream);
            page.Elements["/Contents"] = stream.Reference!;
        }

        document.Save(path);
        return path;
    }

    private async Task<string> ApplyAsync(string input, params OverlayItem[] items)
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await _operation.ApplyToFileAsync(input, stream, new OverlayParams(items), new RecordingProgress(), Token);
        }

        return output;
    }
}
