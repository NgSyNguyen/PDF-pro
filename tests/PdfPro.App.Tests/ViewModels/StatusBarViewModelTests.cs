using PdfPro.App.Resources;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Tests.ViewModels;

public sealed class StatusBarViewModelTests
{
    private readonly StatusBarViewModel _status = new();

    [Fact]
    public void Idle_ShowsReadyAndCannotCancel()
    {
        _status.TaskText.ShouldBe(Strings.Status_Ready);
        _status.IsTaskRunning.ShouldBeFalse();
        _status.CancelTaskCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Task_ShowsItsTextAndProgressUntilCompleted()
    {
        var task = _status.StartTask("Đang in…");

        _status.IsTaskRunning.ShouldBeTrue();
        _status.TaskText.ShouldBe("Đang in…");
        _status.CancelTaskCommand.CanExecute(null).ShouldBeTrue();

        task.Report(0.5, "Trang 2/4");
        _status.TaskProgress.ShouldBe(0.5);
        _status.TaskText.ShouldBe("Trang 2/4");

        task.Report(7, "x");
        _status.TaskProgress.ShouldBe(1, "progress is clamped");

        task.Complete("Xong");
        _status.IsTaskRunning.ShouldBeFalse();
        _status.TaskProgress.ShouldBe(0);
        _status.TaskText.ShouldBe("Xong");
        _status.CancelTaskCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void CancelButton_CancelsTheRunningTask()
    {
        var task = _status.StartTask("Đang in…");

        _status.CancelTaskCommand.Execute(null);

        task.IsCancellationRequested.ShouldBeTrue();
        task.Token.IsCancellationRequested.ShouldBeTrue();
        _status.TaskText.ShouldBe(Strings.Status_Cancelling);
        task.Report(0.9, "late progress");
        _status.TaskText.ShouldBe(Strings.Status_Cancelling, "progress after cancelling is not shown");
        _status.IsTaskRunning.ShouldBeTrue("the task runs until it acknowledges the cancellation");
    }

    [Fact]
    public void OnlyOneTask_RunsAtATime()
    {
        var first = _status.StartTask("A");

        Should.Throw<InvalidOperationException>(() => _status.StartTask("B"));

        first.Complete("done");
        _status.StartTask("B").ShouldNotBeSameAs(first);
    }

    [Fact]
    public void CompletingTwice_OrAfterAnotherTaskStarted_ChangesNothing()
    {
        var first = _status.StartTask("A");
        first.Complete("A done");
        var second = _status.StartTask("B");

        first.Complete("stale");
        first.Report(0.3, "stale");
        first.Cancel();

        _status.TaskText.ShouldBe("B");
        _status.IsTaskRunning.ShouldBeTrue();
        second.IsCancellationRequested.ShouldBeFalse();
    }
}
