using PdfPro.App.ViewModels;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Tìm" (Ctrl+F), F3 / Shift+F3 and "Bình luận" from the window.</summary>
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task Find_ShowsTheSearchPanel_AndAsksForTheQueryBox_LeavingTheOrganizer()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var asked = 0;
        viewModel.SearchRequested += (_, _) => asked++;
        viewModel.OrganizeModeCommand.Execute(null);
        viewModel.IsOrganizing.ShouldBeTrue();

        viewModel.ShowSearchCommand.Execute(null);

        viewModel.Sidebar.SelectedPanel.ShouldBe(SidebarPanel.Search);
        viewModel.Sidebar.IsSearchSelected.ShouldBeTrue();
        viewModel.IsOrganizing.ShouldBeFalse("the matches are shown on the pages of the viewer");
        asked.ShouldBe(1);
    }

    [Fact]
    public async Task Find_NeedsADocument_AndF3FollowsTheActiveOne()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        var document = viewModel.ActiveDocument!;

        viewModel.SelectedTab = viewModel.Home;
        viewModel.ShowSearchCommand.CanExecute(null).ShouldBeFalse();
        viewModel.FindNextCommand.CanExecute(null).ShouldBeFalse();

        viewModel.SelectedTab = document;
        viewModel.ShowSearchCommand.CanExecute(null).ShouldBeTrue();
        viewModel.FindNextCommand.CanExecute(null).ShouldBeTrue();
        viewModel.FindPreviousCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Comments_ShowsTheCommentsPanel_LeavingTheOrganizer()
    {
        var (viewModel, _) = await CreateWithFileAsync("a.pdf");
        viewModel.OrganizeModeCommand.Execute(null);

        viewModel.ShowCommentsCommand.Execute(null);

        viewModel.Sidebar.SelectedPanel.ShouldBe(SidebarPanel.Comments);
        viewModel.IsOrganizing.ShouldBeFalse();
        viewModel.ActiveDocument!.Comments.IsActive.ShouldBeTrue("the list is read while the panel is shown");

        viewModel.SelectedTab = viewModel.Home;
        viewModel.ShowCommentsCommand.CanExecute(null).ShouldBeFalse();
    }
}
