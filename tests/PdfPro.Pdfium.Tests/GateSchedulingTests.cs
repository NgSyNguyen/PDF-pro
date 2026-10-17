using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.Pdfium.Tests.TestSupport;

namespace PdfPro.Pdfium.Tests;

/// <summary>Scheduling behaviour, tested on a gate that does not load PDFium.</summary>
public sealed class GateSchedulingTests : IDisposable
{
    private readonly PdfiumGate _gate = new(NullLogger<PdfiumGate>.Instance, initializeLibrary: false);

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    public void Dispose() => _gate.Dispose();

    [Fact]
    public async Task Work_RunsOnTheDedicatedThread()
    {
        var callerThread = Environment.CurrentManagedThreadId;

        var (workThread, onGate) = await _gate.RunAsync(() => (Environment.CurrentManagedThreadId, _gate.IsOnGateThread), cancellationToken: TestToken);

        workThread.ShouldNotBe(callerThread);
        onGate.ShouldBeTrue();
        _gate.IsOnGateThread.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAccess_ThrowsOutsideTheGateThread()
    {
        Should.Throw<InvalidOperationException>(_gate.VerifyAccess);
        await _gate.RunAsync(_gate.VerifyAccess, cancellationToken: TestToken);
    }

    [Fact]
    public async Task HigherPriorityWork_RunsFirst_EqualPriorityRunsInOrder()
    {
        using var blocker = new ManualResetEventSlim();
        var order = new List<string>();
        var blocked = _gate.RunAsync(() => blocker.Wait(TestToken), cancellationToken: TestToken);

        var tasks = new[]
        {
            _gate.RunAsync(() => order.Add("low"), PdfiumPriority.Low, TestToken),
            _gate.RunAsync(() => order.Add("normal-1"), PdfiumPriority.Normal, TestToken),
            _gate.RunAsync(() => order.Add("high"), PdfiumPriority.High, TestToken),
            _gate.RunAsync(() => order.Add("normal-2"), PdfiumPriority.Normal, TestToken),
        };
        blocker.Set();
        await blocked;
        await Task.WhenAll(tasks);

        order.ShouldBe(["high", "normal-1", "normal-2", "low"]);
    }

    [Fact]
    public async Task CancelledWhileQueued_NeverRuns()
    {
        using var blocker = new ManualResetEventSlim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var ran = false;
        var blocked = _gate.RunAsync(() => blocker.Wait(TestToken), cancellationToken: TestToken);
        var queued = _gate.RunAsync(() => ran = true, PdfiumPriority.Normal, cts.Token);

        await cts.CancelAsync();
        blocker.Set();
        await blocked;

        await Should.ThrowAsync<OperationCanceledException>(() => queued);
        await _gate.RunAsync(() => 0, cancellationToken: TestToken);
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task CancellationAfterStart_StillDeliversTheResult()
    {
        using var started = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var task = _gate.RunAsync(
            () =>
            {
                started.Set();
                proceed.Wait(TestToken);
                return 42;
            },
            PdfiumPriority.Normal,
            cts.Token);

        started.Wait(TestToken);
        await cts.CancelAsync();
        proceed.Set();

        (await task).ShouldBe(42, "a started item must hand over its result so the caller can dispose it");
    }

    [Fact]
    public async Task Exceptions_PropagateToTheCaller()
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => _gate.RunAsync<int>(() => throw new InvalidOperationException("boom"), cancellationToken: TestToken));

        error.Message.ShouldBe("boom");
        (await _gate.RunAsync(() => 1, cancellationToken: TestToken)).ShouldBe(1, "the gate keeps working after a failure");
    }

    [Fact]
    public async Task RunAsync_FromTheGateThread_RunsInlineInsteadOfDeadlocking()
    {
        var inner = await _gate.RunAsync(() => _gate.RunAsync(() => 7, cancellationToken: TestToken).Result, cancellationToken: TestToken);

        inner.ShouldBe(7);
    }

    [Fact]
    public async Task AlreadyCancelledToken_ReturnsCancelledTask()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _gate.RunAsync(() => 1, PdfiumPriority.Normal, cts.Token));
    }

    [Fact]
    public void ReleasePriority_IsReservedForHandles()
        => Should.Throw<ArgumentOutOfRangeException>(() => _gate.RunAsync(() => 1, PdfiumPriority.Release, TestToken));

    [Fact]
    public async Task Post_RunsBeforeQueuedWork()
    {
        using var blocker = new ManualResetEventSlim();
        var order = new List<string>();
        var blocked = _gate.RunAsync(() => blocker.Wait(TestToken), cancellationToken: TestToken);
        var work = _gate.RunAsync(() => order.Add("work"), PdfiumPriority.High, TestToken);
        _gate.Post(() => order.Add("release"));

        blocker.Set();
        await blocked;
        await work;

        order.ShouldBe(["release", "work"]);
    }

    [Fact]
    public async Task Dispose_CancelsPendingWorkButRunsReleases()
    {
        var gate = new PdfiumGate(NullLogger<PdfiumGate>.Instance, initializeLibrary: false);
        using var started = new ManualResetEventSlim();
        using var blocker = new ManualResetEventSlim();
        var released = false;
        var blocked = gate.RunAsync(
            () =>
            {
                started.Set();
                blocker.Wait(TestToken);
            },
            cancellationToken: TestToken);
        started.Wait(TestToken); // the blocking item must be running, otherwise Dispose would cancel it too
        var pending = gate.RunAsync(() => 1, cancellationToken: TestToken);
        gate.Post(() => released = true);

        var disposing = Task.Run(gate.Dispose, TestToken);
        SpinWait.SpinUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(5)).ShouldBeTrue("Dispose cancels queued work first");
        blocker.Set();
        await disposing;
        await blocked;

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        released.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => gate.RunAsync(() => 1, cancellationToken: TestToken));
    }

    [Fact]
    public void SecondLibraryOwner_IsRejected()
        => Should.Throw<InvalidOperationException>(() => new PdfiumGate(NullLogger<PdfiumGate>.Instance))
            .Message.ShouldContain("Only one PdfiumGate");
}
