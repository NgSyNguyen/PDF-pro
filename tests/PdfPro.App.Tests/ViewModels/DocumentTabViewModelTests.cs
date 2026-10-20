using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Sessions;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Organizing;

namespace PdfPro.App.Tests.ViewModels;

public sealed class DocumentTabViewModelTests : IDisposable
{
    private static readonly ViewSize Viewport = new(1000, 700);

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly IMessageService _messages = Substitute.For<IMessageService>();
    private readonly DocumentTabFactory _factory;

    public DocumentTabViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            _messages,
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task NewTab_IsCleanAndNamedAfterTheFile()
    {
        await using var tab = await CreateAsync(3);

        tab.Title.ShouldBe("a.pdf");
        tab.ToolTip.ShouldBe(@"C:\docs\a.pdf");
        tab.IsDirty.ShouldBeFalse();
        tab.UndoCommand.CanExecute(null).ShouldBeFalse();
        tab.UndoToolTip.ShouldBe(Strings.Cmd_Undo);
        tab.HistoryEntries.ShouldBeEmpty();
        tab.IsSameFile(@"C:\DOCS\A.PDF").ShouldBeTrue();
    }

    [Fact]
    public async Task RotateRight_TurnsTheCurrentPageAndCanBeUndone()
    {
        await using var tab = await CreateAsync(3);
        tab.Viewer.NavigateTo(1);

        tab.RotateRightCommand.Execute(null);

        tab.Workspace.Session.Plan[1].RotationDelta.ShouldBe(90);
        tab.Viewer.Pages[1].QuarterTurns.ShouldBe(1);
        tab.Viewer.Document.PageSizes[1].ShouldBe(new PdfSize(842, 595));
        tab.Thumbnails.Items[1].Angle.ShouldBe(90);
        tab.Title.ShouldBe("a.pdf *");
        tab.UndoToolTip.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.Cmd_UndoWith, string.Format(CultureInfo.CurrentCulture, Strings.History_Rotate, 1)));

        tab.UndoCommand.Execute(null);

        tab.Workspace.Session.Plan[1].RotationDelta.ShouldBe(0);
        tab.Viewer.Pages[1].QuarterTurns.ShouldBe(0);
        tab.Title.ShouldBe("a.pdf");
        tab.RedoCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task RotateLeft_TurnsCounterClockwise()
    {
        await using var tab = await CreateAsync(2);

        tab.RotateLeftCommand.Execute(null);

        tab.Workspace.Session.Plan[0].RotationDelta.ShouldBe(270);
    }

    [Fact]
    public async Task Delete_RemovesTheCurrentPageAndKeepsTheOthersViews()
    {
        await using var tab = await CreateAsync(3);
        var lastPage = tab.Viewer.Pages[2];
        var lastThumbnail = tab.Thumbnails.Items[2];
        tab.Viewer.NavigateTo(1);

        tab.DeletePagesCommand.Execute(null);

        tab.Viewer.PageCount.ShouldBe(2);
        tab.Viewer.Pages[1].ShouldBeSameAs(lastPage);
        lastPage.Number.ShouldBe(2);
        tab.Thumbnails.Items[1].ShouldBeSameAs(lastThumbnail);
        lastThumbnail.Number.ShouldBe(2);
        tab.Viewer.CurrentPageIndex.ShouldBeInRange(0, 1);
    }

    [Fact]
    public async Task Delete_IsDisabledForTheLastPage()
    {
        await using var tab = await CreateAsync(1);

        tab.DeletePagesCommand.CanExecute(null).ShouldBeFalse();
        tab.RotateRightCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Duplicate_AddsACopyAfterTheCurrentPage()
    {
        await using var tab = await CreateAsync(2);

        tab.DuplicatePagesCommand.Execute(null);

        tab.Viewer.PageCount.ShouldBe(3);
        tab.Workspace.Session.Plan.Select(p => p.SourcePageIndex).ShouldBe([0, 0, 1]);
    }

    [Fact]
    public async Task History_ListsRedoThenUndoStepsAndJumps()
    {
        await using var tab = await CreateAsync(3);
        tab.RotateRightCommand.Execute(null);
        tab.DuplicatePagesCommand.Execute(null);
        tab.RotateLeftCommand.Execute(null);
        tab.UndoCommand.Execute(null);

        tab.HistoryEntries.Select(e => (e.IsRedo, e.Steps)).ShouldBe([(true, 1), (false, 1), (false, 2)]);
        tab.HistoryEntries[2].Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_Rotate, 1));

        tab.GoToHistoryEntryCommand.Execute(tab.HistoryEntries[2]);

        tab.Workspace.Session.History.UndoCount.ShouldBe(0);
        tab.IsDirty.ShouldBeFalse();
        tab.GoToHistoryEntryCommand.Execute(tab.HistoryEntries[0]);
        tab.Workspace.Session.History.UndoCount.ShouldBe(3);
    }

    [Fact]
    public async Task Edits_AreWrittenToTheSessionManifest()
    {
        await using var tab = await CreateAsync(3);
        tab.RotateRightCommand.Execute(null);

        await tab.Workspace.FlushManifestAsync();

        var folder = Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldHaveSingleItem();
        var manifest = SessionManifestSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(folder, SessionStore.ManifestFileName), TestContext.Current.CancellationToken));
        manifest.Undo.ShouldHaveSingleItem().Operation.ShouldBe(RotatePagesOperation.OperationId);
        manifest.State.Plan[0].Rotation.ShouldBe(90);
        manifest.OriginalPath.ShouldBe(@"C:\docs\a.pdf");
        manifest.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task Closing_RemovesTheSessionFolder()
    {
        var tab = await CreateAsync(2);
        var document = (FakePdfDocument)tab.Document;

        await tab.DisposeAsync();

        Directory.GetDirectories(_fixture.Store.RootDirectory).ShouldBeEmpty();
        document.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EncryptedDocument_CanBeViewedButNotEdited()
    {
        var document = FakePdfDocument.WithPages(3);
        document.Features = FakePdfDocument.PlainFeatures with { SecurityHandlerRevision = 6 };
        await using var tab = _factory.Create(await _fixture.CreateAsync(document));

        tab.IsReadOnly.ShouldBeTrue();
        tab.RotateRightCommand.CanExecute(null).ShouldBeFalse();
        tab.DuplicatePagesCommand.CanExecute(null).ShouldBeFalse();
        tab.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("1", 90)).ShouldBeFalse();
        tab.Workspace.Session.History.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidParameters_AreExplainedNotThrown()
    {
        await using var tab = await CreateAsync(2);

        tab.Apply(TestOperations.Catalog().Get<RotatePagesOperation>(), new RotatePagesParams("9", 90)).ShouldBeFalse();

        await _messages.Received(1).ShowErrorAsync(Strings.Edit_Error_Title, Strings.Edit_Error_Invalid);
        tab.IsDirty.ShouldBeFalse();
    }

    private async Task<DocumentTabViewModel> CreateAsync(int pages)
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(pages)));
        tab.Viewer.ViewportSize = Viewport;
        return tab;
    }
}
