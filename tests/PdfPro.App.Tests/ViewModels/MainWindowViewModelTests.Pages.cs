using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.Core.Operations;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The organizer state of the window and "Trích trang".</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task OrganizeMode_HidesTheViewerStatusAndFollowsTheActiveTab()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        viewModel.ShowsViewerStatus.ShouldBeTrue();

        viewModel.OrganizeModeCommand.Execute(null);

        viewModel.IsOrganizing.ShouldBeTrue();
        viewModel.ShowsViewerStatus.ShouldBeFalse();
        changes.ShouldContain(nameof(MainWindowViewModel.IsOrganizing));

        viewModel.SelectedTab = viewModel.Home;

        viewModel.IsOrganizing.ShouldBeFalse();
        viewModel.ShowsViewerStatus.ShouldBeFalse();
    }

    [Fact]
    public async Task PageRibbonCommands_RunOnTheActiveDocument()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var commands = AllCommands(viewModel).ToDictionary(c => c.Id);

        commands["organize.mode"].Command.Execute(null);
        viewModel.ActiveDocument!.IsOrganizing.ShouldBeTrue();
        commands["organize.insertBlank"].Command.CanExecute(null).ShouldBeTrue();
        commands["organize.insertFromFile"].Command.CanExecute(null).ShouldBeTrue();
        commands["organize.extract"].Command.CanExecute(null).ShouldBeTrue();

        viewModel.ActiveDocument.Organizer!.Select([]);

        commands["organize.extract"].Command.CanExecute(null).ShouldBeFalse();
        commands["organize.rotateRight"].Command.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractPages_WritesTheSelectedPagesToTheChosenFile()
    {
        var (viewModel, path) = await CreateWithFileAsync("báo cáo.pdf");
        var document = viewModel.ActiveDocument!;
        document.ToggleOrganizeModeCommand.Execute(null);
        document.Organizer!.Select([0, 2]);
        var target = Path.Combine(Path.GetDirectoryName(path)!, "trích.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(target);
        var exporting = new TaskCompletionSource();
        AnyExport().Returns(exporting.Task);

        Command(viewModel, "organize.extract").Command.Execute(null);

        _fileDialogs.Received(1).PickSavePath(Format(Strings.Extract_FileName, "báo cáo", "1, 3") + ".pdf", Path.GetDirectoryName(path));
        viewModel.StatusBar.IsTaskRunning.ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Extract_Progress, "trích.pdf"));
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeFalse("one task at a time");
        exporting.SetResult();
        await WaitUntilAsync(() => !viewModel.StatusBar.IsTaskRunning);

        await _saver.Received(1).ExportAsync(document.Workspace, Arg.Is<IReadOnlyList<int>>(p => p.SequenceEqual(new[] { 0, 2 })), target, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Extract_Done, 2, "trích.pdf"));
        document.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractPages_CancelledDialog_WritesNothing()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns((string?)null);

        (await viewModel.ExtractPagesAsync(viewModel.ActiveDocument!, [0])).ShouldBeFalse();

        await _saver.DidNotReceiveWithAnyArgs().ExportAsync(default!, default!, default!, default!, TestToken);
    }

    [Fact]
    public async Task ExtractPages_IntoTheDocumentsOwnFile_IsExplained()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(path);

        (await viewModel.ExtractPagesAsync(viewModel.ActiveDocument!, [0])).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Extract_Error_Title, Strings.Extract_Error_OwnFile);
        await _saver.DidNotReceiveWithAnyArgs().ExportAsync(default!, default!, default!, default!, TestToken);
    }

    [Fact]
    public async Task ExtractPages_IntoAFileOpenInAnotherTab_IsRefused()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var a = viewModel.ActiveDocument!;
        var other = _fixture.CreateFile("b.pdf");
        await viewModel.OpenFilesAsync([other]);
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(other);

        (await viewModel.ExtractPagesAsync(a, [0])).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Extract_Error_Title, Format(Strings.Save_AlreadyOpen, "b.pdf"));
    }

    [Fact]
    public async Task ExtractPages_Failure_IsExplained()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "x.pdf"));
        AnyExport().ThrowsAsync(new DocumentSaveException(SaveFailure.DiskFull));

        (await viewModel.ExtractPagesAsync(viewModel.ActiveDocument!, [1])).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Extract_Error_Title, Format(Strings.Save_Error_DiskFull, "x.pdf"));
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Extract_FailedStatus);
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractPages_Cancelled_SaysSo()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "x.pdf"));
        AnyExport().Returns(call => Task.FromCanceled(new CancellationToken(canceled: true)));

        (await viewModel.ExtractPagesAsync(viewModel.ActiveDocument!, [1])).ShouldBeFalse();

        viewModel.StatusBar.TaskText.ShouldBe(Strings.Extract_Cancelled);
    }

    [Fact]
    public async Task ClosingADocumentDuringAnExtraction_WaitsForIt()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        _fileDialogs.PickSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns(Path.Combine(Path.GetDirectoryName(path)!, "x.pdf"));
        var exporting = new TaskCompletionSource();
        AnyExport().Returns(exporting.Task);
        var extraction = viewModel.ExtractPagesAsync(document, [0]);

        var closing = viewModel.CloseTabCommand.ExecuteAsync(document);
        await Task.Delay(50, TestToken);

        closing.IsCompleted.ShouldBeFalse();
        Directory.Exists(document.Workspace.Session.Directory).ShouldBeTrue("the files are still being read");
        exporting.SetResult();
        await extraction;
        await closing;
        Directory.Exists(document.Workspace.Session.Directory).ShouldBeFalse();
    }

    private Task AnyExport()
        => _saver.ExportAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}
