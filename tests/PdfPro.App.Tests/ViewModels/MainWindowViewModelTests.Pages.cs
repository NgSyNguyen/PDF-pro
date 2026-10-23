using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.Core.Operations;
using PdfPro.Operations.Organizing;

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

    [Fact]
    public async Task ChangeContent_RunsInTheBackgroundWithProgress()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        var request = new ContentChangeRequest(new ChangePageSizeOperation(), new ChangePageSizeParams("1", 420, 595), "Đổi khổ giấy 1 trang");
        var running = new TaskCompletionSource();
        AnyContentChange().Returns(running.Task);

        var change = viewModel.ChangeContentAsync(document, request);

        viewModel.StatusBar.IsTaskRunning.ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Content_Progress, "Đổi khổ giấy 1 trang"));
        viewModel.SaveAsCommand.CanExecute(null).ShouldBeFalse("one task at a time");
        running.SetResult();
        (await change).ShouldBeTrue();

        await _contentChanger.Received(1).ApplyAsync(document.Workspace, request.Operation, request.Parameters, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Content_Done, "Đổi khổ giấy 1 trang"));
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ContentChangeFailure.SourceUnreadable)]
    [InlineData(ContentChangeFailure.DiskFull)]
    [InlineData(ContentChangeFailure.Failed)]
    public async Task ChangeContent_Failure_IsExplained(ContentChangeFailure failure)
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        AnyContentChange().ThrowsAsync(new ContentChangeException(failure));
        var expected = failure switch
        {
            ContentChangeFailure.SourceUnreadable => Strings.Content_Error_SourceUnreadable,
            ContentChangeFailure.DiskFull => Strings.Content_Error_DiskFull,
            _ => Strings.Content_Error_Failed,
        };

        (await viewModel.ChangeContentAsync(viewModel.ActiveDocument!, PageSizeRequest())).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Content_Error_Title, expected);
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Content_FailedStatus, "x"));
    }

    [Fact]
    public async Task ChangeContent_Cancelled_SaysSo()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        AnyContentChange().Returns(_ => Task.FromCanceled(new CancellationToken(canceled: true)));

        (await viewModel.ChangeContentAsync(viewModel.ActiveDocument!, PageSizeRequest())).ShouldBeFalse();

        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Content_Cancelled, "x"));
        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
    }

    [Fact]
    public async Task ChangeContent_RefusedParameters_AreExplained()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        AnyContentChange().ThrowsAsync(new OperationValidationException("organize.pageSize", [new OperationValidationError("Pages", "bad")]));

        (await viewModel.ChangeContentAsync(viewModel.ActiveDocument!, PageSizeRequest())).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Content_Error_Title, Strings.Edit_Error_Invalid);
    }

    [Fact]
    public async Task PageSizeRibbonCommand_ReachesTheContentChanger()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        _pageDialogs.ShowPageSize(Arg.Any<PdfPro.App.ViewModels.Organizer.PageSizeViewModel>()).Returns(call =>
        {
            call.Arg<PdfPro.App.ViewModels.Organizer.PageSizeViewModel>().ApplyCommand.Execute(null);
            return true;
        });
        AnyContentChange().Returns(Task.CompletedTask);

        Command(viewModel, "organize.pageSize").Command.Execute(null);
        await WaitUntilAsync(() => _contentChanger.ReceivedCalls().Any());

        await _contentChanger.Received(1).ApplyAsync(
            viewModel.ActiveDocument!.Workspace,
            Arg.Any<ChangePageSizeOperation>(),
            new ChangePageSizeParams("1", 595, 842),
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkSplitRibbonCommand_MarksTheActiveDocumentsCurrentPage()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        var mark = Command(viewModel, "organize.markSplit");
        mark.Command.CanExecute(null).ShouldBeFalse("the first page always starts the first file");
        mark.ToolTip.ShouldBe(Strings.Cmd_MarkSplit_ToolTip);

        document.Viewer.NavigateTo(document.Viewer.PageCount - 1);
        viewModel.MarkSplitCommand.Execute(null);

        document.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, document.Viewer.PageCount - 1]);
        document.IsDirty.ShouldBeFalse();
        Command(viewModel, "organize.clearSplitMarks").Command.CanExecute(null).ShouldBeTrue();
    }

    private static ContentChangeRequest PageSizeRequest() => new(new ChangePageSizeOperation(), new ChangePageSizeParams("1", 420, 595), "x");

    private Task AnyContentChange()
        => _contentChanger.ApplyAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<Core.Organizing.IContentOperation>(), Arg.Any<object>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());

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
