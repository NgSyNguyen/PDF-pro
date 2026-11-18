using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.Imaging;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Ocr;
using PdfPro.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Pdfium;
using SkiaSharp;

namespace PdfPro.Benchmarks;

/// <summary>The committed samples, found by walking up from the program to the repository.</summary>
public static class Samples
{
    public static string Root { get; } = FindRoot();

    public static string Big1000 => Path("big-1000.pdf");

    public static string VanBanVi => Path("vanban-vi.pdf");

    public static string ScanVi => Path("scan-vi.pdf");

    public static string Path(string name) => System.IO.Path.Combine(Root, "samples", name);

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "samples", "big-1000.pdf")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The samples folder was not found above the benchmark program.");
    }
}

/// <summary>What the operations need, built as the command line builds it (one PDFium gate per process).</summary>
public sealed class BenchmarkServices : IDisposable
{
    private BenchmarkServices()
    {
        Gate = new PdfiumGate(NullLogger<PdfiumGate>.Instance);
        var tools = Path.Combine(Samples.Root, "tools");
        var runner = new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance);
        Tesseract = new TesseractLocator(tools);
        Ocr = new TesseractEngine(runner, Tesseract, () => new TesseractSettings());
        Catalog = StandardOperations.Create(new StandardOperationServices(Gate, runner, tools, Ocr));
    }

    /// <summary>The services of this process.</summary>
    public static BenchmarkServices Shared { get; } = new();

    public PdfiumGate Gate { get; }

    public TesseractLocator Tesseract { get; }

    public TesseractEngine Ocr { get; }

    public OperationCatalog Catalog { get; }

    /// <summary>Whether Tesseract with Vietnamese is installed.</summary>
    public bool HasVietnameseOcr => Tesseract.Find(null, Core.Ocr.OcrDataKind.Best) is { } installation && installation.Languages.Contains("vie");

    /// <summary>Runs an operation on one file into a folder; the files it wrote.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(string operationId, string input, object parameters, string outputFolder)
    {
        var operation = Catalog.Find(operationId) ?? throw new InvalidOperationException($"No operation '{operationId}'.");
        Directory.CreateDirectory(outputFolder);
        var result = await operation.ExecuteAsync(
            new OperationContext { InputPaths = [input], OutputDirectory = outputFolder, WorkingDirectory = outputFolder },
            parameters,
            new Progress<OperationProgress>(),
            CancellationToken.None).ConfigureAwait(false);
        return result.OutputPaths;
    }

    public void Dispose() => Gate.Dispose();
}

/// <summary>A folder of its own under the system's temporary folder, removed afterwards.</summary>
public sealed class ScratchFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfpro-bench-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system to clean.
        }
    }
}

/// <summary>A scan as a scanner writes it: <c>pages</c> A4 pages, each a 300 DPI colour JPEG.</summary>
public static class ScanDocument
{
    /// <summary>
    /// The pages of scan-vi.pdf drawn at 300 DPI (again and again, each copy a little different so no two JPEGs are the
    /// same), written as JPEG quality 92, put byte for byte into a PDF by "convert.fromImages".
    /// </summary>
    public static async Task<string> WriteAsync(BenchmarkServices services, string folder, int pages)
    {
        var images = Directory.CreateDirectory(System.IO.Path.Combine(folder, "images")).FullName;
        var paths = await services.Gate.RunAsync(
            () =>
            {
                using var source = PdfiumDocument.Open(services.Gate, Samples.ScanVi);
                var written = new List<string>();
                for (var i = 0; i < pages; i++)
                {
                    using var bitmap = source.RenderPage(i % source.PageCount, new PageRenderOptions { Scale = (300 + (i / source.PageCount)) / 72.0 });
                    var path = System.IO.Path.Combine(images, $"trang {i + 1:000}.jpg");
                    File.WriteAllBytes(path, ImageResolution.WithJpegResolution(Jpeg(bitmap), 300));
                    written.Add(path);
                }

                return written;
            }).ConfigureAwait(false);
        var output = System.IO.Path.Combine(folder, "scan-50");
        var operation = services.Catalog.Find(FromImagesOperation.OperationId)!;
        var result = await operation.ExecuteAsync(
            new OperationContext { InputPaths = paths, OutputDirectory = output, WorkingDirectory = output },
            new FromImagesParams(DefaultDpi: 300),
            new Progress<OperationProgress>(),
            CancellationToken.None).ConfigureAwait(false);
        Directory.Delete(images, recursive: true);
        return result.OutputPaths[0];
    }

    private static byte[] Jpeg(PageBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, bitmap.Pixels, bitmap.Stride);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}
