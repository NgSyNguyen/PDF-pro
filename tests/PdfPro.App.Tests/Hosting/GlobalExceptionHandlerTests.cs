using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Hosting;
using PdfPro.App.Services;
using PdfPro.App.Tests.TestSupport;

namespace PdfPro.App.Tests.Hosting;

public sealed class GlobalExceptionHandlerTests
{
    private readonly CapturingLogger<GlobalExceptionHandler> _logger = new();
    private readonly IErrorDialogService _dialogs = Substitute.For<IErrorDialogService>();
    private readonly GlobalExceptionHandler _handler;

    public GlobalExceptionHandlerTests() => _handler = new GlobalExceptionHandler(_logger, _dialogs);

    [Fact]
    public void Recoverable_LogsWithIncidentIdAndShowsDialog()
    {
        _dialogs.ShowUnexpectedErrorAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        var error = new InvalidOperationException("boom");

        var incidentId = _handler.HandleRecoverable(error, "UI thread");

        incidentId.ShouldMatch("^[0-9A-F]{8}$");
        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(error);
        entry.Message.ShouldContain(incidentId);
        entry.Message.ShouldContain("UI thread");
        _dialogs.Received(1).ShowUnexpectedErrorAsync(incidentId);
    }

    [Fact]
    public void Recoverable_WhileDialogIsOpen_LogsButDoesNotStackDialogs()
    {
        var dialogClosed = new TaskCompletionSource();
        _dialogs.ShowUnexpectedErrorAsync(Arg.Any<string>()).Returns(dialogClosed.Task);

        _handler.HandleRecoverable(new InvalidOperationException("first"), "UI thread");
        _handler.HandleRecoverable(new InvalidOperationException("second"), "UI thread");
        _handler.HandleRecoverable(new InvalidOperationException("third"), "background task");

        _logger.Entries.Count.ShouldBe(3);
        _dialogs.ReceivedWithAnyArgs(1).ShowUnexpectedErrorAsync(default!);
    }

    [Fact]
    public void Recoverable_AfterDialogClosed_ShowsDialogAgain()
    {
        _dialogs.ShowUnexpectedErrorAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        _handler.HandleRecoverable(new InvalidOperationException("first"), "UI thread");
        _handler.HandleRecoverable(new InvalidOperationException("second"), "UI thread");

        _dialogs.ReceivedWithAnyArgs(2).ShowUnexpectedErrorAsync(default!);
    }

    [Fact]
    public void Recoverable_WhenDialogFails_LogsAndKeepsWorking()
    {
        _dialogs.ShowUnexpectedErrorAsync(Arg.Any<string>())
            .Returns(Task.FromException(new InvalidOperationException("no dispatcher")), Task.CompletedTask);

        Should.NotThrow(() => _handler.HandleRecoverable(new InvalidOperationException("first"), "UI thread"));
        _handler.HandleRecoverable(new InvalidOperationException("second"), "UI thread");

        _logger.Entries.ShouldContain(e => e.Message.StartsWith("Could not show the error dialog", StringComparison.Ordinal));
        _dialogs.ReceivedWithAnyArgs(2).ShowUnexpectedErrorAsync(default!);
    }

    [Fact]
    public void Fatal_LogsCriticalAndShowsBlockingMessage()
    {
        var incidentId = _handler.HandleFatal(new OutOfMemoryException());

        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Critical && e.Message.Contains(incidentId));
        _dialogs.Received(1).ShowFatalError(incidentId);
    }

    [Fact]
    public void Fatal_WhenMessageCannotBeShown_DoesNotThrow()
    {
        _dialogs.When(d => d.ShowFatalError(Arg.Any<string>())).Throw(new InvalidOperationException("no UI"));

        Should.NotThrow(() => _handler.HandleFatal(null));
    }
}
