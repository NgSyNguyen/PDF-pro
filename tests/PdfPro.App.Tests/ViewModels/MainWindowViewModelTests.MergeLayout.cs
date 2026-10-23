using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Merging;
using PdfPro.App.ViewModels.Organizer;
using PdfPro.Core.Operations;
using PdfPro.Operations.Layout;
using PdfPro.Operations.Materializing;
using PdfPro.Operations.Merging;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Gộp" from the home tab and the ribbon, "Trộn mặt trước/sau", and the sheet layouts.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Merge_FromTheHomeTab_WritesTheResultAndOpensIt()
    {
        var viewModel = Create(isDebugBuild: false);
        var output = _fixture.CreateFolder("gộp.pdf");
        await PrepareMergeAsync();
        _merge.WriteAsync(Arg.Any<IReadOnlyList<MergeSource>>(), Arg.Any<MergeParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(output));
        _workspaces.OpenAsync(output, Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(5), output));
        AnswerMergeDialog(output);

        viewModel.Home.MergeCommand.ShouldBeSameAs(viewModel.MergeCommand);
        await viewModel.MergeCommand.ExecuteAsync(null);

        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Merge_Done, 2, "gộp.pdf"));
        await _merge.Received(1).WriteAsync(Arg.Any<IReadOnlyList<MergeSource>>(), Arg.Any<MergeParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        viewModel.ActiveDocument.ShouldNotBeNull().FileName.ShouldBe("gộp.pdf");
    }

    [Fact]
    public async Task Merge_ThatCannotWrite_Explains()
    {
        var viewModel = Create(isDebugBuild: false);
        var output = _fixture.CreateFolder("gộp.pdf");
        await PrepareMergeAsync();
        _merge.WriteAsync(Arg.Any<IReadOnlyList<MergeSource>>(), Arg.Any<MergeParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
        AnswerMergeDialog(output);

        await viewModel.MergeCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Merge_Error_Title, Format(Strings.Merge_Error_Failed, "gộp.pdf"));
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Merge_FailedStatus);
    }

    [Fact]
    public async Task Merge_StartsWithTheOpenDocument()
    {
        var (viewModel, path) = await CreateWithFileAsync("hồ sơ.pdf");
        await PrepareMergeAsync();
        MergeViewModel? shown = null;
        _pageDialogs.ShowMerge(Arg.Any<MergeViewModel>()).Returns(call =>
        {
            shown = call.Arg<MergeViewModel>();
            return false;
        });

        await viewModel.MergeCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull().Items.ShouldHaveSingleItem().FileName.ShouldBe("hồ sơ.pdf");
        shown.OutputPath.ShouldBe(Path.Combine(Path.GetDirectoryName(path)!, Format(Strings.Merge_DefaultName, "hồ sơ") + ".pdf"));
        await _merge.Received(1).AddAsync(path, "hồ sơ.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Merge_OfAnEditedDocument_UsesACopyOfWhatItShows()
    {
        var (viewModel, path) = await CreateWithFileAsync("hồ sơ.pdf");
        var document = viewModel.ActiveDocument!;
        document.RotateRightCommand.Execute(null);
        await PrepareMergeAsync();
        _pageDialogs.ShowMerge(Arg.Any<MergeViewModel>()).Returns(false);

        await viewModel.MergeCommand.ExecuteAsync(null);

        await _saver.Received(1).ExportAsync(
            document.Workspace,
            Arg.Is<IReadOnlyList<int>>(p => p.SequenceEqual(new[] { 0, 1, 2 })),
            Arg.Is<string>(p => p.StartsWith(document.Workspace.WorkDirectory, StringComparison.OrdinalIgnoreCase)),
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
        Directory.Exists(document.Workspace.WorkDirectory).ShouldBeTrue();
        await _merge.DidNotReceive().AddAsync(path, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interleave_ReordersTheOpenDocumentAsOneUndoableStep()
    {
        var (viewModel, _) = await CreateWithFileAsync("scan.pdf", FakePdfDocument.WithPages(4));
        var document = viewModel.ActiveDocument!;
        _pageDialogs.ShowInterleave(Arg.Any<InterleaveViewModel>()).Returns(call =>
        {
            call.Arg<InterleaveViewModel>().ApplyCommand.Execute(null); // backs = pages 3-4, reversed
            return true;
        });

        Command(viewModel, "organize.interleave").Command.Execute(null);

        document.Workspace.Session.Plan.Select(p => p.SourcePageIndex).ShouldBe([0, 3, 1, 2]);
        document.UndoToolTip.ShouldContain(Strings.History_Interleave);
        document.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Layout_WritesTheDocumentThenLaysItOutAndOpensTheResult()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;
        var output = _fixture.CreateFolder("a - sổ gập.pdf");
        string? input = null;
        _workspaces.OpenAsync(output, Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), output));
        var request = new LayoutRequest((path, _, _) =>
        {
            input = path;
            return Task.FromResult(output);
        }, output, OpenWhenDone: true);

        (await viewModel.LayoutAsync(document, request)).ShouldBeTrue();

        await _saver.Received(1).ExportAsync(document.Workspace, Arg.Is<IReadOnlyList<int>>(p => p.SequenceEqual(new[] { 0, 1, 2 })), input!, Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
        input.ShouldStartWith(document.Workspace.WorkDirectory);
        Directory.Exists(document.Workspace.WorkDirectory).ShouldBeTrue("the working copy needs its folder");
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Layout_Done, "a - sổ gập.pdf"));
        viewModel.ActiveDocument!.FileName.ShouldBe("a - sổ gập.pdf");
    }

    [Fact]
    public async Task Layout_IntoAnOpenDocument_IsRefused()
    {
        var (viewModel, path) = await CreateWithFileAsync("a.pdf");
        var request = new LayoutRequest((_, _, _) => Task.FromResult(path), path, OpenWhenDone: false);

        (await viewModel.LayoutAsync(viewModel.ActiveDocument!, request)).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Layout_Error_Title, Format(Strings.Save_AlreadyOpen, "a.pdf"));
    }

    [Fact]
    public async Task LayoutRibbonCommands_ShowTheirDialog()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var kinds = new List<SheetLayoutKind>();
        _pageDialogs.ShowSheetLayout(Arg.Any<SheetLayoutViewModel>()).Returns(call =>
        {
            kinds.Add(call.Arg<SheetLayoutViewModel>().Kind);
            return false;
        });

        Command(viewModel, "layout.nup").Command.Execute(null);
        Command(viewModel, "layout.booklet").Command.Execute(null);
        Command(viewModel, "layout.tile").Command.Execute(null);

        kinds.ShouldBe([SheetLayoutKind.NUp, SheetLayoutKind.Booklet, SheetLayoutKind.Tile]);
    }

    /// <summary>The merge service reads "x.pdf" files as fake 2-page documents and previews like the real one.</summary>
    private async Task PrepareMergeAsync()
    {
        var loader = Substitute.For<IPdfDocumentLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IPdfDocument>(FakePdfDocument.WithPages(2)));
        var operation = new MergeOperation(new PdfSharpPlanMaterializer(Substitute.For<ISourceRepairer>()), loader);
        var sample = await MergeSource.OpenAsync(loader, _fixture.CreateFolder("sample.pdf"), null, TestContext.Current.CancellationToken);
        _merge.AddAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<MergeSource?>(MergeSource.OpenAsync(loader, call.ArgAt<string>(0), null, CancellationToken.None).GetAwaiter().GetResult()));
        _merge.Layout(Arg.Any<IReadOnlyList<MergeSource>>(), Arg.Any<MergeParams>()).Returns(call => operation.Layout(call.ArgAt<IReadOnlyList<MergeSource>>(0), call.ArgAt<MergeParams>(1)));
        sample.PageCount.ShouldBe(2);
    }

    private void AnswerMergeDialog(string output)
        => _pageDialogs.ShowMerge(Arg.Any<MergeViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<MergeViewModel>();
            dialog.AddPathsAsync([_fixture.CreateFolder("a.pdf"), _fixture.CreateFolder("b.pdf")]).GetAwaiter().GetResult();
            dialog.OutputPath = output;
            dialog.RunCommand.Execute(null);
            return dialog.Result is not null;
        });
}
