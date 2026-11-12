using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Cli;
using PdfPro.Infrastructure.ExternalTools;
using PdfPro.Ocr;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Signing;
using PdfPro.Pdfium;

// Vietnamese text must survive the console code page.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops the work cleanly (intermediate files removed); a second one ends the process.
    e.Cancel = !cancel.IsCancellationRequested;
    cancel.Cancel();
};

var tools = Path.Combine(AppContext.BaseDirectory, "tools");
using var gate = new PdfiumGate(NullLogger<PdfiumGate>.Instance);
var runner = new ExternalToolRunner(NullLogger<ExternalToolRunner>.Instance);
var ocr = new TesseractEngine(runner, new TesseractLocator(tools), () => new TesseractSettings());
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var catalog = StandardOperations.Create(new StandardOperationServices(gate, runner, tools, ocr, Timestamps: new HttpTimestampClient(http)));
var environment = new CliEnvironment(
    catalog,
    gate,
    ocr,
    Console.Out,
    Console.Error,
    Environment.CurrentDirectory,
    Environment.GetEnvironmentVariable,
    Path.Combine(Path.GetTempPath(), "PdfPro"));
return await CliApplication.RunAsync(args, environment, cancel.Token);
