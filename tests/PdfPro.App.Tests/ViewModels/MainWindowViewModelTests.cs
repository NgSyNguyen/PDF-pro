using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IAppLifetime _lifetime = Substitute.For<IAppLifetime>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly IDocumentOpener _opener = Substitute.For<IDocumentOpener>();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly AppPaths _paths = new(@"C:\data\PdfPro", @"C:\app\tools");
    private readonly RenderCache _cache;
    private readonly IDocumentTabFactory _tabFactory;

    public MainWindowViewModelTests()
    {
        _cache = new RenderCache(_settings);
        _tabFactory = new DocumentTabFactory(new FakePageImageRenderer(), _cache, new FakeThumbnailProvider(), new ImmediateUiDispatcher(), new ManualDebouncerFactory(), NullLoggerFactory.Instance);
    }

    public void Dispose() => _cache.Dispose();

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
    public void Ribbon_OnlyImplementedCommandsAreAvailable()
    {
        var commands = AllCommands(Create(isDebugBuild: true)).ToList();

        commands.Where(c => c.IsAvailable).Select(c => c.Id).ShouldBe(
            ["file.open", "file.properties", "file.close", "app.settings", "app.exit", "tools.settings", "tools.openLogFolder", "tools.throwTestException"],
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

        viewModel.Tabs.ShouldHaveSingleItem().ShouldBeSameAs(viewModel.Home);
        viewModel.SelectedTab.ShouldBeSameAs(viewModel.Home);
        viewModel.HasActiveDocument.ShouldBeFalse();
        viewModel.Home.CanClose.ShouldBeFalse();
        viewModel.Home.HasNoRecentFiles.ShouldBeTrue();
        viewModel.Home.OpenFileCommand.ShouldBeSameAs(viewModel.OpenCommand);
        viewModel.CloseActiveDocumentCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenFiles_AddsSelectsAndRemembersTheDocument()
    {
        var document = FakePdfDocument.WithPages(3);
        _opener.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(document);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf"]);

        var tab = viewModel.ActiveDocument.ShouldNotBeNull();
        tab.Document.ShouldBeSameAs(document);
        tab.Title.ShouldBe("a.pdf");
        tab.ToolTip.ShouldBe(@"C:\docs\a.pdf");
        viewModel.Tabs.Count.ShouldBe(2);
        viewModel.CloseActiveDocumentCommand.CanExecute(null).ShouldBeTrue();
        _settings.Current.RecentFiles.ShouldBe([@"C:\docs\a.pdf"]);
        viewModel.Home.RecentFiles.Select(r => r.FileName).ShouldBe(["a.pdf"]);
    }

    [Fact]
    public async Task OpenFiles_AnAlreadyOpenFileIsSelectedNotReopened()
    {
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(1));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);

        await viewModel.OpenFilesAsync([@"C:\DOCS\A.PDF"]);

        viewModel.Documents.Count().ShouldBe(2);
        viewModel.ActiveDocument!.Title.ShouldBe("a.pdf");
        await _opener.Received(1).OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>());
        _settings.Current.RecentFiles.ShouldBe([@"C:\docs\b.pdf", @"C:\docs\a.pdf"]);
    }

    [Fact]
    public async Task OpenFiles_CancelledOrFailedOpen_AddsNoTab()
    {
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((IPdfDocument?)null);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([@"C:\docs\locked.pdf"]);

        viewModel.Documents.ShouldBeEmpty();
        viewModel.SelectedTab.ShouldBeSameAs(viewModel.Home);
    }

    [Fact]
    public async Task OpenFiles_FromDrop_SkipsNonPdfFilesAndSaysSo()
    {
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(1));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([@"C:\docs\notes.txt", @"C:\docs\scan.PDF"], pdfOnly: true);

        viewModel.Documents.Select(d => d.Title).ShouldBe(["scan.PDF"]);
        await _messages.Received(1).ShowInfoAsync(Strings.Open_Error_Title, Strings.Open_NotPdf);
    }

    [Fact]
    public async Task PropertiesCommand_ShowsTheActiveDocumentsProperties()
    {
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(2));
        var viewModel = Create(isDebugBuild: false);
        var properties = Command(viewModel, "file.properties").Command;
        properties.CanExecute(null).ShouldBeFalse("the home tab has no properties");

        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf"]);
        properties.CanExecute(null).ShouldBeTrue();
        properties.Execute(null);

        _dialogs.Received(1).ShowDocumentProperties(viewModel.ActiveDocument!);
    }

    [Fact]
    public async Task OpenCommand_UsesTheFileDialog()
    {
        _fileDialogs.PickPdfFiles().Returns([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(1));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenCommand.ExecuteAsync(null);

        viewModel.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task CloseTab_DisposesTheDocumentAndSelectsTheNeighbour()
    {
        var first = FakePdfDocument.WithPages(1);
        var second = FakePdfDocument.WithPages(1);
        _opener.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(first);
        _opener.OpenAsync(@"C:\docs\b.pdf", Arg.Any<CancellationToken>()).Returns(second);
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);

        await viewModel.CloseActiveDocumentCommand.ExecuteAsync(null);

        second.IsDisposed.ShouldBeTrue();
        viewModel.ActiveDocument!.Document.ShouldBeSameAs(first);
        await viewModel.CloseTabCommand.ExecuteAsync(viewModel.ActiveDocument);
        first.IsDisposed.ShouldBeTrue();
        viewModel.SelectedTab.ShouldBeSameAs(viewModel.Home);
        await viewModel.CloseTabCommand.ExecuteAsync(viewModel.Home); // the home tab cannot be closed
        viewModel.Tabs.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task RecentFiles_MissingOnesAreRemoved()
    {
        await _settings.UpdateAsync(s => s with { RecentFiles = [typeof(MainWindowViewModelTests).Assembly.Location, @"C:\no\such\file.pdf"] }, TestContext.Current.CancellationToken);
        var viewModel = Create(isDebugBuild: false);
        viewModel.Home.RecentFiles.Count.ShouldBe(2);

        await viewModel.RemoveMissingRecentFilesAsync();

        viewModel.Home.RecentFiles.ShouldHaveSingleItem().Path.ShouldBe(typeof(MainWindowViewModelTests).Assembly.Location);
    }

    [Fact]
    public async Task CloseAllDocuments_ClosesEverything()
    {
        _opener.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => FakePdfDocument.WithPages(1));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);
        var documents = viewModel.Documents.Select(d => (FakePdfDocument)d.Document).ToList();

        viewModel.CloseAllDocuments();

        documents.ShouldAllBe(d => d.IsDisposed);
        viewModel.Documents.ShouldBeEmpty();
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
    public void StatusBar_StartsReady()
    {
        var status = Create(isDebugBuild: false).StatusBar;

        status.TaskText.ShouldBe(Strings.Status_Ready);
        status.IsTaskRunning.ShouldBeFalse();
    }

    private static IEnumerable<RibbonCommandViewModel> AllCommands(MainWindowViewModel viewModel)
        => viewModel.RibbonTabs.SelectMany(t => t.Groups).SelectMany(g => g.Commands);

    private static RibbonCommandViewModel Command(MainWindowViewModel viewModel, string id)
        => AllCommands(viewModel).Single(c => c.Id == id);

    private MainWindowViewModel Create(bool isDebugBuild)
        => new(
            _dialogs,
            _lifetime,
            _shell,
            _paths,
            new AppBuildInfo("1.0.0", isDebugBuild),
            _opener,
            _tabFactory,
            _settings,
            _fileDialogs,
            _messages,
            NullLogger<MainWindowViewModel>.Instance);
}
