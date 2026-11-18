using BenchmarkDotNet.Attributes;
using PdfPro.Core.Organizing;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Ocr;
using PdfPro.Operations.Optimizing;
using PdfPro.Pdfium;

namespace PdfPro.Benchmarks;

/// <summary>Opening the 1,000-page sample the way the viewer does: the document, its first page's size, its first page drawn.</summary>
[InProcess]
[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(15)]
public class OpenBenchmarks
{
    private readonly PdfDocumentLoader _loader = new(BenchmarkServices.Shared.Gate);

    [Benchmark(Description = "Mở 1.000 trang (số trang + khổ trang 1)")]
    public async Task<int> OpenThousandPages()
    {
        await using var document = await _loader.LoadAsync(Samples.Big1000, password: null);
        await document.GetPageGeometryAsync(0);
        return document.PageCount;
    }

    [Benchmark(Description = "Trang đầu của tệp 1.000 trang hiện ra (mở + vẽ 100 %)")]
    public async Task<int> FirstPageOfThousand()
    {
        await using var document = await _loader.LoadAsync(Samples.Big1000, password: null);
        using var bitmap = await document.RenderPageAsync(0, new PageRenderOptions { Scale = 96 / 72.0 });
        return bitmap.Width;
    }
}

/// <summary>One page drawn at three zoom levels, a page of text and a scanned page.</summary>
[InProcess]
[MemoryDiagnoser]
[WarmupCount(3)]
[IterationCount(15)]
public class RenderBenchmarks
{
    private IPdfDocument? _document;

    [Params("vanban-vi.pdf", "scan-vi.pdf")]
    public string Sample { get; set; } = "vanban-vi.pdf";

    [Params(50, 100, 200)]
    public int Zoom { get; set; }

    [GlobalSetup]
    public async Task OpenAsync() => _document = await new PdfDocumentLoader(BenchmarkServices.Shared.Gate).LoadAsync(Samples.Path(Sample), password: null);

    [GlobalCleanup]
    public async Task CloseAsync()
    {
        if (_document is not null)
        {
            await _document.DisposeAsync();
        }
    }

    /// <summary>At the screen's 96 DPI times the zoom.</summary>
    [Benchmark(Description = "Vẽ một trang")]
    public async Task<int> RenderPage()
    {
        using var bitmap = await _document!.RenderPageAsync(1, new PageRenderOptions { Scale = 96 / 72.0 * Zoom / 100 });
        return bitmap.Width;
    }
}

/// <summary>Saving a 1,000-page plan: every third page turned, the first hundred moved to the end, ten deleted.</summary>
[InProcess]
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(5)]
public class MaterializeBenchmarks
{
    private readonly ScratchFolder _scratch = new();
    private readonly PdfSharpPlanMaterializer _materializer = new(new PdfiumSourceRepairer(BenchmarkServices.Shared.Gate));
    private PagePlan _plan = null!;

    [GlobalSetup]
    public void BuildPlan()
    {
        var plan = PagePlan.Identity("s0", 1000, RandomPageKeys.Instance);
        plan = plan.Rotate([.. Enumerable.Range(0, 1000).Where(i => i % 3 == 0)], 90);
        plan = plan.Move([.. Enumerable.Range(0, 100)], 1000);
        _plan = plan.Delete([.. Enumerable.Range(500, 10)]);
    }

    [GlobalCleanup]
    public void Clean() => _scratch.Dispose();

    [Benchmark(Description = "Vật chất hóa plan 1.000 trang")]
    public async Task<long> MaterializeThousandPages()
    {
        var output = _scratch.Combine("plan.pdf");
        await using var stream = File.Create(output);
        await _materializer.MaterializeAsync(
            new MaterializeRequest
            {
                Plan = _plan,
                BaseSourceId = "s0",
                SourcePaths = new Dictionary<string, string> { ["s0"] = Samples.Big1000 },
                WorkingDirectory = _scratch.Combine("work"),
            },
            stream,
            new Progress<Core.Operations.OperationProgress>(),
            CancellationToken.None);
        return stream.Length;
    }
}

/// <summary>Compressing 50 scanned A4 pages (300 DPI colour JPEGs, about 40 MB) for e-books.</summary>
[InProcess]
[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class CompressBenchmarks
{
    private readonly ScratchFolder _scratch = new();
    private string _scan = null!;
    private int _run;

    [GlobalSetup]
    public async Task WriteScanAsync() => _scan = await ScanDocument.WriteAsync(BenchmarkServices.Shared, _scratch.Path, 50);

    [GlobalCleanup]
    public void Clean() => _scratch.Dispose();

    [Benchmark(Description = "Nén 50 trang scan (eBook)")]
    public async Task<int> CompressFiftyScannedPages()
    {
        var outputs = await BenchmarkServices.Shared.RunAsync(CompressOperation.OperationId, _scan, new CompressParams(CompressionPreset.Ebook), _scratch.Combine($"nén {++_run}"));
        return outputs.Count;
    }
}

/// <summary>Recognising the ten text pages of scan-vi.pdf in Vietnamese, one page at a time and all cores at once.</summary>
[InProcess]
[WarmupCount(1)]
[IterationCount(3)]
public class OcrBenchmarks
{
    /// <summary>Pages 1, 2, 3, 5, 6, 8, 9, 11, 12 and 13, 0-based: the separator sheets 4, 7, 10 and 14 are blank.</summary>
    private static readonly IReadOnlyList<int> TextPages = [0, 1, 2, 4, 5, 7, 8, 10, 11, 12];

    private readonly ScratchFolder _scratch = new();
    private int _run;

    /// <summary>Pages recognised at once (0 = every core but one).</summary>
    [Params(1, 0)]
    public int Parallelism { get; set; }

    [GlobalCleanup]
    public void Clean() => _scratch.Dispose();

    [Benchmark(Description = "OCR 10 trang (vie)")]
    public async Task<int> RecogniseTenPages()
    {
        var parameters = new OcrParams(["vie"], Pages: TextPages, Parallelism: Parallelism);
        var outputs = await BenchmarkServices.Shared.RunAsync(OcrOperation.OperationId, Samples.ScanVi, parameters, _scratch.Combine($"ocr {++_run}"));
        return outputs.Count;
    }
}
