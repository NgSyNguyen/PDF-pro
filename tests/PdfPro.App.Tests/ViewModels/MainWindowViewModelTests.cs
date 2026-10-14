using NSubstitute;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure;

namespace PdfPro.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IAppLifetime _lifetime = Substitute.For<IAppLifetime>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly AppPaths _paths = new(@"C:\data\PdfPro", @"C:\app\tools");

    [Fact]
    public void Ribbon_HasTheSevenTabsInOrder()
    {
        var viewModel = Create(isDebugBuild: false);

        viewModel.RibbonTabs.Select(t => t.Id).ShouldBe(["file", "page", "edit", "annotate", "security", "convert", "tools"]);
        viewModel.RibbonTabs.Select(t => t.Header).ShouldBe(
        [
            Strings.Ribbon_Tab_File,
            Strings.Ribbon_Tab_Page,
            Strings.Ribbon_Tab_Edit,
            Strings.Ribbon_Tab_Annotate,
            Strings.Ribbon_Tab_Security,
            Strings.Ribbon_Tab_Convert,
            Strings.Ribbon_Tab_Tools,
        ]);
    }

    [Fact]
    public void Ribbon_CommandIdsAreUnique()
    {
        var ids = AllCommands(Create(isDebugBuild: true)).Select(c => c.Id).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    [Fact]
    public void Ribbon_OnlyImplementedCommandsAreEnabled()
    {
        var commands = AllCommands(Create(isDebugBuild: true)).ToList();

        commands.Where(c => c.IsAvailable).Select(c => c.Id).ShouldBe(
            ["app.settings", "app.exit", "tools.settings", "tools.openLogFolder", "tools.throwTestException"],
            ignoreOrder: true);

        commands.Where(c => !c.IsAvailable).ShouldAllBe(c =>
            !c.Command.CanExecute(null) && c.ToolTip.Contains(Strings.Cmd_NotAvailableYet));
    }

    [Fact]
    public void ReleaseBuild_HasNoThrowTestExceptionButton()
        => AllCommands(Create(isDebugBuild: false)).ShouldNotContain(c => c.Id == "tools.throwTestException");

    [Fact]
    public void SettingsCommand_OpensSettingsDialog()
    {
        Command(Create(isDebugBuild: false), "app.settings").Command.Execute(null);

        _dialogs.Received(1).ShowSettings();
    }

    [Fact]
    public void ExitCommand_ShutsDownTheApp()
    {
        Command(Create(isDebugBuild: false), "app.exit").Command.Execute(null);

        _lifetime.Received(1).Shutdown();
    }

    [Fact]
    public void OpenLogFolderCommand_OpensTheLogsDirectory()
    {
        Command(Create(isDebugBuild: false), "tools.openLogFolder").Command.Execute(null);

        _shell.Received(1).OpenFolder(_paths.LogsDirectory);
    }

    [Fact]
    public void ThrowTestExceptionCommand_Throws()
        => Should.Throw<InvalidOperationException>(
            () => Command(Create(isDebugBuild: true), "tools.throwTestException").Command.Execute(null));

    [Fact]
    public void Workspace_StartsWithTheHomeTabSelected()
    {
        var viewModel = Create(isDebugBuild: false);

        var home = viewModel.Tabs.ShouldHaveSingleItem().ShouldBeOfType<HomeTabViewModel>();
        viewModel.SelectedTab.ShouldBeSameAs(home);
        home.CanClose.ShouldBeFalse();
        home.HasNoRecentFiles.ShouldBeTrue();
        home.OpenFileCommand.CanExecute(null).ShouldBeFalse("opening documents arrives in Phase 2");
    }

    [Fact]
    public void HomeTab_RecentFilesFlagFollowsCollection()
    {
        var home = new HomeTabViewModel();
        var raised = new List<string?>();
        home.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        home.RecentFiles.Add(@"C:\docs\a.pdf");

        home.HasNoRecentFiles.ShouldBeFalse();
        raised.ShouldContain(nameof(HomeTabViewModel.HasNoRecentFiles));
    }

    [Fact]
    public void Sidebar_SwitchesBetweenPanels()
    {
        var sidebar = Create(isDebugBuild: false).Sidebar;
        sidebar.IsThumbnailsSelected.ShouldBeTrue();

        sidebar.IsBookmarksSelected = true;

        sidebar.SelectedPanel.ShouldBe(SidebarPanel.Bookmarks);
        sidebar.IsThumbnailsSelected.ShouldBeFalse();
    }

    [Fact]
    public void StatusBar_StartsReadyWithoutDocument()
    {
        var status = Create(isDebugBuild: false).StatusBar;

        status.TaskText.ShouldBe(Strings.Status_Ready);
        status.IsTaskRunning.ShouldBeFalse();
        status.PageText.ShouldContain("—");
    }

    private static IEnumerable<RibbonCommandViewModel> AllCommands(MainWindowViewModel viewModel)
        => viewModel.RibbonTabs.SelectMany(t => t.Groups).SelectMany(g => g.Commands);

    private static RibbonCommandViewModel Command(MainWindowViewModel viewModel, string id)
        => AllCommands(viewModel).Single(c => c.Id == id);

    private MainWindowViewModel Create(bool isDebugBuild)
        => new(_dialogs, _lifetime, _shell, _paths, new AppBuildInfo("1.0.0", isDebugBuild));
}
