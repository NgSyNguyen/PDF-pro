using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Optimizing;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Operations;
using PdfPro.Infrastructure.Instances;
using PdfPro.Operations;
using PdfPro.Operations.Robustness;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>
/// "Chế độ an toàn": open files are recorded while they are open; a file open at the last crash is tried in the worker
/// before it opens again, and the user decides when the worker stops on it too or cannot try it.
/// </summary>
public sealed partial class MainWindowViewModelTests
{
    private readonly IOpenDocumentGuard _guard = Substitute.For<IOpenDocumentGuard>();
    private readonly IExportRunner _probes = Substitute.For<IExportRunner>();

    [Fact]
    public async Task OpenDocuments_AreRecorded_WhileTheyAreOpen()
    {
        var (viewModel, path) = await OpenGuardedAsync();

        _guard.Received(1).Opened(path);
        viewModel.Tabs.Remove(viewModel.ActiveDocument!);
        _guard.Received(1).Closed(path);
        await _probes.DidNotReceiveWithAnyArgs().MeasureAsync<FileProbeResult?>(default!, default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFileOpenAtTheLastCrash_IsTriedInTheWorker_ThenOpens()
    {
        Probing().Returns(Task.FromResult<FileProbeResult?>(new FileProbeResult(3, NeedsPassword: false)));

        var (viewModel, path) = await OpenGuardedAsync(suspect: true);

        viewModel.ActiveDocument.ShouldNotBeNull();
        _guard.Received(1).Cleared(path);
        await _messages.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default!, default, default!);
    }

    [Fact]
    public async Task AFileThatStopsTheWorkerToo_OpensOnlyWhenTheUserSaysSo()
    {
        Probing().ThrowsAsync(new WorkerException(WorkerFailure.Crashed));
        Asked().Returns(DialogChoice.Cancel);

        var (viewModel, path) = await OpenGuardedAsync(suspect: true);

        viewModel.ActiveDocument.ShouldBeNull("not opened: it may stop the app again");
        _guard.DidNotReceive().Cleared(path);
        await _messages.Received(1).AskAsync(Strings.Safety_Title, Format(Strings.Safety_Crashed, "nghi vấn.pdf"), Strings.Safety_OpenAnyway, Arg.Is<string?>(s => s == null), Strings.Safety_DontOpen);

        Asked().Returns(DialogChoice.Primary);
        await viewModel.OpenFilesAsync([path]);
        viewModel.ActiveDocument.ShouldNotBeNull("opened anyway, as the user chose");
        _guard.Received(1).Cleared(path);
    }

    [Fact]
    public async Task WithoutTheWorker_TheUserIsAsked_AndADamagedFileIsNotACrash()
    {
        Probing().Returns(call => call.ArgAt<Func<IProgress<OperationProgress>, CancellationToken, Task<FileProbeResult?>>>(3)(new Progress<OperationProgress>(), CancellationToken.None));
        Asked().Returns(DialogChoice.Primary);

        var (viewModel, path) = await OpenGuardedAsync(suspect: true);

        await _messages.Received(1).AskAsync(Strings.Safety_Title, Format(Strings.Safety_NotChecked, "nghi vấn.pdf"), Strings.Safety_OpenAnyway, Arg.Is<string?>(s => s == null), Strings.Safety_DontOpen);
        viewModel.ActiveDocument.ShouldNotBeNull();

        Probing().ThrowsAsync(new System.IO.InvalidDataException("hỏng"));
        viewModel.Tabs.Remove(viewModel.ActiveDocument!);
        _messages.ClearReceivedCalls();
        await viewModel.OpenFilesAsync([path]);
        viewModel.ActiveDocument.ShouldNotBeNull("a file PDFium refuses opens as usual and says why");
        await _messages.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default!, default, default!);
    }

    private Task<FileProbeResult?> Probing()
        => _probes.MeasureAsync<FileProbeResult?>(
            Arg.Is<IMeasuringOperation>(o => o.Id == FileProbe.OperationId),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<Func<IProgress<OperationProgress>, CancellationToken, Task<FileProbeResult?>>>(),
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());

    private Task<DialogChoice> Asked()
        => _messages.AskAsync(Strings.Safety_Title, Arg.Any<string>(), Strings.Safety_OpenAnyway, Arg.Is<string?>(s => s == null), Strings.Safety_DontOpen);

    private async Task<(MainWindowViewModel ViewModel, string Path)> OpenGuardedAsync(bool suspect = false)
    {
        var path = _fixture.CreateFile("nghi vấn.pdf", File.ReadAllBytes(Samples.Basic));
        _guard.IsSuspect(path).Returns(suspect);
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(3), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false, openGuard: _guard, exports: _probes);
        await viewModel.OpenFilesAsync([path]);
        return (viewModel, path);
    }
}
