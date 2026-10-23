using System.Globalization;
using PdfPro.App.Resources;
using PdfPro.App.Services.Documents;
using PdfPro.App.Tests.TestSupport;
using PdfPro.Core.Organizing;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>First-page marks for a split ("Trang đầu tệp", Ctrl+M) in the viewer, the organizer and the sidebar.</summary>
public sealed partial class DocumentTabViewModelTests
{
    [Fact]
    public async Task MarkSplit_InTheOrganizer_MarksTheSelectedPagesWithoutMakingTheDocumentUnsaved()
    {
        await using var tab = await OrganizingAsync(10, [3, 7]);
        var tiles = tab.Organizer!.Tiles;

        tab.MarkSplitCommand.Execute(null);

        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 3, 7]);
        tab.IsDirty.ShouldBeFalse();
        tab.Title.ShouldBe(tab.FileName);
        tab.UndoCommand.CanExecute(null).ShouldBeTrue();
        tab.UndoToolTip.ShouldContain(Format(Strings.History_MarkSplit, 2));
        tab.SplitSummary.ShouldBe(Format(Strings.Split_Summary, 2, 3));
        tab.Organizer.Tiles.ShouldBeSameAs(tiles, "marking keeps the grid's items");
        tab.Organizer.SelectedIndices.ShouldBe([3, 7]);
    }

    [Fact]
    public async Task MarkSplit_Badges_NumberTheFilesAndTintEveryOtherOne()
    {
        await using var tab = await OrganizingAsync(6, [2, 4]);

        tab.MarkSplitCommand.Execute(null);

        var tiles = tab.Organizer!.Tiles;
        tiles.Select(t => t.SplitBadge).ShouldBe([Format(Strings.Split_Badge, 1), null, Format(Strings.Split_Badge, 2), null, Format(Strings.Split_Badge, 3), null]);
        tiles.Select(t => t.IsAlternateFile).ShouldBe([false, false, true, true, false, false]);
        tiles.Select(t => t.IsSplitStart).ShouldBe([true, false, true, false, true, false]);
        tiles[2].SplitToolTip.ShouldBe(Format(Strings.Split_BadgeToolTip, 2));
        tab.Thumbnails.Items.Select(t => t.SplitBadge).ShouldBe(tiles.Select(t => t.SplitBadge));
    }

    [Fact]
    public async Task MarkSplit_Again_RemovesTheMarks()
    {
        await using var tab = await OrganizingAsync(6, [2, 4]);
        tab.MarkSplitCommand.Execute(null);

        tab.MarkSplitCommand.Execute(null);

        tab.Workspace.Session.Plan.HasSplitMarks.ShouldBeFalse();
        tab.UndoToolTip.ShouldContain(Format(Strings.History_UnmarkSplit, 2));
        tab.SplitSummary.ShouldBeNull();
        tab.Organizer!.Tiles.ShouldAllBe(t => t.SplitBadge == null && !t.IsAlternateFile);
    }

    [Fact]
    public async Task MarkSplit_WhenOnlySomeTargetsAreMarked_MarksThemAll()
    {
        await using var tab = await OrganizingAsync(6, [2]);
        tab.MarkSplitCommand.Execute(null);
        tab.Organizer!.Select([2, 4]);

        tab.MarkSplitCommand.Execute(null);

        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 2, 4]);
    }

    [Fact]
    public async Task MarkSplit_TheFirstPageAlone_CannotBeMarked()
    {
        await using var tab = await OrganizingAsync(4, [0]);

        tab.MarkSplitCommand.CanExecute(null).ShouldBeFalse("the first page always starts the first file");
        tab.Organizer!.Select([0, 1]);
        tab.MarkSplitCommand.CanExecute(null).ShouldBeTrue();
        tab.MarkSplitCommand.Execute(null);
        tab.Workspace.Session.Plan[0].SplitMark.ShouldBeNull();
        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 1]);
    }

    [Fact]
    public async Task MarkSplit_InTheViewer_MarksTheCurrentPageAndShowsItsFile()
    {
        await using var tab = await CreateAsync(8);
        tab.Viewer.NavigateTo(4);

        tab.MarkSplitCommand.Execute(null);
        tab.Viewer.NavigateTo(6);

        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 4]);
        tab.CurrentSplitFile.ShouldBe(Format(Strings.Split_CurrentFile, 2, 2));
        tab.Viewer.NavigateTo(1);
        tab.CurrentSplitFile.ShouldBe(Format(Strings.Split_CurrentFile, 1, 2));
    }

    [Fact]
    public async Task MarkSplit_IsUndoneAndRedoneLikeAnyEdit()
    {
        await using var tab = await OrganizingAsync(6, [3]);
        tab.MarkSplitCommand.Execute(null);

        tab.UndoCommand.Execute(null);

        tab.Workspace.Session.Plan.HasSplitMarks.ShouldBeFalse();
        tab.Organizer!.Tiles[3].SplitBadge.ShouldBeNull();
        tab.RedoCommand.Execute(null);
        tab.Organizer.Tiles[3].SplitBadge.ShouldBe(Format(Strings.Split_Badge, 2));
    }

    [Fact]
    public async Task MarksFollowTheirPages_AndADeletedMarkedPageTakesItsMarkAlong()
    {
        await using var tab = await OrganizingAsync(6, [3]);
        tab.MarkSplitCommand.Execute(null);

        tab.MovePages([3], 1).ShouldBeTrue();
        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 1]);

        tab.Organizer!.Select([1]);
        tab.DeletePagesCommand.Execute(null);
        tab.Workspace.Session.Plan.HasSplitMarks.ShouldBeFalse();
        tab.IsDirty.ShouldBeTrue("moving and deleting pages are changes");
    }

    [Fact]
    public async Task ClearSplitMarks_RemovesEveryMarkInOneStep()
    {
        await using var tab = await OrganizingAsync(8, [2, 5, 7]);
        tab.ClearSplitMarksCommand.CanExecute(null).ShouldBeFalse();
        tab.MarkSplitCommand.Execute(null);

        tab.ClearSplitMarksCommand.Execute(null);

        tab.Workspace.Session.Plan.HasSplitMarks.ShouldBeFalse();
        tab.UndoToolTip.ShouldContain(Strings.History_SetSplitMarks);
        tab.UndoCommand.Execute(null);
        tab.Workspace.Session.Plan.GetSplitStarts().ShouldBe([0, 2, 5, 7]);
        tab.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task SplitSummary_CountsMarksAndFiles()
    {
        await using var tab = await CreateAsync(5);
        tab.SplitSummary.ShouldBeNull();
        tab.CurrentSplitFile.ShouldBeNull();

        tab.Workspace.Apply(TestOperations.Catalog().Get<Operations.Organizing.SetSplitMarksOperation>(), new Operations.Organizing.SetSplitMarksParams([new(1, "Tờ trình")]));

        tab.SplitSummary.ShouldBe(Format(Strings.Split_Summary, 0, 1), "naming the first file adds no file");
        tab.Organizer.ShouldBeNull();
        tab.Thumbnails.Items[0].SplitToolTip.ShouldBe(Format(Strings.Split_BadgeToolTipNamed, 1, "Tờ trình"));
    }

    [Fact]
    public void HistoryDescriptions_NameTheMarkSteps()
    {
        var plan = PagePlan.Identity("s0", 5, RandomPageKeys.Instance);

        Describe(new Operations.Organizing.MarkSplitParams("2-3")).ShouldBe(Format(Strings.History_MarkSplit, 2));
        Describe(new Operations.Organizing.MarkSplitParams("2", Marked: false)).ShouldBe(Format(Strings.History_UnmarkSplit, 1));
        Describe(new Operations.Organizing.SetSplitMarksParams([])).ShouldBe(Strings.History_SetSplitMarks);
        Describe(new Operations.Organizing.InterleaveParams(3)).ShouldBe(Strings.History_Interleave);

        string Describe(object parameters) => HistoryDescriptions.Describe(new Core.Sessions.HistoryRecord("organize.x", parameters, []), plan, _ => null);
    }

    private static string Format(string template, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, template, arguments);
}
