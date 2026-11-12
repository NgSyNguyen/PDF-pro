using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using PdfPro.Benchmarks;
using PdfPro.Pdfium;

// dotnet run -c Release --project benchmarks/PdfPro.Benchmarks -- budgets      the budgets of SPEC Phase 13, quickly
// dotnet run -c Release --project benchmarks/PdfPro.Benchmarks -- --filter '*' every benchmark (OCR needs Tesseract)
if (args is ["budgets", ..])
{
    return await Budgets.RunAsync();
}

var types = new List<Type> { typeof(OpenBenchmarks), typeof(RenderBenchmarks), typeof(MaterializeBenchmarks), typeof(CompressBenchmarks) };
if (BenchmarkServices.Shared.HasVietnameseOcr)
{
    types.Add(typeof(OcrBenchmarks));
}
else
{
    Console.WriteLine("Tesseract with Vietnamese is not installed: the OCR benchmark is left out (tools/fetch-tools.ps1).");
}

BenchmarkSwitcher.FromTypes([.. types]).Run(args, DefaultConfig.Instance.WithArtifactsPath(Path.Combine(Samples.Root, "benchmarks", "BenchmarkDotNet.Artifacts")));
return 0;

/// <summary>
/// The budgets of SPEC Phase 13 measured once, in a fresh process (what a person sees, not an average): the first page
/// of the 1,000-page file, and the memory of viewing it — the document open, 40 thumbnails and 6 pages at 100 % drawn
/// and kept, as the viewer's caches keep them.
/// </summary>
internal static class Budgets
{
    public static async Task<int> RunAsync()
    {
        var services = BenchmarkServices.Shared;
        var clock = Stopwatch.StartNew();
        var loader = new PdfDocumentLoader(services.Gate);
        await using var document = await loader.LoadAsync(Samples.Big1000, password: null);
        var opened = clock.Elapsed;
        var kept = new List<PageBitmap> { await document.RenderPageAsync(0, new PageRenderOptions { Scale = 96 / 72.0 }) };
        var firstPage = clock.Elapsed;

        for (var i = 1; i < 6; i++)
        {
            kept.Add(await document.RenderPageAsync(i, new PageRenderOptions { Scale = 96 / 72.0 }));
        }

        for (var i = 0; i < 40; i++)
        {
            kept.Add(await document.RenderPageAsync(i, new PageRenderOptions { Scale = 0.25 }, PdfiumPriority.Low));
        }

        for (var i = 0; i < document.PageCount; i += 50)
        {
            await document.GetPageGeometryAsync(i);
        }

        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var bitmaps = kept.Sum(b => b.ByteSize);
        kept.ForEach(b => b.Dispose());

        Console.WriteLine("| Đo | Kết quả | Ngân sách |");
        Console.WriteLine("|---|---|---|");
        Line("Mở tệp 1.000 trang", Ms(opened), "—");
        Line("Trang đầu của tệp 1.000 trang hiện ra (mở + vẽ 100 %)", Ms(firstPage), "< 2 s");
        Line("Bộ nhớ khi xem tệp 1.000 trang (working set)", Mb(process.WorkingSet64), "< 800 MB");
        Line("— trong đó ảnh trang đang giữ", Mb(bitmaps), "—");
        Line("Bộ nhớ cao nhất của tiến trình", Mb(process.PeakWorkingSet64), "—");
        Console.WriteLine($"({RuntimeInformation()}; khởi động ứng dụng WPF đo trên Windows)");
        return 0;
    }

    private static void Line(string what, string value, string budget) => Console.WriteLine($"| {what} | {value} | {budget} |");

    private static string Ms(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{time.TotalMilliseconds:N0} ms");

    private static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:N0} MB");

    private static string RuntimeInformation()
        => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} nhân, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}";
}
