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
    public async Task AScanToolsMeasurement_RunsInTheWorker_AndWhatItFoundComesBack()
    {
        var parameters = JsonSerializer.Serialize(new PdfPro.Operations.Scanning.ScanMeasureParams(), ParameterSchema.Json);

        var blank = await _client.RunAsync(new WorkerRun(PdfPro.Operations.Scanning.RemoveBlankPagesOperation.OperationId, parameters, Sample("scan-vi.pdf"), string.Empty) { Measure = true }, new Recorded(), Token);
        var shares = JsonSerializer.Deserialize<IReadOnlyList<double>>(blank.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        shares.Count.ShouldBe(18);
        PdfPro.Operations.Splitting.BlankPageDetector.BlankPages(shares, PdfPro.Operations.Splitting.BlankPageDetector.DefaultThresholdPercent).Order().ToArray().ShouldBe(new[] { 3, 6, 9, 13 }, "the separator sheets of the scan");

        var tilt = await _client.RunAsync(new WorkerRun(PdfPro.Operations.Scanning.DeskewOperation.OperationId, parameters, Sample("scan-vi.pdf"), string.Empty) { Measure = true }, new Recorded(), Token);
        var angles = JsonSerializer.Deserialize<IReadOnlyList<PdfPro.Operations.Scanning.PageAngle>>(tilt.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        angles.Select(a => a.Page).ShouldContain(1);
        angles.Select(a => a.Page).ShouldContain(7);

        var unreadable = Path.Combine(_folder, "hỏng.pdf");
        await File.WriteAllTextAsync(unreadable, "không phải PDF", Token);
        (await Should.ThrowAsync<WorkerOperationException>(() => _client.RunAsync(new WorkerRun(PdfPro.Operations.Scanning.CleanBordersOperation.OperationId, parameters, unreadable, string.Empty) { Measure = true }, new Recorded(), Token)))
            .ErrorType.ShouldBe("PdfPro.Pdfium.PdfCorruptedFileException", "PDFium cannot read it; the app explains it as an unreadable file");
    }

    [Fact]
    public async Task TablesAndComparisons_RunInTheWorker_AndWhatTheyFoundComesBack()
    {
        var search = JsonSerializer.Serialize(new PdfPro.Operations.Extracting.TableSearchParams(), ParameterSchema.Json);
        var tablesJson = await _client.RunAsync(new WorkerRun(PdfPro.Operations.Extracting.ExtractTablesOperation.OperationId, search, Sample("tables.pdf"), string.Empty) { Measure = true }, new Recorded(), Token);
        var tables = JsonSerializer.Deserialize<IReadOnlyList<PdfPro.Operations.Extracting.ExtractedTable>>(tablesJson.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        tables.Select(t => t.RowCount).ShouldBe([7, 9], "the ruled table and the one aligned by its columns");
        tables[0].Rows[0].Count.ShouldBe(4);

        var request = new PdfPro.Operations.Comparing.CompareRequest(Sample("contract-v1.pdf"), Sample("contract-v2.pdf"));
        var comparedJson = await _client.RunAsync(new WorkerRun(PdfPro.Operations.Comparing.ComparisonMeasurement.OperationId, JsonSerializer.Serialize(request, ParameterSchema.Json), request.OldPath, string.Empty) { Measure = true }, new Recorded(), Token);
        var compared = JsonSerializer.Deserialize<PdfPro.Operations.Comparing.ComparedDocuments>(comparedJson.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        compared.Text.Changes.Count.ShouldBe(4, "the four changes of the contract");
        compared.Text.Changes.ShouldContain(c => c.NewText.Contains("120.000.000"));
        compared.Visual.ShouldNotBeNull().Count.ShouldBe(compared.Text.Pages.Count);
    }

    [Fact]
    public async Task AWorkflow_RunsOnAFileInTheWorker_AndItsFailureComesBackWithItsStep()
    {
        var workflow = new PdfPro.Operations.Automation.Workflow("x", [
            PdfPro.Operations.Automation.WorkflowSteps.Create(PdfPro.Operations.Organizing.RotatePagesOperation.OperationId, new PdfPro.Operations.Organizing.RotatePagesParams("1", 90)),
            PdfPro.Operations.Automation.WorkflowSteps.Create(PdfPro.Operations.Stamping.PageNumbersOperation.OperationId, new PdfPro.Operations.Stamping.PageNumbersParams("Trang {page}")),
        ]);
        var output = Path.Combine(_folder, "kết quả");
        var call = new PdfPro.Operations.Automation.WorkerWorkflowCall(PdfPro.Operations.Automation.WorkflowFile.Write(workflow), output, Path.Combine(_folder, "làm việc"));
        var progress = new Recorded();

        var json = await _client.RunAsync(new WorkerRun("workflow.run", JsonSerializer.Serialize(call, ParameterSchema.Json), Sample("basic-vi.pdf"), string.Empty) { Workflow = true }, progress, Token);

        var result = JsonSerializer.Deserialize<PdfPro.Operations.Automation.WorkflowRunResult>(json.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        result.Outputs.ShouldHaveSingleItem().ShouldBe(Path.Combine(output, "basic-vi.pdf"));
        File.Exists(result.Outputs[0]).ShouldBeTrue();
        result.Steps.Count.ShouldBe(2);
        progress.Reports.Select(PdfPro.Operations.Automation.WorkflowProgressCodec.Decode).ShouldAllBe(p => p != null, "the steps' progress travels as the run's");

        var failing = new PdfPro.Operations.Automation.Workflow("x", [
            PdfPro.Operations.Automation.WorkflowSteps.Create(PdfPro.Operations.Organizing.DeletePagesOperation.OperationId, new PdfPro.Operations.Organizing.DeletePagesParams("99")),
        ]);
        var bad = new PdfPro.Operations.Automation.WorkerWorkflowCall(PdfPro.Operations.Automation.WorkflowFile.Write(failing), output);
        var error = await Should.ThrowAsync<WorkerOperationException>(() => _client.RunAsync(new WorkerRun("workflow.run", JsonSerializer.Serialize(bad, ParameterSchema.Json), Sample("basic-vi.pdf"), string.Empty) { Workflow = true }, new Recorded(), Token));
        error.ErrorType.ShouldBe(typeof(PdfPro.Operations.Automation.WorkflowFailedException).FullName);
        var detail = JsonSerializer.Deserialize<PdfPro.Operations.Automation.WorkflowFailureDetail>(error.Detail.ShouldNotBeNull(), ParameterSchema.ReportJson).ShouldNotBeNull();
        (detail.Failure, detail.Step).ShouldBe((PdfPro.Operations.Automation.WorkflowFailure.Parameters, 0));
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
