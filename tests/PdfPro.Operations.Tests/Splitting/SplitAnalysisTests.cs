using PdfPro.Core.Organizing;
using PdfPro.Core.Splitting;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Splitting;
using PdfPro.Operations.Tests.TestSupport;
using PdfPro.Pdfium;

namespace PdfPro.Operations.Tests.Splitting;

/// <summary>Blank page detection, text matching and size estimates on samples/.</summary>
public sealed class SplitAnalysisTests(PdfiumFixture pdfium) : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<IPdfDocument> _documents = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var document in _documents)
        {
            await document.DisposeAsync();
        }

        _temp.Dispose();
    }

    [Fact]
    public async Task BlankPages_OfTheScan_AreTheSeparatorSheetsOnly()
    {
        var document = await OpenAsync(Samples.ScanVi);
        var progress = new RecordingProgress();

        var shares = await BlankPageDetector.MeasureAsync(document, progress, CancellationToken);

        BlankPageDetector.BlankPages(shares, BlankPageDetector.DefaultThresholdPercent).Order().ShouldBe([3, 6, 9, 13]);
        shares[6].ShouldBeLessThan(0.05, "bleed-through from the back is far lighter than ink");
        shares[11].ShouldBeGreaterThan(0.2, "the upside-down page with two lines of text is not blank");
        progress.Reports[^1].ShouldBe(new(18, 18, BlankPageDetector.Stage));
    }

    [Fact]
    public async Task BlankPages_OfTextDocuments_AreNone()
    {
        foreach (var path in new[] { Samples.VanBanVi, Samples.BasicVi, Samples.LinksAnnots })
        {
            var shares = await BlankPageDetector.MeasureAsync(await OpenAsync(path), new RecordingProgress(), CancellationToken);

            BlankPageDetector.BlankPages(shares, BlankPageDetector.DefaultThresholdPercent).ShouldBeEmpty(path);
        }
    }

    [Fact]
    public async Task BlankPages_OfThePlan_CountAsBlankAndCropsAreMeasuredAlone()
    {
        var source = await OpenAsync(Samples.VanBanVi);
        var plan = source.Plan
            .InsertBlank(1, new(595, 842), RandomPageKeys.Instance)
            .Crop([0], new PageCrop(0, 600, 595, 242)); // the empty lower part of page 1 (the footer is in the margin)
        var document = source with { Plan = plan };

        var shares = await BlankPageDetector.MeasureAsync(document, new RecordingProgress(), CancellationToken);

        shares[1].ShouldBe(0);
        shares[0].ShouldBeLessThan(BlankPageDetector.DefaultThresholdPercent);
        shares[2].ShouldBeGreaterThan(BlankPageDetector.DefaultThresholdPercent);
    }

    [Fact]
    public void DarkShare_CountsPixelsDarkerThanTheLevel()
    {
        // 4 × 2 BGRx pixels, stride 20: white, black, mid grey (128), light grey (200); second row all white.
        var pixels = new byte[40];
        Array.Fill(pixels, (byte)255);
        pixels.AsSpan(4, 4).Fill(0);
        pixels.AsSpan(8, 4).Fill(128);
        pixels.AsSpan(12, 4).Fill(200);

        BlankPageDetector.DarkShare(pixels, 4, 2, 20).ShouldBe(25);
        BlankPageDetector.DarkShare(pixels, 0, 0, 0).ShouldBe(0);
        BlankPageDetector.IsBlank(0.1, 0.1).ShouldBeTrue();
        BlankPageDetector.IsBlank(0.11, 0.1).ShouldBeFalse();
    }

    [Fact]
    public async Task Texts_AreReadOncePerPage()
    {
        var document = await OpenAsync(Samples.VanBanVi);

        var texts = await PageTextMatcher.ReadAsync(document, new RecordingProgress(), CancellationToken);
        PageTextMatcher.TryCreate(@"Số:\s*\d+", matchCase: false, out var regex, out _).ShouldBeTrue();
        var matches = PageTextMatcher.Match(texts, regex!, out var slow);

        texts.Count.ShouldBe(14);
        texts[0].ShouldContain("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM");
        matches.Select((m, i) => (m, i)).Where(x => x.m is not null).Select(x => x.i).ShouldBe([0, 3, 5, 7, 10]);
        matches[0].ShouldBe("Số: 12");
        slow.ShouldBeEmpty();
    }

    [Fact]
    public void Matcher_RefusesBadPatternsAndStopsSlowOnes()
    {
        PageTextMatcher.TryCreate("(", matchCase: false, out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
        PageTextMatcher.TryCreate(" ", matchCase: false, out _, out _).ShouldBeFalse();
        PageTextMatcher.TryCreate("x*", matchCase: false, out var empty, out _).ShouldBeTrue();
        PageTextMatcher.Match(["abc"], empty!, out _).ShouldBe([null], "an empty match starts no file");

        // Catastrophic backtracking: the page counts as not matching instead of hanging.
        PageTextMatcher.TryCreate("^(a+)+$", matchCase: true, out var evil, out _).ShouldBeTrue();
        var matches = PageTextMatcher.Match([new string('a', 40) + "!"], evil!, out var slow);

        matches.ShouldBe([null]);
        slow.ShouldBe([0]);
    }

    [Fact]
    public async Task SizeEstimate_CountsSharedImagesOncePerFile()
    {
        var document = await OpenAsync(Samples.Big1000);

        var estimate = await PageSizeEstimator.EstimateAsync(document, new RecordingProgress(), CancellationToken);

        var whole = SplitPlanner.EstimateSize(new SplitSegment([.. Enumerable.Range(0, 1000)]), estimate.PartsOf, estimate.OverheadBytes);
        var actual = new FileInfo(Samples.Big1000).Length;
        ((double)whole / actual).ShouldBeInRange(0.8, 1.25);
        var shared = estimate.PartsOf(0).Intersect(estimate.PartsOf(5)).ToArray();
        shared.ShouldNotBeEmpty("pages 1 and 6 use the same image");
    }

    [Fact]
    public async Task SizeEstimate_OfOnePage_IsCloseToTheWrittenFile()
    {
        var document = await OpenAsync(Samples.BasicVi);
        var estimate = await PageSizeEstimator.EstimateAsync(document, new RecordingProgress(), CancellationToken);
        var operation = new SplitOperation(new PdfSharpPlanMaterializer(new PdfiumSourceRepairer(pdfium.Gate)), pdfium.Loader);
        var parameters = new SplitParams(SplitMode.Ranges, Ranges: "2");

        var file = (await operation.WriteAsync(document, [new SplitSegment([1])], parameters, _temp.Combine("out"), [], new RecordingProgress(), CancellationToken))[0];

        var estimated = SplitPlanner.EstimateSize(new SplitSegment([1]), estimate.PartsOf, estimate.OverheadBytes);
        ((double)estimated / new FileInfo(file).Length).ShouldBeInRange(0.6, 1.6);
    }

    private async Task<SplitDocument> OpenAsync(string path)
    {
        var document = await pdfium.Loader.LoadAsync(path, password: null, CancellationToken);
        _documents.Add(document);
        return new SplitDocument
        {
            Plan = PagePlan.Identity("s0", document.PageCount, RandomPageKeys.Instance),
            BaseSourceId = "s0",
            Documents = new Dictionary<string, IPdfDocument> { ["s0"] = document },
            SourcePaths = new Dictionary<string, string> { ["s0"] = path },
            Name = Path.GetFileNameWithoutExtension(path),
            WorkingDirectory = _temp.Combine("work"),
        };
    }
}
