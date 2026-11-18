using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Services.Automation;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Ocr;
using PdfPro.Core.Operations;
using PdfPro.Core.Security;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// Batches and the hot folder run each file's workflow in the worker process when it is on (SPEC Phase 13): what it
/// wrote and how it failed come back as if it had run in the app; a workflow that asks for a secret, or a worker that is
/// off, missing or not starting, runs in the app; the files a batch runs at once each get a worker.
/// </summary>
public sealed class WorkerWorkflowRunnerTests : IDisposable
{
    private static readonly Workflow Rotate = new("x", [WorkflowSteps.Create(RotatePagesOperation.OperationId, new RotatePagesParams("1", 90))]);

    private readonly List<IWorkerClient> _workers = [];

    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task WithTheWorkerOn_TheFileRunsThere_WithItsPassword_AndItsResultAndProgressComeBack()
    {
        var expected = new WorkflowRunResult(@"C:\vào\a.pdf", [@"C:\ra\a.pdf"], [new WorkflowStepReport(0, RotatePagesOperation.OperationId, WorkflowStepOutcome.Done, TimeSpan.FromSeconds(1), [])], Unprotected: true, TimeSpan.FromSeconds(2));
        WorkerRun? sent = null;
        var runner = Runner(isolate: true, worker =>
            worker.RunAsync(Arg.Do<WorkerRun>(r => sent = r), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                call.Arg<IProgress<OperationProgress>>().Report(WorkflowProgressCodec.Encode(new WorkflowProgress(0, 1, RotatePagesOperation.OperationId, new OperationProgress(1, 2), 0.5)));
                return JsonSerializer.Serialize(expected, ParameterSchema.ReportJson);
            }));
        var steps = new List<WorkflowProgress>();

        var result = await runner.RunAsync(Rotate, new WorkflowRunRequest(@"C:\vào\a.pdf") { OutputFolder = @"C:\ra", Password = "chủ" }, new SyncProgress<WorkflowProgress>(steps.Add), Token);

