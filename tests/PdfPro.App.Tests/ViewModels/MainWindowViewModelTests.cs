using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Hosting;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Printing;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

public sealed partial class MainWindowViewModelTests : IDisposable
{
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IAppLifetime _lifetime = Substitute.For<IAppLifetime>();
    private readonly IShellLauncher _shell = Substitute.For<IShellLauncher>();
    private readonly IDocumentWorkspaceService _workspaces = Substitute.For<IDocumentWorkspaceService>();
    private readonly WorkspaceFixture _fixture = new();
    private readonly IFileDialogService _fileDialogs = Substitute.For<IFileDialogService>();
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly IPrintDialogService _printDialogs = Substitute.For<IPrintDialogService>();
    private readonly IDocumentPrinter _printer = Substitute.For<IDocumentPrinter>();
    private readonly IDocumentSaver _saver = Substitute.For<IDocumentSaver>();
    private readonly IRecoveryService _recovery = Substitute.For<IRecoveryService>();
    private readonly IContentChanger _contentChanger = Substitute.For<IContentChanger>();
    private readonly IPageDialogService _pageDialogs = Substitute.For<IPageDialogService>();
    private readonly IMergeService _merge = Substitute.For<IMergeService>();
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly AppPaths _paths = new(@"C:\data\PdfPro", @"C:\app\tools");
    private readonly RenderCache _cache;
    private readonly IDocumentTabFactory _tabFactory;

