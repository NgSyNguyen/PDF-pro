using System.IO.Compression;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Converting;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"PDF sang Word" from the ribbon (the real operation writes the .docx).</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task ToWord_WritesTheDocumentWhereChosen_AndOpensIt()
    {
        var path = _fixture.CreateFile("văn bản.pdf", File.ReadAllBytes(Samples.Basic));
        var output = Path.Combine(Path.GetDirectoryName(path)!, "bản Word.docx");
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        _pageDialogs.ShowToWord(Arg.Any<ToWordViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<ToWordViewModel>();
            dialog.Tables.ShouldBeTrue();
            dialog.Pictures.ShouldBeTrue();
            dialog.OpenWhenDone.ShouldBeTrue();
            dialog.ApplyCommand.Execute(null);
            return true;
        });
        _fileDialogs.PickWordSavePath("văn bản.docx", Path.GetDirectoryName(path)).Returns(output);

        await viewModel.ToWordCommand.ExecuteAsync(null);

        using (var zip = ZipFile.OpenRead(output))
        {
            zip.GetEntry("word/document.xml").ShouldNotBeNull();
        }

        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.ToWord_Done, "bản Word.docx"));
        _shell.Received(1).OpenFile(output);
        await _messages.DidNotReceive().ShowInfoAsync(Strings.ToWord_Title, Arg.Any<string>());
    }

    [Fact]
    public async Task ToWord_OfAScan_SaysThePagesNeedOcr_AndCancellingTheSaveWritesNothing()
    {
        var path = _fixture.CreateFile("scan.pdf", File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Samples.Basic)!, "scan-vi.pdf")));
        var output = Path.Combine(Path.GetDirectoryName(path)!, "scan.docx");
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(18), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([path]);
        _pageDialogs.ShowToWord(Arg.Any<ToWordViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<ToWordViewModel>();
            dialog.Target.RangeText = "1-2";
            dialog.Target.IsRange = true;
            dialog.OpenWhenDone = false;
            dialog.ApplyCommand.Execute(null);
            return true;
        });
        _fileDialogs.PickWordSavePath(Arg.Any<string>(), Arg.Any<string?>()).Returns((string?)null, output);

        await viewModel.ToWordCommand.ExecuteAsync(null);
        File.Exists(output).ShouldBeFalse("the save was cancelled");
        await viewModel.ToWordCommand.ExecuteAsync(null);

        File.Exists(output).ShouldBeTrue();
        await _messages.Received(1).ShowInfoAsync(Strings.ToWord_Title, Format(Strings.ToWord_PagesWithoutText, "1-2"));
        _shell.DidNotReceive().OpenFile(Arg.Any<string>());
    }

    [Fact]
    public async Task Present_ShowsTheDocumentFromThePageBeingRead_AndTheViewerGoesWhereItEnded()
    {
        var path = _fixture.CreateFile("trình chiếu.pdf", File.ReadAllBytes(Samples.Basic));
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(6), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        viewModel.PresentCommand.CanExecute(null).ShouldBeFalse("no document");
        await viewModel.OpenFilesAsync([path]);
        var viewer = viewModel.ActiveDocument.ShouldNotBeNull().Viewer;
        viewer.NavigateTo(2);
        _pageDialogs.When(d => d.ShowPresentation(Arg.Any<PdfPro.App.ViewModels.Viewer.PresentationViewModel>())).Do(call =>
        {
            var presentation = call.Arg<PdfPro.App.ViewModels.Viewer.PresentationViewModel>();
            presentation.PageIndex.ShouldBe(2);
            presentation.PageCount.ShouldBe(6);
            presentation.LastCommand.Execute(null);
        });

        viewModel.PresentCommand.CanExecute(null).ShouldBeTrue();
        viewModel.PresentCommand.Execute(null);

        _pageDialogs.Received(1).ShowPresentation(Arg.Any<PdfPro.App.ViewModels.Viewer.PresentationViewModel>());
        viewer.CurrentPageIndex.ShouldBe(5, "the viewer shows the page the presentation ended on");
        PdfPro.App.ViewModels.KeyboardShortcuts.OfCommands["file.present"].ShouldBe("F5");
    }

    [Fact]
    public void TheWordDialog_ChecksItsPages_AndGivesItsChoice()
    {
        var dialog = new ToWordViewModel("hợp đồng.pdf", 5, []);
        dialog.Tables = false;
        dialog.Choice(out var error).ShouldNotBeNull().Parameters.ShouldBe(new Operations.Converting.ToWordParams(null, Tables: false, Pictures: true));
        error.ShouldBeNull();

        dialog.Target.RangeText = "9";
        dialog.Target.IsRange = true;

        dialog.ErrorText.ShouldNotBeNull();
        dialog.ApplyCommand.CanExecute(null).ShouldBeFalse();
        dialog.Target.RangeText = "2-3";
        dialog.ApplyCommand.Execute(null);
        dialog.Result.ShouldNotBeNull().Parameters.Pages.ShouldBe("2-3");
    }
}
