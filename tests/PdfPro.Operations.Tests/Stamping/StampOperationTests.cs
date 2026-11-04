using System.Security.Cryptography;
using System.Text;
using PdfPro.Core.Documents;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Stamping;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PdfPro.Operations.Tests.Stamping;

/// <summary>stamp.watermark, stamp.background, stamp.headerFooter, stamp.pageNumbers, stamp.bates and stamp.remove on samples/.</summary>
public sealed class StampOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operations_HaveTheirIdsAndKinds()
    {
        new WatermarkOperation().Id.ShouldBe("stamp.watermark");
        new BackgroundOperation().Id.ShouldBe("stamp.background");
        new HeaderFooterOperation().Id.ShouldBe("stamp.headerFooter");
        new PageNumbersOperation().Id.ShouldBe("stamp.pageNumbers");
        new RemoveStampsOperation().Kind.ShouldBe(OperationKind.Content);
        new BatesOperation().Kind.ShouldBe(OperationKind.Export);
    }

    [Fact]
    public async Task ATextWatermark_IsLightTurnedSearchableAndInItsLayer()
    {
        var output = await ApplyAsync(new WatermarkOperation(), Samples.BasicVi, new WatermarkParams("BẢN NHÁP", Pages: "1"));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        (await PdfInspector.TextsAsync(after, 0))[0].Normalize(NormalizationForm.FormC).ShouldContain("BẢN NHÁP");
        (await PdfInspector.TextsAsync(after, 1))[0].ShouldBe((await PdfInspector.TextsAsync(before, 1))[0], "only page 1 was chosen");
        using var original = await RenderAsync(before, 0);
        using var stamped = await RenderAsync(after, 0);
        var changed = Changes(original, stamped);
        changed.Count.ShouldBeGreaterThan(500);

        // Rising diagonal through the centre: changes above-right and below-left of it, none in the corners off it.
        var (w, h) = (stamped.Width, stamped.Height);
        changed.ShouldContain(p => p.X > w * 0.6 && p.Y < h * 0.45);
        changed.ShouldContain(p => p.X < w * 0.4 && p.Y > h * 0.55);
        changed.ShouldNotContain(p => p.X < w * 0.3 && p.Y < h * 0.35);
        Layers(output).ShouldBe(["Watermark"]);
    }

    [Fact]
    public async Task ATiledWatermark_CoversThePage()
    {
        var blank = Blank(new PdfSize(595, 842));

        var output = await ApplyAsync(new WatermarkOperation(), blank, new WatermarkParams("MẬT", Style: WatermarkParams.DefaultStyle with { FontSize = 24 }, Tile: true, Opacity: 1));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await RenderAsync(document, 0);
        foreach (var (x, y) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
        {
            Ink(bitmap, new ViewRect(x * 297.5, y * 421, 297.5, 421), dark: 200).ShouldBeGreaterThan(100, $"quarter {x},{y}");
        }
    }

    [Fact]
    public async Task UnderTheContent_AScanHidesTheWatermark_OverItShowsIt()
    {
        var under = await ApplyAsync(new WatermarkOperation(), Samples.ScanVi, new WatermarkParams("BẢN SAO", Pages: "1", BehindContent: true, Opacity: 1));
        var over = await ApplyAsync(new WatermarkOperation(), Samples.ScanVi, new WatermarkParams("BẢN SAO", Pages: "1", Opacity: 1));

        await using var original = await PdfInspector.OpenAsync(pdfium.Gate, Samples.ScanVi);
        await using var hidden = await PdfInspector.OpenAsync(pdfium.Gate, under);
        await using var shown = await PdfInspector.OpenAsync(pdfium.Gate, over);
        using var a = await RenderAsync(original, 0);
        using var b = await RenderAsync(hidden, 0);
        using var c = await RenderAsync(shown, 0);
        Changes(a, b).Count.ShouldBe(0, "the opaque scan covers what is under it");
        Changes(a, c).Count.ShouldBeGreaterThan(500);
    }

    [Fact]
    public async Task APictureWatermark_TakesItsShareOfThePageWidth()
    {
        var png = _temp.Combine("red.png");
        await File.WriteAllBytesAsync(png, TestImages.Png(20, 10, (_, _) => (255, 0, 0, 255)), Token);
        var blank = Blank(new PdfSize(400, 400));

        var output = await ApplyAsync(new WatermarkOperation(), blank, new WatermarkParams(ImagePath: png, ImageScale: 0.5, Rotation: 0, Opacity: 1));

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var bitmap = await RenderAsync(document, 0);
        IsRed(bitmap, 105, 200).ShouldBeTrue();
        IsRed(bitmap, 295, 200).ShouldBeTrue();
        IsRed(bitmap, 95, 200).ShouldBeFalse("200 points wide: from 100 to 300");
        IsRed(bitmap, 200, 145).ShouldBeFalse("and 100 high: from 150 to 250");
    }

    [Fact]
    public async Task ABackground_IsUnderTheText()
    {
        var output = await ApplyAsync(new BackgroundOperation(), Samples.BasicVi, new BackgroundParams(new RgbColor(255, 240, 200)));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        using var original = await RenderAsync(before, 0);
        using var tinted = await RenderAsync(after, 0);
        var (b, g, r) = tinted.GetPixel(5, 5);
        (r, g, b).ShouldBe(((byte)255, (byte)240, (byte)200), "the margin is tinted");
        var (text, covered) = (0, 0);
        for (var y = 0; y < original.Height; y++)
        {
            for (var x = 0; x < original.Width; x++)
            {
                var (b0, g0, r0) = original.GetPixel(x, y);
                if ((r0 + g0 + b0) / 3 < 60)
                {
                    text++;
                    var (b1, g1, r1) = tinted.GetPixel(x, y);
                    covered += (r1 + g1 + b1) / 3 >= 90 ? 1 : 0;
                }
            }
        }

        text.ShouldBeGreaterThan(1000);
        covered.ShouldBe(0, "the text is still on top");
        Layers(output).ShouldBe(["Background"]);
        (await PdfInspector.AllTextsAsync(after)).ShouldBe(await PdfInspector.AllTextsAsync(before));
    }

    [Fact]
    public async Task HeadersAndFooters_SkipTheCover_AndNumberTheRest()
    {
        // vanban-vi.pdf has its own centred footers ("Trang 1/3"), so ours goes to the bottom right.
        var parameters = new HeaderFooterParams(
            TopRight: "{filename} – {date:dd/MM/yyyy}",
            BottomRight: "Trang {page}/{pages}",
            SkipFirstPage: true,
            Date: new DateTime(2026, 10, 5),
            FileName: "Hồ sơ");

        var output = await ApplyAsync(new HeaderFooterOperation(), Samples.VanBanVi, parameters);

        using var pig = PigDocument.Open(output);
        static bool Footer(UglyToad.PdfPig.Content.Letter l) => l.StartBaseLine.Y < 60 && l.StartBaseLine.X > 400;
        static bool Header(UglyToad.PdfPig.Content.Letter l) => l.StartBaseLine.Y > 780 && l.StartBaseLine.X > 300;
        Region(pig, 1, Footer).ShouldBeEmpty("the cover has none");
        Region(pig, 1, Header).ShouldBeEmpty();
        Region(pig, 2, Footer).ShouldBe("Trang1/13");
        Region(pig, 14, Footer).ShouldBe("Trang13/13");
        Region(pig, 2, Header).ShouldBe("Hồsơ–05/10/2026");

        // Both end at the right margin; the footer sits just above the bottom margin.
        var page = pig.GetPage(2);
        page.Letters.Where(Header).Max(l => l.EndBaseLine.X).ShouldBe(page.Width - 36, 1.5);
        var footer = page.Letters.Where(Footer).ToArray();
        footer.Max(l => l.EndBaseLine.X).ShouldBe(page.Width - 36, 1.5);
        footer.Min(l => l.StartBaseLine.Y).ShouldBeInRange(24, 40);
    }

    [Fact]
    public async Task HeaderFooterPreviewTexts_AreWhatTheOperationWrites()
    {
        var parameters = new HeaderFooterParams(
            TopRight: "{filename} – {date:dd/MM/yyyy}",
            BottomRight: "Trang {page}/{pages}",
            SkipFirstPage: true,
            StartNumber: 5,
            NumberStyle: PageNumberStyle.UpperRoman,
            Date: new DateTime(2026, 10, 5),
            FileName: "Hồ sơ",
            Pages: "1-12");

        var output = await ApplyAsync(new HeaderFooterOperation(), Samples.VanBanVi, parameters);

        using var pig = PigDocument.Open(output);
        static bool Footer(UglyToad.PdfPig.Content.Letter l) => l.StartBaseLine.Y < 60 && l.StartBaseLine.X > 400;
        static bool Header(UglyToad.PdfPig.Content.Letter l) => l.StartBaseLine.Y > 780 && l.StartBaseLine.X > 300;
        for (var page = 0; page < pig.NumberOfPages; page++)
        {
            var texts = HeaderFooterOperation.TextsOn(parameters, page, pig.NumberOfPages, "Hồ sơ", title: null, new DateTime(2026, 10, 5));
            Region(pig, page + 1, Footer).ShouldBe(Compact(texts.GetValueOrDefault(StampAnchor.BottomRight)), $"footer of page {page + 1}");
            Region(pig, page + 1, Header).ShouldBe(Compact(texts.GetValueOrDefault(StampAnchor.TopRight)), $"header of page {page + 1}");
        }

        HeaderFooterOperation.TextsOn(parameters, 0, 14, "Hồ sơ", null, DateTime.Now).ShouldBeEmpty("the cover is skipped");
        HeaderFooterOperation.TextsOn(parameters, 12, 14, "Hồ sơ", null, DateTime.Now).ShouldBeEmpty("outside the pages");
        HeaderFooterOperation.TextsOn(parameters, 1, 14, "Hồ sơ", null, DateTime.Now)[StampAnchor.BottomRight].ShouldBe("Trang V/XV");
    }

    [Fact]
    public async Task PageNumbersPreviewText_IsWhatTheOperationWrites_WithTheGivenFileName()
    {
        var parameters = new PageNumbersParams("{page}/{pages} {filename}", PageNumberStyle.LowerRoman, StampAnchor.BottomRight, FirstPage: 3, StartNumber: 2, FileName: "HoSo");

        var output = await ApplyAsync(new PageNumbersOperation(), Samples.BasicVi, parameters);

        using var pig = PigDocument.Open(output);
        for (var page = 0; page < pig.NumberOfPages; page++)
        {
            Region(pig, page + 1, l => l.StartBaseLine.Y < 60 && l.StartBaseLine.X > 400)
                .ShouldBe(Compact(PageNumbersOperation.TextOn(parameters, page, pig.NumberOfPages, "HoSo", title: null)), $"page {page + 1}");
        }

        PageNumbersOperation.TextOn(parameters, 1, 6, "HoSo", null).ShouldBeNull("numbering starts on page 3");
        PageNumbersOperation.TextOn(parameters, 2, 6, "HoSo", null).ShouldBe("ii/v HoSo");
    }

    [Fact]
    public async Task PageNumbers_StartOnAChosenPage_InRomanNumerals()
    {
        // basic-vi.pdf has its own centred footer, so the numbers go to the bottom right.
        var output = await ApplyAsync(new PageNumbersOperation(), Samples.BasicVi, new PageNumbersParams(Style: PageNumberStyle.LowerRoman, Anchor: StampAnchor.BottomRight, FirstPage: 3, StartNumber: 1));

        using var pig = PigDocument.Open(output);
        string Corner(int page) => Region(pig, page, l => l.StartBaseLine.Y < 60 && l.StartBaseLine.X > 450);
        Corner(1).ShouldBeEmpty();
        Corner(2).ShouldBeEmpty();
        Corner(3).ShouldBe("i");
        Corner(5).ShouldBe("iii");
        Corner(6).ShouldBe("iv");
    }

    [Fact]
    public async Task PageNumbers_SitAtTheShownBottom_OfTurnedPages()
    {
        var output = await ApplyAsync(new PageNumbersOperation(), Samples.RotatedCropBox, new PageNumbersParams("Trang {page}"));

        await using var before = await PdfInspector.OpenAsync(pdfium.Gate, Samples.RotatedCropBox);
        await using var after = await PdfInspector.OpenAsync(pdfium.Gate, output);
        for (var i = 0; i < 4; i++)
        {
            using var original = await RenderAsync(before, i);
            using var numbered = await RenderAsync(after, i);
            var changes = Changes(original, numbered);
            changes.ShouldNotBeEmpty($"page {i + 1}");
            changes.ShouldAllBe(p => p.Y > numbered.Height - 45 && Math.Abs(p.X - (numbered.Width / 2)) < 60, $"page {i + 1}: bottom centre as shown");
            (await PdfInspector.TextsAsync(after, i))[0].ShouldContain($"Trang {i + 1}");
        }
    }

    [Fact]
    public async Task Bates_RunsOnAcrossFiles_AndLeavesTheInputsAlone()
    {
        string[] inputs = [Copy(Samples.BasicVi, "a.pdf"), Copy(Samples.LinksAnnots, "b.pdf"), Copy(Samples.VanBanVi, "c.pdf")];
        var hashes = inputs.Select(Hash).ToArray();
        var folder = _temp.Combine("bates");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "b.pdf"), "already here", Token);

        var result = await new BatesOperation().WriteAsync(inputs, new BatesParams("HS-", StartNumber: 120), folder, new RecordingProgress(), Token);

        result.OutputPaths.Select(Path.GetFileName).ShouldBe(["a.pdf", "b (2).pdf", "c.pdf"]);
        result.NextNumber.ShouldBe(146);
        result.Ranges.Select(r => (r.First, r.Last)).ShouldBe([(120L, 125L), (126L, 131L), (132L, 145L)]);
        using (var first = PigDocument.Open(result.OutputPaths[0]))
        {
            first.GetPage(1).Text.ShouldContain("HS-000120");
            first.GetPage(6).Text.ShouldContain("HS-000125");
        }

        using (var second = PigDocument.Open(result.OutputPaths[1]))
        {
            second.GetPage(1).Text.ShouldContain("HS-000126");
        }

        using (var third = PigDocument.Open(result.OutputPaths[2]))
        {
            third.GetPage(14).Text.ShouldContain("HS-000145");
        }

        inputs.Select(Hash).ShouldBe(hashes);
        Directory.GetFiles(folder, "*.tmp", SearchOption.TopDirectoryOnly).ShouldBeEmpty();
        (await File.ReadAllTextAsync(Path.Combine(folder, "b.pdf"), Token)).ShouldBe("already here");
    }

    [Fact]
    public async Task Bates_WritesNothing_WhenAFileCannotBeRead()
    {
        string[] inputs = [Copy(Samples.BasicVi, "a.pdf"), Copy(Samples.Encrypted, "locked.pdf")];
        var folder = _temp.Combine("bates");

        var error = await Should.ThrowAsync<BatesFileException>(() => new BatesOperation().WriteAsync(inputs, new BatesParams(), folder, new RecordingProgress(), Token));

        error.FileIndex.ShouldBe(1);
        (Directory.Exists(folder) ? Directory.GetFiles(folder) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task RemovingStamps_TakesOffOnlyTheChosenKinds_AndThenThePageIsAsBefore()
    {
        var stamped = await ApplyAsync(new BackgroundOperation(), Samples.BasicVi, new BackgroundParams(new RgbColor(230, 240, 255)));
        stamped = await ApplyAsync(new WatermarkOperation(), stamped, new WatermarkParams("MẪU"));
        stamped = await ApplyAsync(new HeaderFooterOperation(), stamped, new HeaderFooterParams(BottomRight: "{page}"));
        stamped = await ApplyAsync(new OverlayOperation(), stamped, new TextOverlay(0, new OverlayBox(new PdfPoint(300, 100), new PdfSize(100, 20)), "Giữ lại", TextStyle.Default));
        RemoveStampsOperation.Scan(stamped).ShouldBe(new Dictionary<StampKind, int> { [StampKind.Background] = 6, [StampKind.Watermark] = 6, [StampKind.HeaderFooter] = 6 }, ignoreOrder: true);

        var withoutWatermark = await ApplyAsync(new RemoveStampsOperation(), stamped, new RemoveStampsParams([StampKind.Watermark]));
        RemoveStampsOperation.Scan(withoutWatermark).Keys.ShouldBe([StampKind.Background, StampKind.HeaderFooter], ignoreOrder: true);
        Layers(withoutWatermark).ShouldBe(["Background", "Header/Footer"], ignoreOrder: true);

        var clean = await ApplyAsync(new RemoveStampsOperation(), withoutWatermark, new RemoveStampsParams([StampKind.Background, StampKind.HeaderFooter]));
        RemoveStampsOperation.Scan(clean).ShouldBeEmpty();
        Layers(clean).ShouldBeEmpty();
        await using var withText = await PdfInspector.OpenAsync(pdfium.Gate, clean);
        (await PdfInspector.TextsAsync(withText, 0))[0].Normalize(NormalizationForm.FormC).ShouldContain("Giữ lại", customMessage: "objects applied in the editor are content, not stamps");

        var reference = await ApplyAsync(new OverlayOperation(), Samples.BasicVi, new TextOverlay(0, new OverlayBox(new PdfPoint(300, 100), new PdfSize(100, 20)), "Giữ lại", TextStyle.Default));
        await using var expected = await PdfInspector.OpenAsync(pdfium.Gate, reference);
        for (var i = 0; i < 6; i++)
        {
            using var a = await RenderAsync(expected, i);
            using var b = await RenderAsync(withText, i);
            Changes(a, b).Count.ShouldBe(0, $"page {i + 1} shows what it showed before the stamps");
        }
    }

    [Fact]
    public void Validation_ExplainsWhatIsMissingOrWrong()
    {
        var document = new DocumentInfo(6);
        new WatermarkOperation().Validate(new WatermarkParams("x", ImagePath: "y.png"), document).Errors.Select(e => e.MemberName).ShouldContain("Text");
        new WatermarkOperation().Validate(new WatermarkParams(), document).IsValid.ShouldBeFalse();
        new WatermarkOperation().Validate(new WatermarkParams("x", Style: TextStyle.Default with { FontFamily = "Nope" }), document).Errors.Single().MemberName.ShouldBe("Style");
        new BackgroundOperation().Validate(new BackgroundParams(), document).IsValid.ShouldBeFalse();
        new HeaderFooterOperation().Validate(new HeaderFooterParams(), document).IsValid.ShouldBeFalse();
        new HeaderFooterOperation().Validate(new HeaderFooterParams(TopLeft: "{author}"), document).Errors.Single().MemberName.ShouldBe("TopLeft");
        new PageNumbersOperation().Validate(new PageNumbersParams(FirstPage: 7), document).Errors.Single().MemberName.ShouldBe("FirstPage");
        new PageNumbersOperation().Validate(new PageNumbersParams(Pages: "9"), document).Errors.Single().MemberName.ShouldBe("Pages");
        new BatesOperation().Validate(new BatesParams(Digits: 16), document).IsValid.ShouldBeFalse();
        new RemoveStampsOperation().Validate(new RemoveStampsParams([]), document).IsValid.ShouldBeFalse();
    }

    /// <summary>The letters of a page region in reading order, spaces left out, NFC.</summary>
    private static string Region(PigDocument document, int page, Func<UglyToad.PdfPig.Content.Letter, bool> where)
        => string.Concat(document.GetPage(page).Letters.Where(where).Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .OrderByDescending(l => Math.Round(l.StartBaseLine.Y)).ThenBy(l => l.StartBaseLine.X).Select(l => l.Value)).Normalize(NormalizationForm.FormC);

    /// <summary>A text as <see cref="Region"/> reads it back: spaces left out, NFC.</summary>
    private static string Compact(string? text)
        => string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c))).Normalize(NormalizationForm.FormC);

    private static string[] Layers(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return AsDictionary(document.Internals.Catalog.Elements["/OCProperties"]) is { } properties && AsArray(properties.Elements["/OCGs"]) is { } groups
            ? [.. groups.Elements.Select(AsDictionary).OfType<PdfDictionary>().Select(g => g.Elements.GetString("/Name"))]
            : [];
    }

    private static async Task<PageBitmap> RenderAsync(PdfDocumentHandle document, int page)
        => await document.RenderPageAsync(page, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: Token);

    private static List<(int X, int Y)> Changes(PageBitmap before, PageBitmap after)
    {
        var changes = new List<(int, int)>();
        for (var y = 0; y < Math.Min(before.Height, after.Height); y++)
        {
            for (var x = 0; x < Math.Min(before.Width, after.Width); x++)
            {
                var (b1, g1, r1) = before.GetPixel(x, y);
                var (b2, g2, r2) = after.GetPixel(x, y);
                if (Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2) > 24)
                {
                    changes.Add((x, y));
                }
            }
        }

        return changes;
    }

    private static int Ink(PageBitmap bitmap, ViewRect area, int dark)
    {
        var count = 0;
        for (var y = (int)area.Y; y < (int)area.Bottom && y < bitmap.Height; y++)
        {
            for (var x = (int)area.X; x < (int)area.Right && x < bitmap.Width; x++)
            {
                var (b, g, r) = bitmap.GetPixel(x, y);
                count += (r + g + b) / 3 < dark ? 1 : 0;
            }
        }

        return count;
    }

    private static bool IsRed(PageBitmap bitmap, int x, int y)
    {
        var (b, g, r) = bitmap.GetPixel(x, y);
        return r > 200 && g < 60 && b < 60;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private string Copy(string sample, string name)
    {
        var path = _temp.Combine("in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(sample, path);
        return path;
    }

    private string Blank(PdfSize size)
    {
        var path = _temp.Combine($"blank-{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height));
        document.Save(path);
        return path;
    }

    private async Task<string> ApplyAsync<TParams>(DocumentContentOperation<TParams> operation, string input, TParams parameters)
        where TParams : class
    {
        var output = _temp.Combine($"out-{Guid.NewGuid():N}.pdf");
        var stream = File.Create(output);
        await using (stream)
        {
            await operation.ApplyToFileAsync(input, stream, parameters, new RecordingProgress(), Token);
        }

        return output;
    }

    private Task<string> ApplyAsync(OverlayOperation operation, string input, params OverlayItem[] items)
        => ApplyAsync<OverlayParams>(operation, input, new OverlayParams(items));
}