    public MainWindowViewModelTests()
    {
        _cache = new RenderCache(_settings);
        _tabFactory = new DocumentTabFactory(
            new FakePageImageRenderer(),
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            _pageDialogs,
            _fileDialogs,
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

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
            ["file.open", "file.save", "file.saveAs", "file.print", "file.properties", "file.close", "app.settings", "app.exit", "tools.settings", "tools.openLogFolder", "tools.throwTestException",
                "edit.undo", "edit.redo", "organize.mode", "organize.rotateLeft", "organize.rotateRight", "organize.delete", "organize.duplicate",
                "organize.insertBlank", "organize.insertFromFile", "organize.extract", "organize.crop", "organize.pageSize",
                "organize.split", "organize.markSplit", "organize.clearSplitMarks",
                "organize.merge", "organize.interleave", "layout.nup", "layout.booklet", "layout.tile", "edit.addText", "edit.addShape"],
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
    public async Task ExitCommand_ShutsDownTheApp()
    {
        _lifetime.ShutdownAsync().Returns(true);

        Command(Create(isDebugBuild: false), "app.exit").Command.Execute(null);

        await _lifetime.Received(1).ShutdownAsync();
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
        _workspaces.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(document, @"C:\docs\a.pdf"));
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
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);

        await viewModel.OpenFilesAsync([@"C:\DOCS\A.PDF"]);

        viewModel.Documents.Count().ShouldBe(2);
        viewModel.ActiveDocument!.Title.ShouldBe("a.pdf");
        await _workspaces.Received(1).OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>());
        _settings.Current.RecentFiles.ShouldBe([@"C:\docs\b.pdf", @"C:\docs\a.pdf"]);
    }

    [Fact]
    public async Task OpenFiles_CancelledOrFailedOpen_AddsNoTab()
    {
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((DocumentWorkspace?)null);
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([@"C:\docs\locked.pdf"]);

        viewModel.Documents.ShouldBeEmpty();
        viewModel.SelectedTab.ShouldBeSameAs(viewModel.Home);
    }

    [Fact]
    public async Task OpenFiles_FromDrop_SkipsNonPdfFilesAndSaysSo()
    {
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenFilesAsync([@"C:\docs\notes.txt", @"C:\docs\scan.PDF"], pdfOnly: true);

        viewModel.Documents.Select(d => d.Title).ShouldBe(["scan.PDF"]);
        await _messages.Received(1).ShowInfoAsync(Strings.Open_Error_Title, Strings.Open_NotPdf);
    }

    [Fact]
    public async Task Print_SendsTheChosenPagesAndReportsTheResult()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(5));
        var job = Job([1, 2, 3]);
        _printDialogs.ShowPrintDialog(5, 0).Returns(job);
        var printing = new TaskCompletionSource<PrintOutcome>();
        _printer.PrintAsync(Arg.Any<VirtualDocument>(), job, "a.pdf", Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>()).Returns(printing.Task);

        var command = viewModel.PrintCommand.ExecuteAsync(null);

        viewModel.StatusBar.IsTaskRunning.ShouldBeTrue();
        viewModel.PrintCommand.CanExecute(null).ShouldBeFalse("one print job at a time");
        printing.SetResult(new PrintOutcome(Cancelled: false, FailedPages: 0));
        await command;

        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
        viewModel.StatusBar.TaskText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Print_Done, 3));
        viewModel.PrintCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Print_ReportsCancelledAndBlankPages()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(5));
        _printDialogs.ShowPrintDialog(5, 0).Returns(Job([0, 1]));
        AnyPrint().Returns(
            Task.FromResult(new PrintOutcome(false, 1)),
            Task.FromResult(new PrintOutcome(true, 0)));

        await viewModel.PrintCommand.ExecuteAsync(null);
        viewModel.StatusBar.TaskText.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Print_DoneWithFailures, 2, 1));

        await viewModel.PrintCommand.ExecuteAsync(null);
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Print_Cancelled);
    }

    [Fact]
    public async Task Print_CancelButton_CancelsTheJob()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(2));
        _printDialogs.ShowPrintDialog(2, 0).Returns(Job([0, 1]));
        var token = CancellationToken.None;
        var printing = new TaskCompletionSource<PrintOutcome>();
        AnyPrint().Returns(call =>
        {
            token = call.ArgAt<CancellationToken>(4);
            return printing.Task;
        });

        var command = viewModel.PrintCommand.ExecuteAsync(null);
        viewModel.StatusBar.CancelTaskCommand.Execute(null);

        token.IsCancellationRequested.ShouldBeTrue();
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Status_Cancelling);
        printing.SetResult(new PrintOutcome(Cancelled: true, FailedPages: 0));
        await command;
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Print_Cancelled);
    }

    [Fact]
    public async Task Print_Failure_IsExplained()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(2));
        _printDialogs.ShowPrintDialog(2, 0).Returns(Job([0]));
        AnyPrint()
            .Returns(Task.FromException<PrintOutcome>(new PrintFailedException("offline")));

        await viewModel.PrintCommand.ExecuteAsync(null);

        viewModel.StatusBar.TaskText.ShouldBe(Strings.Print_FailedStatus);
        viewModel.StatusBar.IsTaskRunning.ShouldBeFalse();
        await _messages.Received(1).ShowErrorAsync(Strings.Print_Error_Title, Strings.Print_FailedMessage);
    }

    [Fact]
    public async Task Print_CancelledDialog_PrintsNothing()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(2));
        _printDialogs.ShowPrintDialog(2, 0).Returns((PrintJob?)null);

        await viewModel.PrintCommand.ExecuteAsync(null);

        await _printer.DidNotReceive().PrintAsync(Arg.Any<VirtualDocument>(), Arg.Any<PrintJob>(), Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>());
        viewModel.StatusBar.TaskText.ShouldBe(Strings.Status_Ready);
    }

    [Fact]
    public async Task Print_DocumentThatForbidsPrinting_IsRefused()
    {
        var document = FakePdfDocument.WithPages(2);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 4, Permissions = 0xFFFFFFFB }; // bit 3 cleared
        var viewModel = await CreateWithDocumentAsync(document);

        await viewModel.PrintCommand.ExecuteAsync(null);

        _printDialogs.DidNotReceiveWithAnyArgs().ShowPrintDialog(default, default);
        await _messages.Received(1).ShowInfoAsync(Strings.Print_Error_Title, Strings.Print_NotAllowed);
    }

    [Fact]
    public async Task ClosingTheDocumentBeingPrinted_CancelsAndWaitsForTheJob()
    {
        var document = FakePdfDocument.WithPages(3);
        var viewModel = await CreateWithDocumentAsync(document);
        _printDialogs.ShowPrintDialog(3, 0).Returns(Job([0, 1, 2]));
        var printing = new TaskCompletionSource<PrintOutcome>();
        var token = CancellationToken.None;
        AnyPrint().Returns(call =>
        {
            token = call.ArgAt<CancellationToken>(4);
            return printing.Task;
        });
        var command = viewModel.PrintCommand.ExecuteAsync(null);

        var closing = viewModel.CloseTabCommand.ExecuteAsync(viewModel.ActiveDocument);

        token.IsCancellationRequested.ShouldBeTrue();
        document.IsDisposed.ShouldBeFalse("the print job still uses the document");
        printing.SetResult(new PrintOutcome(Cancelled: true, FailedPages: 0));
        await closing;
        await command;
        document.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public void Print_WithoutADocument_IsDisabled()
        => Create(isDebugBuild: false).PrintCommand.CanExecute(null).ShouldBeFalse();

    [Fact]
    public async Task PropertiesCommand_ShowsTheActiveDocumentsProperties()
    {
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(2), call.ArgAt<string>(0)));
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
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);

        await viewModel.OpenCommand.ExecuteAsync(null);

        viewModel.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task CloseTab_DisposesTheDocumentAndSelectsTheNeighbour()
    {
        var first = FakePdfDocument.WithPages(1);
        var second = FakePdfDocument.WithPages(1);
        _workspaces.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(first, @"C:\docs\a.pdf"));
        _workspaces.OpenAsync(@"C:\docs\b.pdf", Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(second, @"C:\docs\b.pdf"));
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
        _workspaces.OpenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => _fixture.OpenAsync(FakePdfDocument.WithPages(1), call.ArgAt<string>(0)));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf", @"C:\docs\b.pdf"]);
        var documents = viewModel.Documents.Select(d => (FakePdfDocument)d.Document).ToList();

        viewModel.CloseAllDocuments();

        documents.ShouldAllBe(d => d.IsDisposed);
        viewModel.Documents.ShouldBeEmpty();
    }

    [Fact]
    public async Task PageAndHistoryCommands_ActOnTheActiveDocument()
    {
        var viewModel = await CreateWithDocumentAsync(FakePdfDocument.WithPages(3));
        var undo = Command(viewModel, "edit.undo");
        undo.Command.CanExecute(null).ShouldBeFalse();

        Command(viewModel, "organize.rotateRight").Command.Execute(null);

        var document = viewModel.ActiveDocument.ShouldNotBeNull();
        document.Title.ShouldBe("a.pdf *");
        undo.Command.CanExecute(null).ShouldBeTrue();
        undo.ToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, string.Format(CultureInfo.CurrentCulture, Strings.History_Rotate, 1)));

        viewModel.UndoCommand.Execute(null);

        document.IsDirty.ShouldBeFalse();
        viewModel.RedoCommand.CanExecute(null).ShouldBeTrue();
        undo.ToolTip.ShouldBe(Strings.Cmd_Undo);
    }

    [Fact]
    public async Task DocumentCommands_FollowTheSelectedTab()
    {
        var viewModel = Create(isDebugBuild: false);
        viewModel.RotateRightCommand.CanExecute(null).ShouldBeFalse();
        viewModel.UndoCommand.CanExecute(null).ShouldBeFalse();
        Command(viewModel, "organize.delete").Command.CanExecute(null).ShouldBeFalse();

        _workspaces.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(FakePdfDocument.WithPages(2), @"C:\docs\a.pdf"));
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf"]);
        viewModel.RotateRightCommand.CanExecute(null).ShouldBeTrue();
        viewModel.DeletePagesCommand.CanExecute(null).ShouldBeTrue();

        viewModel.SelectedTab = viewModel.Home;

        viewModel.RotateRightCommand.CanExecute(null).ShouldBeFalse();
        viewModel.DuplicatePagesCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task CloseAllDocuments_OnTheUiThread_NeverWaitsForTheUiThread()
    {
        // Application exit blocks the UI thread until the documents are closed; closing must not need it.
        var document = FakePdfDocument.WithPages(1);
        document.DisposeDelay = TimeSpan.FromMilliseconds(50);
        var viewModel = await CreateWithDocumentAsync(document);
        var sessionFolder = Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldHaveSingleItem();

        var elapsed = Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            viewModel.CloseAllDocuments();
            return watch.Elapsed;
        });

        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        document.IsDisposed.ShouldBeTrue();
        Directory.Exists(sessionFolder).ShouldBeFalse("the session folder is removed when the document closes");
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

    /// <summary>Any call to the printer, for setting up its result.</summary>
    private Task<PrintOutcome> AnyPrint()
        => _printer.PrintAsync(Arg.Any<VirtualDocument>(), Arg.Any<PrintJob>(), Arg.Any<string>(), Arg.Any<IProgress<int>?>(), Arg.Any<CancellationToken>());

    private static PrintJob Job(IReadOnlyList<int> pages)
        => new(string.Empty, "Test printer", [], new PrintSheet(new ViewSize(816, 1056), new ViewRect(0, 0, 816, 1056), 300), pages);

    private async Task<MainWindowViewModel> CreateWithDocumentAsync(FakePdfDocument document)
    {
        _workspaces.OpenAsync(@"C:\docs\a.pdf", Arg.Any<CancellationToken>()).Returns(_ => _fixture.OpenAsync(document, @"C:\docs\a.pdf"));
        var viewModel = Create(isDebugBuild: false);
        await viewModel.OpenFilesAsync([@"C:\docs\a.pdf"]);
        return viewModel;
    }

    private MainWindowViewModel Create(bool isDebugBuild)
        => new(
            _dialogs,
            _lifetime,
            _shell,
            _paths,
            new AppBuildInfo("1.0.0", isDebugBuild),
            _workspaces,
            _tabFactory,
            _settings,
            _fileDialogs,
            _messages,
            _printDialogs,
            _printer,
            _saver,
            _recovery,
            _contentChanger,
            _pageDialogs,
            _merge,
            NullLogger<MainWindowViewModel>.Instance);
}
