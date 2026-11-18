using System.Globalization;
using System.Text;
using PdfPro.Core.Geometry;
using PdfPro.Core.Imaging;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Organizing;
using PdfPro.Ocr;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Scanning;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using static PdfPro.Operations.Materializing.PdfGraph;

namespace PdfPro.Operations.Tests.Scanning;

/// <summary>
/// The scan tools: blank pages found and removed, turned pages found (orientation detection checked by reading) and
/// turned back, tilted pages measured and straightened, dark edge bands measured and painted white — the last two
/// over the pages' content, their images byte for byte.
/// </summary>
public sealed class ScanOperationsTests(PdfiumFixture pdfium)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSeparatorSheets_OfTheScan_AreFound_AndRemovedAsOneStep()
    {
        var shares = await RemoveBlankPagesOperation.MeasureAsync(pdfium.Gate, Samples.ScanVi, new RecordingProgress(), Token);

        var blank = BlankPageDetector.BlankPages(shares, BlankPageDetector.DefaultThresholdPercent).Order().ToList();
        blank.ShouldBe([3, 6, 9, 13]);
        var operation = new RemoveBlankPagesOperation(Materializer(), pdfium.Loader);
        var plan = PagePlan.Identity("s0", 18, RandomPageKeys.Instance);
        var result = operation.ApplyToPlan(plan, new RemoveBlankPagesParams(PageRanges.Format(blank)), Context(plan));
        result.Count.ShouldBe(14);
        result.Select(p => p.SourcePageIndex).ShouldNotContain(3);
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(plan, new RemoveBlankPagesParams("1-18"), Context(plan)));
    }

    [Fact]
    public async Task ATurnedPage_IsFoundAndTurnedBack_WhenTheEngineIsSure()
    {
        using var temp = new TempDirectory();
        // One page at a time, in order: the third call is page 3 (index 2; blank sheets are not asked about).
        var engine = new OrientationEngine(call => call == 2 ? new OcrOrientation(90, 20, "Latin") : new OcrOrientation(0, 5, "Latin"));

        var turns = await AutoRotateOperation.DetectAsync(pdfium.Gate, engine, Samples.ScanVi, ["vie"], 1, new RecordingProgress(), Token);

        turns.ShouldBe([new PageTurn(2, 90)]);
        engine.Readings.ShouldBe(0, "a sure detection needs no reading");
        var operation = new AutoRotateOperation(Materializer(), pdfium.Loader);
        var plan = PagePlan.Identity("s0", 18, RandomPageKeys.Instance);
        var turned = operation.ApplyToPlan(plan, new AutoRotateParams(turns), Context(plan));
        turned[2].RotationDelta.ShouldBe(90);
        turned.Where((_, i) => i != 2).ShouldAllBe(p => p.RotationDelta == 0);
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(plan, new AutoRotateParams([new PageTurn(2, 45)]), Context(plan)));
        Should.Throw<OperationValidationException>(() => operation.ApplyToPlan(plan, new AutoRotateParams([new PageTurn(18, 90)]), Context(plan)));
    }

    [Fact]
    public async Task TheRealTesseract_FindsTheUpsideDownPage_AndNoOther()
    {
        var engine = RealEngine();

        var turns = await AutoRotateOperation.DetectAsync(pdfium.Gate, engine, Samples.ScanVi, ["vie"], Math.Max(1, Environment.ProcessorCount - 1), new RecordingProgress(), Token);

        turns.ShouldBe([new PageTurn(11, 180)], "page 13 is detected upside down too, but reads better upright");
    }

    [Fact]
    public async Task TiltedPages_AreMeasured_AndStraightened_TheirImagesUntouched()
    {
        using var temp = new TempDirectory();
        var angles = await DeskewOperation.MeasureAsync(pdfium.Gate, Samples.ScanVi, [1, 3, 7], DeskewOperation.DefaultMinDegrees, new RecordingProgress(), Token);

        angles.Select(a => a.Page).ShouldBe([1, 7], "page 4 is a blank sheet");
        angles[0].Degrees.ShouldBe(0.922, 0.2);
        angles[1].Degrees.ShouldBe(-1.145, 0.2);

        var output = temp.Combine("straight.pdf");
        await using (var stream = File.Create(output))
        {
            await new DeskewOperation().ApplyToFileAsync(Samples.ScanVi, stream, new DeskewParams(angles), new RecordingProgress(), Token);
        }

        var after = await DeskewOperation.MeasureAsync(pdfium.Gate, output, [1, 7], 0, new RecordingProgress(), Token);
        after.ShouldAllBe(a => Math.Abs(a.Degrees) < 0.3);
        ImageHashes(output).ShouldBe(ImageHashes(Samples.ScanVi));
        new DeskewOperation().Validate(new DeskewParams([new PageAngle(1, 30)]), new Core.Documents.DocumentInfo(18)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task DarkBandsAlongTheEdges_AreMeasured_AndPaintedWhite()
    {
        using var temp = new TempDirectory();
        var input = temp.Combine("borders.pdf");
        BorderedPdf(input);

        var borders = await CleanBordersOperation.MeasureAsync(pdfium.Gate, input, null, new RecordingProgress(), Token);

        var page = borders.ShouldHaveSingleItem();
        page.Page.ShouldBe(0);
        page.Left.ShouldBe(30 / 595.0, 0.01);
        page.Right.ShouldBe(45 / 595.0, 0.01);
        (page.Top, page.Bottom).ShouldBe((0, 0));
        var output = temp.Combine("clean.pdf");
        await using (var stream = File.Create(output))
        {
            await new CleanBordersOperation().ApplyToFileAsync(input, stream, new CleanBordersParams(borders), new RecordingProgress(), Token);
        }

        (await CleanBordersOperation.MeasureAsync(pdfium.Gate, output, null, new RecordingProgress(), Token)).ShouldBeEmpty();
        var picture = await RenderAsync(output, 0);
        InkCoverage.DarkPercent(picture).ShouldBeGreaterThan(0.5, "the text-like blocks in the middle are still there");
        (await TextAsync(output)).ShouldContain("Middle");
    }

    private PdfSharpPlanMaterializer Materializer() => new(new PdfiumSourceRepairer(pdfium.Gate));

    private static PlanEditContext Context(PagePlan plan)
        => new(new Dictionary<string, IReadOnlyList<PdfSize>> { ["s0"] = [.. Enumerable.Repeat(new PdfSize(595, 842), plan.Count)] }, RandomPageKeys.Instance);

    private static List<string> ImageHashes(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return [.. document.Pages.Cast<PdfPage>()
            .SelectMany(page => AsDictionary(AsDictionary(page.Elements["/Resources"])?.Elements["/XObject"])?.Elements.Values ?? [])
            .Select(AsDictionary).OfType<PdfDictionary>()
            .Where(x => Name(x, "/Subtype") == "/Image")
            .Select(x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(x.Stream.Value)))];
    }

    private Task<GrayImage> RenderAsync(string path, int page)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                return ScanPages.Render(document, page, 72).Picture;
            },
            PdfiumPriority.Normal,
            Token);

    private Task<string> TextAsync(string path)
        => pdfium.Gate.RunAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(pdfium.Gate, path);
                return document.GetPageText(0);
            },
            PdfiumPriority.Normal,
            Token);

    /// <summary>An A4 page with a black band 30 pt wide on the left, a grey one 45 pt wide on the right, and blocks of "text".</summary>
    private static void BorderedPdf(string path)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(595);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(842);
        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        var fonts = new PdfDictionary(document);
        fonts.Elements["/F1"] = font;
        var resources = new PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;
        var content = new StringBuilder("0.05 g 0 0 30 842 re f 0.45 g 550 0 45 842 re f 0 g\n");
        for (var y = 760; y > 100; y -= 24)
        {
            content.Append(CultureInfo.InvariantCulture, $"80 {y} 420 9 re f\n");
        }

        content.Append("BT /F1 12 Tf 250 60 Td (Middle) Tj ET\n");
        var stream = new PdfContent(document);
        stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        document.Internals.AddObject(stream);
        page.Elements["/Contents"] = stream.Reference;
        document.Save(path);
    }

    private static TesseractEngine RealEngine()
    {
        var locator = new TesseractLocator(Path.Combine(Samples.RepositoryRoot, "tools"));
        Assert.SkipUnless(locator.Find(null, OcrDataKind.Best) is { HasOsd: true } found && found.Languages.Contains("vie"), "Tesseract with Vietnamese and orientation data is not installed");
        var runner = new PdfPro.Infrastructure.ExternalTools.ExternalToolRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<PdfPro.Infrastructure.ExternalTools.ExternalToolRunner>.Instance);
        return new TesseractEngine(runner, locator, () => new TesseractSettings());
    }

    /// <summary>An engine whose orientation detection answers by the order it is asked in (0, 1, 2…).</summary>
    private sealed class OrientationEngine(Func<int, OcrOrientation?> orientation) : IOcrEngine
    {
        private int _calls;

        public int Readings { get; private set; }

        public OcrEngineInfo? Describe() => new("Script", "script", OcrDataKind.Best, ["vie"], true);

        public Task<OcrPageResult> RecognizeAsync(OcrImage image, OcrOptions options, CancellationToken cancellationToken)
        {
            Readings++;
            return Task.FromResult(new OcrPageResult([], [], string.Empty, null));
        }

        public Task<OcrOrientation?> DetectOrientationAsync(OcrImage image, CancellationToken cancellationToken)
            => Task.FromResult(orientation(Interlocked.Increment(ref _calls) - 1));
    }
}
