using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Splitting;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tách": the dialog from the ribbon, marks it gives the document, and the files written in the background.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Split_WritesTheFilesInTheBackgroundAndOpensTheirFolder()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var folder = _fixture.CreateFolder("a - tách");
        AnswerSplitDialog(dialog =>
        {
            dialog.OutputFolder = folder;
            dialog.RangesText = "1, 2-3";
            dialog.RunCommand.Execute(null);
        });

        Command(viewModel, "organize.split").Command.Execute(null);
        await WaitUntilAsync(() => !viewModel.StatusBar.IsTaskRunning && viewModel.StatusBar.TaskText is not null);

        File.Exists(Path.Combine(folder, "a_001.pdf")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "a_002.pdf")).ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Split_Done, 2, "a - tách"));
        _shell.Received(1).OpenFolder(folder);
        viewModel.ActiveDocument!.IsDirty.ShouldBeFalse("splitting does not change the document");
    }

    [Fact]
    public async Task Split_ByMarks_KeepsTheNamesTypedInTheDialogAsMarkNames()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        document.Viewer.NavigateTo(1);
        document.MarkSplitCommand.Execute(null);
        var folder = _fixture.CreateFolder("out");
        AnswerSplitDialog(dialog =>
        {
            dialog.OutputFolder = folder;
            dialog.OpenFolderWhenDone = false;
            dialog.Rows[1].FileName = "Phần hai";
            dialog.RunCommand.Execute(null);
        });

        document.SplitCommand.Execute(null);
        await WaitUntilAsync(() => File.Exists(Path.Combine(folder, "Phần hai.pdf")));

        document.Workspace.Session.Plan[1].SplitMark!.FileName.ShouldBe("Phần hai");
        document.UndoToolTip.ShouldContain(Strings.History_SetSplitMarks);
        document.IsDirty.ShouldBeFalse();
        _shell.DidNotReceive().OpenFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Split_ConvertToMarks_ShowsTheMarksInTheOrganizerAndWritesNothing()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        AnswerSplitDialog(dialog =>
        {
            dialog.RangesText = "1, 2, 3";
            dialog.ConvertToMarksCommand.Execute(null);
        });

        document.SplitCommand.Execute(null);

        document.IsOrganizing.ShouldBeTrue();
        document.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 1, 2]);
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task Split_ThatCannotWrite_ExplainsAndCreatesNothing()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var blocked = _fixture.CreateFile("not a folder"); // a file where the folder should be
        AnswerSplitDialog(dialog =>
        {
            dialog.OutputFolder = blocked;
            dialog.RangesText = "1-3";
            dialog.RunCommand.Execute(null);
        });

        viewModel.ActiveDocument!.SplitCommand.Execute(null);
        await WaitUntilAsync(() => _messages.ReceivedCalls().Any());

        await _messages.Received(1).ShowErrorAsync(Strings.Split_Error_Title, Strings.Split_Error_Failed);
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Split_FailedStatus);
    }

    [Fact]
    public async Task Split_OfAPasswordProtectedDocument_IsNotOffered()
    {
        var document = FakePdfDocument.WithPages(3);
        document.Features = document.Features with { SecurityHandlerRevision = 6 };

        var (viewModel, _) = await CreateWithFileAsync("secret.pdf", document);

        Command(viewModel, "organize.split").Command.CanExecute(null).ShouldBeFalse();
    }

    private void AnswerSplitDialog(Action<SplitViewModel> answer)
        => _pageDialogs.ShowSplit(Arg.Any<SplitViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<SplitViewModel>();
            answer(dialog);
            return dialog.Result is not null;
        });
}
