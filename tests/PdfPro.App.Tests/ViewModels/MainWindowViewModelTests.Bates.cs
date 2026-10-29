using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Operations;
using PdfPro.Core.Stamping;
using PdfPro.Operations.Stamping;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Số Bates" from the ribbon: the dialog, the copies written in the background, and failures.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Bates_StartsWithTheOpenDocument_WritesTheCopies_AndOpensTheirFolder()
    {
        var (viewModel, path) = await CreateWithFileAsync("hồ sơ.pdf");
        _bates.AddAsync(path, "hồ sơ.pdf", Arg.Any<CancellationToken>()).Returns(new BatesFile(path, "hồ sơ.pdf", 3));
        var folder = _fixture.CreateFolder("đánh số");
        var output = Path.Combine(folder, "hồ sơ.pdf");
        AnswerBatesDialog(folder, prefix: "HS-");
        _bates.WriteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<BatesParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(new BatesResult([output], [new BatesRange(0, 1, 3)], 4));

        await viewModel.BatesCommand.ExecuteAsync(null);

        await _bates.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == path),
            Arg.Is<BatesParams>(p => p.Prefix == "HS-"),
            folder,
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Format(Strings.Bates_Done, 1, "HS-000001", "HS-000003"));
        _shell.Received(1).OpenFolder(folder);
    }

    [Fact]
    public async Task Bates_OfAnEditedDocument_NumbersACopyNamedLikeIt_AndRemovesItAfterwards()
    {
        var (viewModel, path) = await CreateDirtyAsync();
        var document = viewModel.ActiveDocument!;
        BatesViewModel? shown = null;
        _pageDialogs.ShowBates(Arg.Any<BatesViewModel>()).Returns(call =>
        {
            shown = call.Arg<BatesViewModel>();
            return false;
        });

        await viewModel.BatesCommand.ExecuteAsync(null);

        shown.ShouldNotBeNull();
        await _saver.Received(1).ExportAsync(
            document.Workspace,
            Arg.Is<IReadOnlyList<int>>(p => p.Count == 3),
            Arg.Is<string>(p => p.StartsWith(document.Workspace.WorkDirectory, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(p) == "a.pdf"),
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
        await _bates.Received(1).AddAsync(Arg.Is<string>(p => Path.GetFileName(p) == "a.pdf" && p != path), Format(Strings.Merge_CurrentDocument, "a.pdf"), Arg.Any<CancellationToken>());
        Directory.GetDirectories(document.Workspace.WorkDirectory, "bates-*").ShouldBeEmpty("the working copy is removed");
    }

    [Fact]
    public async Task Bates_FileThatCannotBeNumbered_IsNamedInTheError()
    {
        var (viewModel, path) = await CreateWithFileAsync("hồ sơ.pdf");
        _bates.AddAsync(path, "hồ sơ.pdf", Arg.Any<CancellationToken>()).Returns(new BatesFile(path, "hồ sơ.pdf", 3));
        AnswerBatesDialog(_fixture.CreateFolder("đánh số"));
        _bates.WriteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<BatesParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new BatesFileException(0, new InvalidDataException("damaged")));

        await viewModel.BatesCommand.ExecuteAsync(null);

        await _messages.Received(1).ShowErrorAsync(Strings.Bates_Error_Title, Format(Strings.Bates_Error_File, "hồ sơ.pdf"));
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Bates_FailedStatus);
        _shell.DidNotReceiveWithAnyArgs().OpenFolder(default!);
        viewModel.SaveCommand.CanExecute(null).ShouldBeFalse("nothing is unsaved");
        viewModel.BatesCommand.CanExecute(null).ShouldBeTrue("the run is over");
    }

    [Fact]
    public async Task Bates_Cancelled_SaysSo()
    {
        var (viewModel, path) = await CreateWithFileAsync("hồ sơ.pdf");
        _bates.AddAsync(path, "hồ sơ.pdf", Arg.Any<CancellationToken>()).Returns(new BatesFile(path, "hồ sơ.pdf", 3));
        AnswerBatesDialog(_fixture.CreateFolder("đánh số"));
        _bates.WriteAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<BatesParams>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await viewModel.BatesCommand.ExecuteAsync(null);

        viewModel.StatusBar.TaskText.ShouldBe(Strings.Bates_Cancelled);
        await _messages.DidNotReceiveWithAnyArgs().ShowErrorAsync(default!, default!);
    }

    [Fact]
    public async Task Bates_WithoutTheService_IsOff()
    {
        var viewModel = Create(isDebugBuild: false, withBates: false);

        viewModel.BatesCommand.CanExecute(null).ShouldBeFalse();
        await viewModel.BatesCommand.ExecuteAsync(null);

        _pageDialogs.DidNotReceive().ShowBates(Arg.Any<BatesViewModel>());
    }

    /// <summary>The dialog numbers what it was given into <paramref name="folder"/>.</summary>
    private void AnswerBatesDialog(string folder, string prefix = "")
        => _pageDialogs.ShowBates(Arg.Any<BatesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<BatesViewModel>();
            dialog.Prefix = prefix;
            dialog.OutputFolder = folder;
            dialog.RunCommand.Execute(null);
            return dialog.Result is not null;
        });
}
