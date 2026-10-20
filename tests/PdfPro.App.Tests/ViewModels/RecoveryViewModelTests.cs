using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Tests.ViewModels;

public sealed class RecoveryViewModelTests
{
    private static readonly DateTime LastChange = new(2026, 10, 20, 9, 30, 0, DateTimeKind.Local);

    private readonly RecoveryItem _first = new(Guid.NewGuid(), "a.pdf", @"C:\docs", LastChange, 3);
    private readonly RecoveryItem _second = new(Guid.NewGuid(), "b.pdf", @"D:\work", LastChange, 1);

    [Fact]
    public void Items_AreTickedAndDescribed()
    {
        var viewModel = new RecoveryViewModel([_first, _second]);

        viewModel.Items.ShouldAllBe(i => i.IsSelected);
        var item = viewModel.Items[0];
        item.FileName.ShouldBe("a.pdf");
        item.Folder.ShouldBe(@"C:\docs");
        item.Details.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Recovery_Details, LastChange.ToString("g", CultureInfo.CurrentCulture), 3));
        viewModel.HasUnchecked.ShouldBeFalse();
        viewModel.RestoreCommand.CanExecute(null).ShouldBeTrue();
        viewModel.Decision.ShouldBeNull();
    }

    [Fact]
    public void Restore_ReturnsTheTickedDocumentsAndWarnsAboutTheOthers()
    {
        var viewModel = new RecoveryViewModel([_first, _second]);
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;

        viewModel.Items[1].IsSelected = false;

        viewModel.HasUnchecked.ShouldBeTrue();
        viewModel.UncheckedWarning.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Recovery_UncheckedWarning, 1));
        viewModel.RestoreCommand.Execute(null);
        closed.ShouldBeTrue();
        var decision = viewModel.TakeDecision();
        decision.Action.ShouldBe(RecoveryAction.Restore);
        decision.Selected.ShouldBe([_first.SessionId]);
    }

    [Fact]
    public void Restore_NothingTicked_IsDisabled()
    {
        var viewModel = new RecoveryViewModel([_first]);
        var changes = 0;
        viewModel.RestoreCommand.CanExecuteChanged += (_, _) => changes++;

        viewModel.Items[0].IsSelected = false;

        viewModel.RestoreCommand.CanExecute(null).ShouldBeFalse();
        changes.ShouldBe(1);
    }

    [Fact]
    public void DiscardAll_NeedsConfirmation()
    {
        var viewModel = new RecoveryViewModel([_first, _second]);
        var closed = false;
        viewModel.CloseRequested += (_, _) => closed = true;

        viewModel.DiscardAllCommand.Execute(null);

        viewModel.IsConfirmingDiscard.ShouldBeTrue();
        viewModel.ConfirmDiscardText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Recovery_ConfirmDiscard, 2));
        closed.ShouldBeFalse();
        viewModel.CancelDiscardCommand.Execute(null);
        viewModel.IsConfirmingDiscard.ShouldBeFalse();

        viewModel.DiscardAllCommand.Execute(null);
        viewModel.ConfirmDiscardAllCommand.Execute(null);

        closed.ShouldBeTrue();
        viewModel.TakeDecision().Action.ShouldBe(RecoveryAction.DiscardAll);
    }

    [Fact]
    public void Escape_LeavesTheConfirmationThenMeansLater()
    {
        var viewModel = new RecoveryViewModel([_first]);
        viewModel.DiscardAllCommand.Execute(null);

        viewModel.EscapeCommand.Execute(null);
        viewModel.IsConfirmingDiscard.ShouldBeFalse();
        viewModel.Decision.ShouldBeNull();

        viewModel.EscapeCommand.Execute(null);
        viewModel.Decision.ShouldBe(RecoveryDecision.Later);
    }

    [Fact]
    public void ClosedWithoutAChoice_MeansLater()
        => new RecoveryViewModel([_first]).TakeDecision().Action.ShouldBe(RecoveryAction.Later);
}
