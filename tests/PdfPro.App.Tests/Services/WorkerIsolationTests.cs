using System.IO;
using System.Text.Json;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Settings;
using PdfPro.Infrastructure.Worker;
using PdfPro.Operations.Automation;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Security;

namespace PdfPro.App.Tests.Services;

/// <summary>Heavy operations go to the worker when the setting says so; their result and report come back.</summary>
public sealed class WorkerIsolationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void OnlyHeavyOperations_GoToTheWorker_AndOnlyWhenTheSettingIsOn()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var isolation = new WorkerIsolation(new FakeWorker(null), settings);
        var sanitize = new SanitizeOperation(TestOperations.PdfiumGate);

        isolation.Isolates(sanitize).ShouldBeTrue();
        isolation.Isolates(new ChangePageSizeOperation()).ShouldBeFalse();

        var off = new WorkerIsolation(new FakeWorker(null), new FakeSettingsService(AppSettings.Default with { IsolateOperations = false }));
        off.Isolates(sanitize).ShouldBeFalse();

        var missing = new WorkerIsolation(new FakeWorker(null) { IsAvailable = false }, settings);
        missing.Isolates(sanitize).ShouldBeFalse("without the worker's program everything runs in the app");
    }

    [Fact]
    public async Task TheWorkersResult_IsCopiedOut_AndItsReportGivenToTheOperation()
    {
        var sanitize = new SanitizeOperation(TestOperations.PdfiumGate);
        var ran = new SanitizeParams();
        await using (var output = new MemoryStream())
        {
            await sanitize.ApplyToFileAsync(Samples.Basic, output, ran, new Progress<OperationProgress>(), Token);
        }

        var report = JsonSerializer.Serialize(SanitizeOperation.ReportOf(ran).ShouldNotBeNull(), ParameterSchema.Json);
        var worker = new FakeWorker(report);
        var isolation = new WorkerIsolation(worker, new FakeSettingsService(AppSettings.Default));
        var input = Path.Combine(_temp.Path, "trang.pdf");
        File.Copy(Samples.Basic, input);
        var parameters = new SanitizeParams(ExternalLinks: true);

        await using var written = new MemoryStream();
        await isolation.ApplyAsync(sanitize, input, written, parameters, new Progress<OperationProgress>(), Token);

        written.ToArray().ShouldBe(FakeWorker.Output);
        worker.Run.ShouldNotBeNull().Operation.ShouldBe(SanitizeOperation.OperationId);
        JsonSerializer.Deserialize<SanitizeParams>(worker.Run.Parameters, ParameterSchema.Json).ShouldBe(parameters);
        JsonSerializer.Serialize(SanitizeOperation.ReportOf(parameters).ShouldNotBeNull(), ParameterSchema.Json).ShouldBe(report);
        Directory.GetFiles(_temp.Path).ShouldBe([input], "the worker's file is removed");
    }

    /// <summary>Writes a fixed output where the run asks, and returns the report it was given.</summary>
    private sealed class FakeWorker(string? report) : IWorkerClient
    {
        public static readonly byte[] Output = "%PDF worker"u8.ToArray();

        public WorkerRun? Run { get; private set; }

        public bool IsAvailable { get; init; } = true;

        public async Task<string?> RunAsync(WorkerRun run, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
        {
            Run = run;
            await File.WriteAllBytesAsync(run.Output, Output, cancellationToken);
            return report;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
