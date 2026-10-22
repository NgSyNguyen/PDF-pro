using PdfPro.Core.Geometry;
using PdfPro.Core.Layout;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Layout;

/// <summary>Golden-file tests of N-up, booklet and poster layouts over samples/.</summary>
public sealed class LayoutOperationTests(PdfiumFixture pdfium) : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PdfiumSourceRepairer _repairer = new(pdfium.Gate);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Operations_AreExports()
    {
        new NUpOperation(_repairer).Id.ShouldBe("layout.nup");
        new BookletOperation(_repairer).Id.ShouldBe("layout.booklet");
        new TileOperation(_repairer).Id.ShouldBe("layout.tile");
        new NUpOperation(_repairer).Kind.ShouldBe(OperationKind.Export);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void FormMatrix_PutsTheDisplayedPageAtTheOrigin(int rotation)
    {
        var visible = new PdfRect(10, 20, 110, 220); // 100 × 200
        var m = PageForms.Matrix(rotation, visible);
        (double X, double Y) Apply(double x, double y) => ((m[0] * x) + (m[2] * y) + m[4], (m[1] * x) + (m[3] * y) + m[5]);

        var corners = new[] { Apply(10, 20), Apply(110, 20), Apply(10, 220), Apply(110, 220) };

        var (width, height) = rotation % 180 == 0 ? (100.0, 200.0) : (200.0, 100.0);
        corners.Select(c => c.X).Min().ShouldBe(0, 1e-9);
        corners.Select(c => c.Y).Min().ShouldBe(0, 1e-9);
        corners.Select(c => c.X).Max().ShouldBe(width, 1e-9);
        corners.Select(c => c.Y).Max().ShouldBe(height, 1e-9);

        // The page's top-left as displayed (PDF y up): unrotated top-left (10, 220); turned 90° clockwise it is the
        // bottom-left corner (10, 20) that ends up at the top-left.
        var displayedTopLeft = rotation switch { 0 => Apply(10, 220), 90 => Apply(10, 20), 180 => Apply(110, 20), _ => Apply(110, 220) };
        displayedTopLeft.X.ShouldBe(0, 1e-9);
        displayedTopLeft.Y.ShouldBe(height, 1e-9);
    }

    [Fact]
    public async Task NUp_ShowsEachPageAsDisplayed_RotationAndCropIncluded()
    {
        var parameters = new NUpParams(4, Border: true);
        var output = await new NUpOperation(_repairer).WriteAsync(Samples.RotatedCropBox, null, parameters, _temp.Combine("nup.pdf"), _temp.Path, new RecordingProgress(), CancellationToken);

        await using var source = await PdfInspector.OpenAsync(pdfium.Gate, Samples.RotatedCropBox);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(1);
        var grid = NUpOperation.Grid(source.PageSizes, parameters);
        document.PageSizes[0].Width.ShouldBe(grid.Sheet.Width, 0.01);
        document.PageSizes[0].Height.ShouldBe(grid.Sheet.Height, 0.01);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 1, Flags = PdfRenderFlags.None }, cancellationToken: CancellationToken);

        // The displayed top-left square of pages 1-4 is red, blue, black and green (see samples/README.md).
        (int R, int G, int B)[] topLeft = [(255, 0, 0), (0, 0, 255), (0, 0, 0), (0, 153, 0)];
        for (var i = 0; i < 4; i++)
        {
            // 28 pt into the page, scaled like the page (the squares cover it; see the materializer crop test).
            var target = NUpLayout.Place(grid.Cell(i), source.PageSizes[i]);
            var inset = 28 * target.Width / source.PageSizes[i].Width;
            var pixel = bitmap.GetPixel((int)(target.Left + inset), (int)(grid.Sheet.Height - target.Top + inset));
            ((double)pixel.R).ShouldBe(topLeft[i].R, tolerance: 12, $"page {i + 1}");
            ((double)pixel.G).ShouldBe(topLeft[i].G, tolerance: 12, $"page {i + 1}");
            ((double)pixel.B).ShouldBe(topLeft[i].B, tolerance: 12, $"page {i + 1}");
        }
    }

    [Fact]
    public async Task NUp_KeepsTextAndFillsCellsInTheChosenOrder()
    {
        var operation = new NUpOperation(_repairer);
        var parameters = new NUpParams(4, Order: NUpOrder.ColumnsFirst, Pages: "1-6");
        var tenPages = await TenNumberedPagesAsync();

        var output = await operation.WriteAsync(tenPages, null, parameters, _temp.Combine("nup.pdf"), _temp.Path, new RecordingProgress(), CancellationToken);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(2);
        operation.SheetCount([.. Enumerable.Repeat(new PdfSize(595, 842), 10)], parameters).ShouldBe(2);
        var texts = await PdfInspector.TextsAsync(document, 0, 1);
        Order(texts[0], 1, 2, 3, 4).ShouldBeTrue("forms are drawn slot by slot");
        texts[1].ShouldContain("trang 5 /");
        texts[1].ShouldContain("trang 6 /");
        texts[1].ShouldNotContain("trang 7 /");
    }

    [Fact]
    public async Task Booklet_TenPages_PrintsTwelveInFoldOrder()
    {
        var operation = new BookletOperation(_repairer);

        var output = await operation.WriteAsync(await TenNumberedPagesAsync(), null, new BookletParams(), _temp.Combine("booklet.pdf"), _temp.Path, new RecordingProgress(), CancellationToken);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        document.PageCount.ShouldBe(6);
        document.PageSizes.ShouldAllBe(s => s.Width > s.Height, "sheets are landscape");
        var texts = await PdfInspector.AllTextsAsync(document);

        // [12|1] [2|11] [10|3] [4|9] [8|5] [6|7]; 11 and 12 are blank.
        Pages(texts[0]).ShouldBe([1]);
        Pages(texts[1]).ShouldBe([2]);
        Pages(texts[2]).ShouldBe([10, 3]);
        Pages(texts[3]).ShouldBe([4, 9]);
        Pages(texts[4]).ShouldBe([8, 5]);
        Pages(texts[5]).ShouldBe([6, 7]);
    }

    [Fact]
    public async Task Booklet_BoundOnTheRight_MirrorsEverySide()
    {
        var output = await new BookletOperation(_repairer).WriteAsync(await TenNumberedPagesAsync(), null, new BookletParams(Binding: BookletBinding.Right), _temp.Combine("rtl.pdf"), _temp.Path, new RecordingProgress(), CancellationToken);

        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        Pages((await PdfInspector.TextsAsync(document, 2))[0]).ShouldBe([3, 10]);
    }

    [Fact]
    public async Task Tile_PrintsALargePageOnLabelledSheets()
    {
        var operation = new TileOperation(_repairer);
        var parameters = new TileParams(Scale: 2, Pages: "2");

        var output = await operation.WriteAsync(Samples.BasicVi, null, parameters, _temp.Combine("tiles.pdf"), _temp.Path, new RecordingProgress(), CancellationToken);

        await using var source = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);
        var (scale, options, columns, rows) = TileOperation.LayoutOf(source.PageSizes[1], parameters);
        await using var document = await PdfInspector.OpenAsync(pdfium.Gate, output);
        scale.ShouldBe(2);
        document.PageCount.ShouldBe(columns * rows);
        document.PageCount.ShouldBe(operation.SheetCount(source.PageSizes, parameters));
        document.PageSizes.ShouldAllBe(s => Math.Abs(s.Width - options.Sheet.Width) < 0.01 && Math.Abs(s.Height - options.Sheet.Height) < 0.01);
        var texts = await PdfInspector.AllTextsAsync(document);
        texts[0].ShouldContain("Chương I");
        texts[0].ShouldEndWith("A1");
        texts[^1].ShouldEndWith($"{(char)('A' + rows - 1)}{columns}");
    }

    [Fact]
    public async Task Tile_FitToSheets_ScalesThePageToTheGrid()
    {
        var parameters = new TileParams(FitColumns: 2, FitRows: 2);
        await using var source = await PdfInspector.OpenAsync(pdfium.Gate, Samples.BasicVi);

        var (scale, _, columns, rows) = TileOperation.LayoutOf(source.PageSizes[0], parameters);

        scale.ShouldBeGreaterThan(1.5);
        (columns, rows).ShouldBe((2, 2));
    }

    [Fact]
    public void Tile_Labels_NameRowsByLetter()
    {
        TileOperation.Label(new PosterTile(0, 0, default, default), null).ShouldBe("A1");
        TileOperation.Label(new PosterTile(2, 3, default, default), 5).ShouldBe("5 · C4");
        TileOperation.Label(new PosterTile(26, 0, default, default), null).ShouldBe("27-1");
    }

    [Fact]
    public async Task InvalidParameters_AreRefused()
    {
        var nup = new NUpOperation(_repairer);
        var tile = new TileOperation(_repairer);

        (await Should.ThrowAsync<OperationValidationException>(() => nup.WriteAsync(Samples.BasicVi, null, new NUpParams(16, Margin: 290), _temp.Combine("x.pdf"), _temp.Path, new RecordingProgress(), CancellationToken)))
            .Errors.Single().MemberName.ShouldBe("Margin");
        (await Should.ThrowAsync<OperationValidationException>(() => nup.WriteAsync(Samples.BasicVi, null, new NUpParams(3), _temp.Combine("x.pdf"), _temp.Path, new RecordingProgress(), CancellationToken)))
            .Errors.ShouldContain(e => e.MemberName == "PagesPerSheet");
        (await Should.ThrowAsync<OperationValidationException>(() => tile.WriteAsync(Samples.BasicVi, null, new TileParams(FitColumns: 2), _temp.Combine("x.pdf"), _temp.Path, new RecordingProgress(), CancellationToken)))
            .Errors.ShouldContain(e => e.MemberName == "FitColumns");
        (await Should.ThrowAsync<OperationValidationException>(() => tile.WriteAsync(Samples.BasicVi, null, new TileParams(Pages: "9"), _temp.Combine("x.pdf"), _temp.Path, new RecordingProgress(), CancellationToken)))
            .Errors.ShouldContain(e => e.MemberName == "Pages");
        File.Exists(_temp.Combine("x.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task OnFiles_TheOutputIsNamedAfterTheInput()
    {
        var context = new OperationContext { InputPaths = [Samples.LinksAnnots], OutputDirectory = _temp.Combine("out"), WorkingDirectory = _temp.Combine("work") };

        var result = await new NUpOperation(_repairer).ExecuteAsync(context, new NUpParams(2), new RecordingProgress(), CancellationToken);

        result.OutputPaths.ShouldBe([_temp.Combine("out", "links-annots-nup.pdf")]);
        PdfInspector.Links(result.OutputPaths[0]).ShouldBeEmpty("annotations do not come along on sheets");
    }

    /// <summary>Pages 1-10 of big-1000.pdf ("… trang N / 1000" on each), written with the split writer.</summary>
    private async Task<string> TenNumberedPagesAsync()
    {
        await using var big = await pdfium.Loader.LoadAsync(Samples.Big1000, null, CancellationToken);
        var document = new SplitDocument
        {
            Plan = PagePlan.Create(PagePlan.Identity("s0", 1000, RandomPageKeys.Instance).Take(10)),
            BaseSourceId = "s0",
            Documents = new Dictionary<string, IPdfDocument> { ["s0"] = big },
            SourcePaths = new Dictionary<string, string> { ["s0"] = Samples.Big1000 },
            Name = "ten",
            WorkingDirectory = _temp.Combine("work"),
        };
        var operation = new SplitOperation(new PdfSharpPlanMaterializer(_repairer), pdfium.Loader);
        var parameters = new SplitParams(SplitMode.EveryN, PagesPerFile: 10, NamePattern: "ten");
        return (await operation.WriteAsync(document, [new([.. Enumerable.Range(0, 10)])], parameters, _temp.Combine("in"), [], new RecordingProgress(), CancellationToken))[0];
    }

    /// <summary>The page numbers ("trang N / 1000") in a sheet's text, in order.</summary>
    private static int[] Pages(string text)
        => [.. System.Text.RegularExpressions.Regex.Matches(text, @"trang (\d+) / 1000").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    private static bool Order(string text, params int[] pages) => Pages(text).SequenceEqual(pages);
}
