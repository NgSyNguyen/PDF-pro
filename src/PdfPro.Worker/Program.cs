using System.IO.Pipes;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Infrastructure.Settings;
using PdfPro.Ocr;
using PdfPro.Operations.Automation;
using PdfPro.Pdfium;
using PdfPro.Worker;

// PdfPro.Worker <pipe name> [<data folder>] — started by the app (WorkerClient), never by hand.
if (args.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(args[0]))
{
    await Console.Error.WriteLineAsync("PdfPro.Worker is started by PDF_Pro.");
    return 2;
}

var paths = args.Length == 2 ? new AppPaths(args[1], Path.Combine(AppContext.BaseDirectory, "tools")) : new AppPaths();
var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
using var gate = new PdfiumGate(NullLogger<PdfiumGate>.Instance);
var runner = new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance);
var ocr = new TesseractEngine(
    runner,
    new TesseractLocator(paths.ToolsDirectory),
    () => new TesseractSettings(string.IsNullOrWhiteSpace(settings.Current.TesseractPath) ? null : settings.Current.TesseractPath.Trim(), settings.Current.OcrData));
var catalog = StandardOperations.Create(new StandardOperationServices(gate, runner, paths.ToolsDirectory, ocr));

await using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
try
{
    await pipe.ConnectAsync(20_000);
}
catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
{
    return 3;
}

await new WorkerServer(pipe, catalog, [new PdfPro.Operations.Comparing.ComparisonMeasurement(gate)], new WorkflowRunner(catalog, gate, ocr)).ServeAsync();
return 0;
