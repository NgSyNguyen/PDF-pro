using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Services.Optimizing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Converting;
using PdfPro.Operations.Optimizing;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// The file tools' operations run in the worker process when it is on (SPEC Phase 13): their results come back typed,
/// the errors the tools explain come back as their own types, and a worker that does not start lets them run in the app.
/// </summary>
public sealed class ExportRunnerTests
{
    private static readonly CompressReport Report = new(1000, 400, 2, 3, 1, 5, true, false);

    private readonly IWorkerClient _worker = Substitute.For<IWorkerClient>();
    private readonly IExportOperation _operation = Substitute.For<IExportOperation>();

    public ExportRunnerTests()
    {
        _worker.IsAvailable.Returns(true);
        _operation.Id.Returns(CompressOperation.OperationId);
        _operation.ResultType.Returns(typeof(CompressReport));
        _operation.ExportAsync(Arg.Any<ExportCall>(), Arg.Any<object>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<object>(Report));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithTheWorkerOn_TheExportRunsThere_AndItsResultComesBackTyped()
    {
        WorkerRun? sent = null;
        _worker.RunAsync(Arg.Do<WorkerRun>(r => sent = r), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(Report, ParameterSchema.ReportJson));

        var report = await Runner(isolate: true).RunAsync<CompressReport>(_operation, new ExportCall([@"C:\a.pdf"], @"C:\a (nén).pdf", Name: "a"), new CompressParams(), new Progress<OperationProgress>(), Token);

        report.ShouldBe(Report);
        sent.ShouldNotBeNull();
        (sent.Export, sent.Operation, sent.Input, sent.Output, sent.Name).ShouldBe((true, CompressOperation.OperationId, @"C:\a.pdf", @"C:\a (nén).pdf", "a"));
        await _operation.DidNotReceiveWithAnyArgs().ExportAsync(default!, default!, default!, Token);
    }

    [Fact]
    public async Task WithTheWorkerOff_OrNotStarting_TheExportRunsInTheApp()
    {
        (await Runner(isolate: false).RunAsync<CompressReport>(_operation, new ExportCall([@"C:\a.pdf"], @"C:\b.pdf"), new CompressParams(), new Progress<OperationProgress>(), Token)).ShouldBe(Report);
        await _worker.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, Token);

        _worker.RunAsync(Arg.Any<WorkerRun>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new WorkerException(WorkerFailure.NotStarted));
        (await Runner(isolate: true).RunAsync<CompressReport>(_operation, new ExportCall([@"C:\a.pdf"], @"C:\b.pdf"), new CompressParams(), new Progress<OperationProgress>(), Token)).ShouldBe(Report);
    }

    [Fact]
    public async Task AWorkerThatCrashes_FailsTheExport()
    {
        _worker.RunAsync(Arg.Any<WorkerRun>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new WorkerException(WorkerFailure.Crashed));

        (await Should.ThrowAsync<WorkerException>(() => Runner(isolate: true).RunAsync<CompressReport>(_operation, new ExportCall([@"C:\a.pdf"], @"C:\b.pdf"), new CompressParams(), new Progress<OperationProgress>(), Token)))
            .Failure.ShouldBe(WorkerFailure.Crashed);
    }

    [Fact]
    public void TheErrorsTheToolsExplain_AreRebuilt_AndOthersAreAFileThatCannotBeRead()
    {
        var picture = WorkerExportRunner.Rebuild(new WorkerOperationException(typeof(ImageFileException).FullName!, "hỏng", "2\nTooLarge")).ShouldBeOfType<ImageFileException>();
        (picture.FileIndex, picture.Problem).ShouldBe((2, ImageFileProblem.TooLarge));

        var repair = WorkerExportRunner.Rebuild(new WorkerOperationException(typeof(RepairFailedException).FullName!, "không sửa được", """[["Pdfium","Unreadable"],["Qpdf","Missing"]]""")).ShouldBeOfType<RepairFailedException>();
        repair.Attempts.Select(a => a.Method).ShouldBe([RepairMethod.Pdfium, RepairMethod.Qpdf]);

        WorkerExportRunner.Rebuild(new WorkerOperationException("PdfPro.Pdfium.PdfDocumentException", "hỏng", null)).ShouldBeOfType<System.IO.InvalidDataException>();
        WorkerExportRunner.Rebuild(new WorkerOperationException(typeof(ImageFileException).FullName!, "hỏng", "không đọc được")).ShouldBeOfType<System.IO.InvalidDataException>();
    }

    private WorkerExportRunner Runner(bool isolate)
        => new(_worker, new FakeSettingsService(AppSettings.Default with { IsolateOperations = isolate }), NullLogger<WorkerExportRunner>.Instance);
}
