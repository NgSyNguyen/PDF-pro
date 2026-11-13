using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Security;
using PdfPro.Operations.Stamping;

namespace PdfPro.Worker.Tests;

/// <summary>
/// PdfPro.Worker started for real (SPEC Phase 13): operations run in it from file to file, their reports come back,
/// a cancelled run stops, and a worker that dies or overruns fails only its run — the next run starts a new worker.
/// </summary>
public sealed class WorkerTests : IAsyncDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdfpro-worker-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly WorkerClient _client;

    public WorkerTests() => _client = Client(TimeSpan.FromMinutes(2));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string RepositoryRoot { get; } = FindRoot();

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the system.
        }
    }

    [Fact]
    public async Task AnExport_RunsInTheWorker_AndItsResultComesBack()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_folder, "văn bản")).FullName;
        var run = new WorkerRun(PdfPro.Operations.Converting.ToTextOperation.OperationId, JsonSerializer.Serialize(new PdfPro.Operations.Converting.ToTextParams(), ParameterSchema.Json), Sample("basic-vi.pdf"), folder)
        {
            Export = true,
            Name = "hồ sơ",
        };

        var json = await _client.RunAsync(run, new Recorded(), Token);

        var result = JsonSerializer.Deserialize<PdfPro.Operations.Converting.ToTextResult>(json.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        result.PageCount.ShouldBe(6);
        result.Files.ShouldHaveSingleItem().ShouldBe(Path.Combine(folder, "hồ sơ.txt"));
        File.ReadAllText(result.Files[0]).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AnExportsError_ComesBackWithWhatTheAppExplains()
    {
        var notAPicture = Path.Combine(_folder, "ảnh.png");
        await File.WriteAllTextAsync(notAPicture, "không phải ảnh", Token);
        var run = new WorkerRun(PdfPro.Operations.Converting.FromImagesOperation.OperationId, JsonSerializer.Serialize(new PdfPro.Operations.Converting.FromImagesParams(), ParameterSchema.Json), notAPicture, Path.Combine(_folder, "ảnh.pdf"))
        {
            Export = true,
            Inputs = [Sample("basic-vi.pdf"), notAPicture],
        };

        var error = await Should.ThrowAsync<WorkerOperationException>(() => _client.RunAsync(run, new Recorded(), Token));

        error.ErrorType.ShouldBe(typeof(PdfPro.Operations.Converting.ImageFileException).FullName);
        error.Detail.ShouldNotBeNull().Split('\n')[0].ShouldBe("0", "the first file is not a picture");
    }

    [Fact]
    public async Task AContentOperation_RunsInTheWorker_WithItsProgress()
    {
        var output = Path.Combine(_folder, "số trang.pdf");
        var progress = new Recorded();

        var report = await _client.RunAsync(Run(PageNumbersOperation.OperationId, new PageNumbersParams(), Sample("basic-vi.pdf"), output), progress, Token);

        report.ShouldBeNull();
        using var original = UglyToad.PdfPig.PdfDocument.Open(Sample("basic-vi.pdf"));
        using var document = UglyToad.PdfPig.PdfDocument.Open(output);
        document.NumberOfPages.ShouldBe(original.NumberOfPages);
        document.GetPage(2).Text.Length.ShouldBeGreaterThan(original.GetPage(2).Text.Length, "the page number was added");
        progress.Reports.ShouldNotBeEmpty();
        var worker = Process.GetProcessById(_client.ProcessId.ShouldNotBeNull());
        worker.Id.ShouldNotBe(Environment.ProcessId);
        worker.PriorityClass.ShouldBe(ProcessPriorityClass.BelowNormal, "heavy work leaves the processor to the window first");
    }

    [Fact]
    public async Task TheReport_OfAReportingOperation_ComesBack()
    {
        var report = await _client.RunAsync(Run(SanitizeOperation.OperationId, new SanitizeParams(), Sample("links-annots.pdf"), Path.Combine(_folder, "sạch.pdf")), new Recorded(), Token);

        var read = JsonSerializer.Deserialize<SanitizeReport>(report.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        File.Exists(Path.Combine(_folder, "sạch.pdf")).ShouldBeTrue();
        read.ShouldNotBeNull();
    }

    [Fact]
    public async Task ParametersThatDoNotFit_AreRefused_AsInTheApp()
    {
        var bad = new RedactParams([new RedactionArea(99, [new PdfRect(0, 0, 10, 10)])]);

        var error = await Should.ThrowAsync<OperationValidationException>(() => _client.RunAsync(Run(RedactOperation.OperationId, bad, Sample("basic-vi.pdf"), Path.Combine(_folder, "x.pdf")), new Recorded(), Token));

        error.OperationId.ShouldBe(RedactOperation.OperationId);
        error.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AWorkerKilledDuringARun_FailsThatRunOnly_AndTheNextRunStartsANewOne()
    {
        var progress = new Recorded();
        var running = _client.RunAsync(LongRun(Path.Combine(_folder, "dài.pdf")), progress, Token);
        await WaitUntilAsync(() => progress.Reports.Count > 0);
        var first = _client.ProcessId.ShouldNotBeNull();

        Process.GetProcessById(first).Kill();

        (await Should.ThrowAsync<WorkerException>(() => running)).Failure.ShouldBe(WorkerFailure.Crashed);
        await _client.RunAsync(Run(PageNumbersOperation.OperationId, new PageNumbersParams(), Sample("basic-vi.pdf"), Path.Combine(_folder, "sau.pdf")), new Recorded(), Token);
        _client.ProcessId.ShouldNotBeNull().ShouldNotBe(first);
    }

    [Fact]
    public async Task ACancelledRun_Stops_AndTheWorkerStays()
    {
        var progress = new Recorded();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = _client.RunAsync(LongRun(Path.Combine(_folder, "hủy.pdf")), progress, cancel.Token);
        await WaitUntilAsync(() => progress.Reports.Count > 0);
        var worker = _client.ProcessId;

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        _client.ProcessId.ShouldBe(worker);
    }

    [Fact]
    public async Task ARunOverItsTimeLimit_StopsTheWorker()
    {
        // The limit counts from the run's start (the worker is already connected): 1,000 pages redacted as pictures
        // take far longer than a second.
        await using var hurried = Client(TimeSpan.FromSeconds(1));

        var error = await Should.ThrowAsync<WorkerException>(() => hurried.RunAsync(LongRun(Path.Combine(_folder, "chậm.pdf")), new Recorded(), Token));

        error.Failure.ShouldBe(WorkerFailure.TimedOut);
        hurried.ProcessId.ShouldBeNull();
    }

    [Fact]
    public async Task AMissingWorkerProgram_IsSaid()
    {
        await using var missing = new WorkerClient(new WorkerOptions(Path.Combine(_folder, "không có.dll"), 0, TimeSpan.FromMinutes(1)), NullLogger<WorkerClient>.Instance);

        missing.IsAvailable.ShouldBeFalse();
        (await Should.ThrowAsync<WorkerException>(() => missing.RunAsync(LongRun(Path.Combine(_folder, "x.pdf")), new Recorded(), Token))).Failure.ShouldBe(WorkerFailure.NotStarted);
    }

    /// <summary>Every page of the 1,000-page sample redacted as pictures: many seconds of work.</summary>
    private static WorkerRun LongRun(string output)
        => Run(RedactOperation.OperationId, new RedactParams([.. Enumerable.Range(1, 1000).Select(p => new RedactionArea(p, [new PdfRect(72, 72, 200, 100)]))]), Sample("big-1000.pdf"), output);

    private static WorkerRun Run(string operation, object parameters, string input, string output)
        => new(operation, JsonSerializer.Serialize(parameters, parameters.GetType(), ParameterSchema.Json), input, output);

    /// <remarks>
    /// Two minutes to start, not the app's twenty seconds: the worker runs below normal priority, and with the whole
    /// suite running in parallel on every processor it was starved past twenty seconds on Windows (NotStarted).
    /// </remarks>
    private WorkerClient Client(TimeSpan runTimeout)
        => new(
            new WorkerOptions(Path.Combine(AppContext.BaseDirectory, "PdfPro.Worker.dll"), 0, runTimeout) { CancelGrace = TimeSpan.FromSeconds(20), StartTimeout = TimeSpan.FromMinutes(2) },
            NullLogger<WorkerClient>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "the worker never reported");
            await Task.Delay(20, Token);
        }
    }

    private static string Sample(string name) => Path.Combine(RepositoryRoot, "samples", name);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PdfPro.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository was not found.");
    }

    /// <summary>Progress reports as they come (from the client's reading thread).</summary>
    private sealed class Recorded : IProgress<OperationProgress>
    {
        private readonly List<OperationProgress> _reports = [];

        public IReadOnlyList<OperationProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(OperationProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }
}
