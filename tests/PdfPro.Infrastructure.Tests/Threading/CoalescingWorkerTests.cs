using Microsoft.Extensions.Logging;
using PdfPro.Infrastructure.Tests.TestSupport;
using PdfPro.Infrastructure.Threading;

namespace PdfPro.Infrastructure.Tests.Threading;

public sealed class CoalescingWorkerTests
{
    private readonly CapturingLogger<CoalescingWorkerTests> _logger = new();

    [Fact]
    public async Task Request_RunsTheWork()
    {
        var runs = 0;
        await using var worker = new CoalescingWorker(_ => { Interlocked.Increment(ref runs); return Task.CompletedTask; }, _logger);

        worker.Request();
        await worker.FlushAsync();

        runs.ShouldBe(1);
    }

    [Fact]
    public async Task RequestsDuringARun_CollapseIntoOneMoreRun()
    {
        var runs = 0;
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new CoalescingWorker(
            async _ =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    firstRunStarted.SetResult();
                    await releaseFirstRun.Task;
                }
            },
            _logger);

        worker.Request();
        await firstRunStarted.Task;
        worker.Request();
        worker.Request();
        worker.Request();
        releaseFirstRun.SetResult();
        await worker.FlushAsync();

        runs.ShouldBe(2);
    }

    [Fact]
    public async Task Failures_AreLoggedAndDoNotStopLaterRuns()
    {
        var runs = 0;
        await using var worker = new CoalescingWorker(
            _ => Interlocked.Increment(ref runs) == 1 ? throw new IOException("disk gone") : Task.CompletedTask,
            _logger);

        worker.Request();
        await worker.FlushAsync();
        worker.Request();
        await worker.FlushAsync();

        runs.ShouldBe(2);
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is IOException);
    }

    [Fact]
    public async Task Dispose_WaitsForTheRunAndIgnoresLaterRequests()
    {
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new CoalescingWorker(
            async _ =>
            {
                Interlocked.Increment(ref runs);
                await release.Task;
            },
            _logger);
        worker.Request();

        var disposing = worker.DisposeAsync().AsTask();
        disposing.IsCompleted.ShouldBeFalse();
        release.SetResult();
        await disposing;
        worker.Request();
        await worker.FlushAsync();

        runs.ShouldBe(1);
    }
}
