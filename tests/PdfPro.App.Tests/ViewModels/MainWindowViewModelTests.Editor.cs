using NSubstitute;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Core.Overlay;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The page editor seen from the window: objects waiting to be applied are written before saving or closing.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Save_AppliesObjectsWaitingInThePageEditorFirst()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse("nothing changed yet");
        AddWhiteout(document);
        AnyContentChange().Returns(Task.CompletedTask);
        AnySave().Returns(Task.CompletedTask);

        viewModel.SaveCommand.CanExecute(null).ShouldBeTrue("objects wait to be written");
        await viewModel.SaveCommand.ExecuteAsync(null);

        await _contentChanger.Received(1).ApplyAsync(document.Workspace, Arg.Any<OverlayOperation>(), Arg.Any<OverlayParams>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        await _saver.Received(1).SaveAsync(document.Workspace, path, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        document.Editor.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_IsNotDone_WhenTheObjectsCannotBeWritten()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        AddWhiteout(document);
        AnyContentChange().Returns(Task.FromException(new ContentChangeException(ContentChangeFailure.Failed, new IOException("disk"))));

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.DidNotReceive().SaveAsync(Arg.Any<DocumentWorkspace>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        document.Editor.Objects.Count.ShouldBe(1, "the objects stay in the editor");
    }

    private static void AddWhiteout(DocumentTabViewModel document)
    {
        var key = document.Workspace.Pages.Pages[0].Key;
        document.Editor.GetGeometryAsync(key).GetAwaiter().GetResult();
        document.Editor.Add(key, new ShapeOverlay(0, new OverlayBox(new PdfPoint(100, 100), new PdfSize(20, 20)), ShapeKind.Whiteout));
    }
}
