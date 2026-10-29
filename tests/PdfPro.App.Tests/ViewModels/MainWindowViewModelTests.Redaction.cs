using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;
using PdfPro.Core.Operations;
using PdfPro.Operations.Security;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>Redaction marks that are not applied yet: saving, closing and exiting ask about them first.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Save_PendingRedactionMarks_CanBeAppliedFirst()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        Mark(tab);
        AskPendingRedaction(1).Returns(DialogChoice.Primary);
        _pageDialogs.ShowRedactionApply(Arg.Any<RedactionApplyViewModel>()).Returns(call =>
        {
            call.Arg<RedactionApplyViewModel>().ApplyCommand.Execute(null);
            return true;
        });
        SaverMarksSaved();

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _contentChanger.Received(1).ApplyAsync(
            tab.Workspace,
            Arg.Any<RedactOperation>(),
            Arg.Is<RedactParams>(p => p.Areas.Count == 1 && p.Areas[0].Page == 1),
            Arg.Any<IProgress<OperationProgress>>(),
            Arg.Any<CancellationToken>());
        tab.HasRedactionMarks.ShouldBeFalse("the marks were applied");
        await _saver.ReceivedWithAnyArgs(1).SaveAsync(default!, default!, default!, TestToken);
    }

    [Theory]
    [InlineData(DialogChoice.Secondary, 1)]
    [InlineData(DialogChoice.Cancel, 0)]
    public async Task Save_PendingRedactionMarks_SaveAnywayKeepsThem_CancelStops(DialogChoice choice, int saves)
    {
        var (viewModel, _) = await CreateDirtyAsync();
        var tab = viewModel.ActiveDocument!;
        Mark(tab);
        AskPendingRedaction(1).Returns(choice);
        SaverMarksSaved();

        await viewModel.SaveCommand.ExecuteAsync(null);

        await _saver.ReceivedWithAnyArgs(saves).SaveAsync(default!, default!, default!, TestToken);
        tab.HasRedactionMarks.ShouldBeTrue("nothing was redacted");
        await _contentChanger.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, default!, TestToken);
    }

    [Fact]
    public async Task Save_PendingRedactionMarks_CancellingTheApplyDialog_SavesNothing()
    {
        var (viewModel, _) = await CreateDirtyAsync();
        Mark(viewModel.ActiveDocument!);
        AskPendingRedaction(1).Returns(DialogChoice.Primary);
        _pageDialogs.ShowRedactionApply(Arg.Any<RedactionApplyViewModel>()).Returns(false);

        await viewModel.SaveCommand.ExecuteAsync(null);

        await DidNotSave();
    }

    [Theory]
    [InlineData(DialogChoice.Primary, true)]
    [InlineData(DialogChoice.Cancel, false)]
    public async Task CloseTab_PendingRedactionMarks_AreDroppedOnlyWhenConfirmed(DialogChoice choice, bool closes)
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var tab = viewModel.ActiveDocument!;
        Mark(tab);
        _messages.AskAsync(Strings.Redaction_Pending_Title, Format(Strings.Redaction_Pending_Close, 1, "a.pdf"), Strings.Redaction_Pending_Drop, null, Strings.Common_Cancel)
            .Returns(choice);

        await viewModel.CloseTabCommand.ExecuteAsync(tab);

        viewModel.Documents.Contains(tab).ShouldBe(!closes);
    }

    [Fact]
    public async Task ConfirmExit_PendingRedactionMarks_CancelKeepsTheApp()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        Mark(viewModel.ActiveDocument!);
        _messages.AskAsync(Strings.Redaction_Pending_Title, Format(Strings.Redaction_Pending_Close, 1, "a.pdf"), Strings.Redaction_Pending_Drop, null, Strings.Common_Cancel)
            .Returns(DialogChoice.Cancel);

        (await viewModel.ConfirmExitAsync()).ShouldBeFalse();

        viewModel.ExitConfirmed.ShouldBeFalse();
    }

    private static void Mark(DocumentTabViewModel tab)
    {
        tab.Redaction.Add([new RedactionMark(Guid.NewGuid(), tab.Workspace.Pages.Pages[0].Key, [new PdfRect(72, 700, 300, 720)])]);
        tab.HasRedactionMarks.ShouldBeTrue();
    }

    private Task<DialogChoice> AskPendingRedaction(int marks)
        => _messages.AskAsync(
            Strings.Redaction_Pending_Title,
            Format(Strings.Redaction_Pending_Save, marks),
            Strings.Redaction_Pending_Apply,
            Strings.Redaction_Pending_SaveAnyway,
            Strings.Common_Cancel);
}
