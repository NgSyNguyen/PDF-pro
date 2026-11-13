using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services.Comparing;
using PdfPro.App.Services.Optimizing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Comparing;

namespace PdfPro.App.Tests.Services;

/// <summary>
/// Comparing two documents runs in the worker process when it is on (PdfPig on a "content bomb" page exhausts the
/// worker, not the window): the two files and their passwords go there, what it found comes back.
/// </summary>
public sealed class DocumentComparisonTests
{
    private readonly IWorkerClient _worker = Substitute.For<IWorkerClient>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithTheWorkerOn_TheComparisonRunsThere_AndItsResultComesBack()
    {
        var inApp = await new ComparisonMeasurement(TestOperations.PdfiumGate).CompareAsync(new CompareRequest(Samples.ContractV1, Samples.ContractV2, Visual: false), new Progress<OperationProgress>(), Token);
        _worker.IsAvailable.Returns(true);
        WorkerRun? sent = null;
        _worker.RunAsync(Arg.Do<WorkerRun>(r => sent = r), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(inApp, ParameterSchema.ReportJson));
        var comparison = new DocumentComparison(TestOperations.PdfiumGate, Runner(isolate: true));

        var result = await comparison.CompareAsync(new ComparedFile(Samples.ContractV1, null), new ComparedFile(Samples.ContractV2, "mật khẩu"), new CompareSettings(Visual: false), new Progress<OperationProgress>(), Token);

        result.Text.Changes.Count.ShouldBe(4);
        result.Visual.ShouldBeNull();
        sent.ShouldNotBeNull();
        (sent.Measure, sent.Operation).ShouldBe((true, ComparisonMeasurement.OperationId));
        var request = JsonSerializer.Deserialize<CompareRequest>(sent.Parameters, ParameterSchema.Json).ShouldNotBeNull();
        (request.OldPath, request.NewPath, request.NewPassword, request.Visual).ShouldBe((Samples.ContractV1, Samples.ContractV2, "mật khẩu", false));
        request.ToString().ShouldNotContain("mật khẩu", customMessage: "a request never shows its passwords");
    }

    [Fact]
    public async Task WithTheWorkerOff_TheComparisonRunsInTheApp()
    {
        var comparison = new DocumentComparison(TestOperations.PdfiumGate, Runner(isolate: false));

        var result = await comparison.CompareAsync(new ComparedFile(Samples.ContractV1, null), new ComparedFile(Samples.ContractV2, null), new CompareSettings(), new Progress<OperationProgress>(), Token);

        result.Text.Changes.Count.ShouldBe(4);
        result.Visual.ShouldNotBeNull().Count.ShouldBe(result.Text.Pages.Count);
        await _worker.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, Token);
    }

    private WorkerExportRunner Runner(bool isolate)
        => new(_worker, new FakeSettingsService(AppSettings.Default with { IsolateOperations = isolate }), NullLogger<WorkerExportRunner>.Instance);
}