        (result.Outputs.Single(), result.Unprotected, result.Steps.Single().Outcome).ShouldBe((@"C:\ra\a.pdf", true, WorkflowStepOutcome.Done));
        sent.ShouldNotBeNull();
        (sent.Workflow, sent.Input, sent.Password).ShouldBe((true, @"C:\vào\a.pdf", "chủ"));
        var call = JsonSerializer.Deserialize<WorkerWorkflowCall>(sent.Parameters, ParameterSchema.Json).ShouldNotBeNull();
        call.OutputFolder.ShouldBe(@"C:\ra");
        WorkflowFile.Read(call.Workflow).Steps.Single().Operation.ShouldBe(RotatePagesOperation.OperationId);
        await AnyAsync(() => steps.Count > 0);
        (steps[0].Position, steps[0].Count, steps[0].Operation, steps[0].Fraction).ShouldBe((0, 1, RotatePagesOperation.OperationId, 0.5));
    }

    [Fact]
    public async Task AWorkflowThatAsksForASecret_OrAWorkerThatIsOff_RunsInTheApp()
    {
        var protect = new Workflow("x", [WorkflowSteps.Create(ProtectDocumentOperation.OperationId, new ProtectParams(new DocumentSecurity("mở", "chủ")))]);
        var on = Runner(isolate: true, _ => { });
        on.RunsInWorker(Rotate).ShouldBeTrue();
        on.RunsInWorker(protect).ShouldBeFalse("its password or certificate is asked for when it runs (a certificate may live on a token)");
        Runner(isolate: false, _ => { }).RunsInWorker(Rotate).ShouldBeFalse();

        var input = _temp.Combine("hồ sơ.pdf");
        File.Copy(Samples.Basic, input);
        var folder = _temp.Combine("ra");
        var result = await Runner(isolate: true, worker => worker.RunAsync(Arg.Any<WorkerRun>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new WorkerException(WorkerFailure.NotStarted)))
            .RunAsync(new Workflow("x", [WorkflowSteps.Create(PdfPro.Operations.Stamping.PageNumbersOperation.OperationId, new PdfPro.Operations.Stamping.PageNumbersParams("Trang {page}"))]), new WorkflowRunRequest(input) { OutputFolder = folder }, null, Token);
        File.Exists(result.Outputs.ShouldHaveSingleItem()).ShouldBeTrue("a worker that does not start lets the app run the file");
    }

    [Fact]
    public async Task TheFilesOfABatchRunAtOnce_EachGetAWorker()
    {
        var gate = new TaskCompletionSource();
        var runner = Runner(isolate: true, worker => worker.RunAsync(Arg.Any<WorkerRun>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return (string?)JsonSerializer.Serialize(new WorkflowRunResult("a", [], [], false, TimeSpan.Zero), ParameterSchema.ReportJson);
        }));

        var first = runner.RunAsync(Rotate, new WorkflowRunRequest(@"C:\a.pdf"), null, Token);
        var second = runner.RunAsync(Rotate, new WorkflowRunRequest(@"C:\b.pdf"), null, Token);
        gate.SetResult();
        await Task.WhenAll(first, second);
        await runner.RunAsync(Rotate, new WorkflowRunRequest(@"C:\c.pdf"), null, Token);

        _workers.Count.ShouldBe(2, "two files at once, two workers; the third file reuses one");
        await runner.DisposeAsync();
        foreach (var worker in _workers)
        {
            await worker.Received(1).DisposeAsync();
        }
    }

    [Fact]
    public void TheWorkersFailures_AreToldAsTheAppTellsThem()
    {
        var ocr = WorkerWorkflowRunner.Rebuild(Failure(new WorkflowFailureDetail(WorkflowFailure.Step, 2, "ocr.run", InnerType: typeof(OcrException).FullName, InnerMessage: "OCR failed", InnerDetail: "NotInstalled")));
        (ocr.Failure, ocr.Step, ocr.Operation).ShouldBe((WorkflowFailure.Step, 2, "ocr.run"));
        BatchRunner.Innermost(ocr).ShouldBeOfType<OcrException>().Failure.ShouldBe(OcrFailure.NotInstalled);
        MainWindowViewModel.Explain(ocr).ShouldContain(PdfPro.App.Resources.Strings.Workflow_Failure_NoOcr);

        var unreadable = WorkerWorkflowRunner.Rebuild(Failure(new WorkflowFailureDetail(WorkflowFailure.Step, 0, RotatePagesOperation.OperationId, InnerType: "PdfPro.Pdfium.PdfCorruptedFileException", InnerMessage: "hỏng")));
        BatchRunner.IsUnreadableFile(unreadable).ShouldBeTrue();
        BatchRunner.ErrorTypeName(unreadable).ShouldBe("PdfCorruptedFileException", "the batch log names the error as it was in the worker");

        var invalid = WorkerWorkflowRunner.Rebuild(Failure(new WorkflowFailureDetail(WorkflowFailure.Invalid, Issues: [new WorkflowIssue(WorkflowIssueSeverity.Error, WorkflowIssueKind.NoSteps)])));
        invalid.Issues.ShouldHaveSingleItem().Kind.ShouldBe(WorkflowIssueKind.NoSteps);

        WorkerWorkflowRunner.Rebuild(new WorkerOperationException("System.OutOfMemoryException", "hết bộ nhớ", null)).Failure.ShouldBe(WorkflowFailure.Step);
    }

    [Fact]
    public async Task AWorkerThatDies_FailsOnlyThatFile_AndSaysSo()
    {
        var runner = Runner(isolate: true, worker => worker.RunAsync(Arg.Any<WorkerRun>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new WorkerException(WorkerFailure.Crashed)));

        var failed = await Should.ThrowAsync<WorkflowFailedException>(() => runner.RunAsync(Rotate, new WorkflowRunRequest(@"C:\a.pdf"), null, Token));

        failed.Failure.ShouldBe(WorkflowFailure.Step);
        MainWindowViewModel.Explain(failed).ShouldBe(PdfPro.App.Resources.Strings.Content_Error_WorkerStopped);
    }

    private static WorkerOperationException Failure(WorkflowFailureDetail detail)
        => new(typeof(WorkflowFailedException).FullName!, "failed", JsonSerializer.Serialize(detail, ParameterSchema.ReportJson));

    private static async Task AnyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, Token);
        }
    }

    private WorkerWorkflowRunner Runner(bool isolate, Action<IWorkerClient> setUp)
    {
        var catalog = TestOperations.Catalog();
        return new WorkerWorkflowRunner(
            new WorkflowRunner(catalog, TestOperations.PdfiumGate),
            () =>
            {
                var worker = Substitute.For<IWorkerClient>();
                worker.IsAvailable.Returns(true);
                setUp(worker);
                lock (_workers)
                {
                    _workers.Add(worker);
                }

                return worker;
            },
            new FakeSettingsService(AppSettings.Default with { IsolateOperations = isolate }),
            NullLogger<WorkerWorkflowRunner>.Instance);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
