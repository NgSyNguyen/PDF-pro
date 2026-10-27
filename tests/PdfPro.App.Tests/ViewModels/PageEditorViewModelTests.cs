using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Organizing;
using PdfPro.Operations.Stamping;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The page editor: making, moving, pasting and applying objects; its place in the document tab.</summary>
public sealed class PageEditorViewModelTests : IDisposable
{
    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public PageEditorViewModelTests()
        => _factory = new DocumentTabFactory(
            new FakePageImageRenderer { AutoComplete = true },
            _cache,
            new FakeThumbnailProvider(),
            new ImmediateUiDispatcher(),
            new ManualDebouncerFactory(),
            TestOperations.Catalog(),
            Substitute.For<IMessageService>(),
            Substitute.For<IPageDialogService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IPdfDocumentLoader>(),
            NullLoggerFactory.Instance);

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task AClick_MakesADefaultSizedShape_ADrag_ItsOwnSize()
    {
        await using var tab = await OpenAsync(3);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 0);

        var clicked = (ShapeOverlay)editor.NewItem(OverlayTool.Rectangle, geometry, new ViewPoint(100, 100), new ViewPoint(101, 100))!;
        var dragged = (ShapeOverlay)editor.NewItem(OverlayTool.Ellipse, geometry, new ViewPoint(300, 400), new ViewPoint(200, 340))!;

        clicked.Box.Size.ShouldBe(PageEditorViewModel.DefaultShapeSize);
        Shown(geometry, clicked.Box.Center).ShouldBe(new ViewPoint(160, 140), "the click is the top-left corner");
        dragged.Kind.ShouldBe(ShapeKind.Ellipse);
        dragged.Box.Size.ShouldBe(new PdfSize(100, 60));
        Shown(geometry, dragged.Box.Center).ShouldBe(new ViewPoint(250, 370));
        editor.NewItem(OverlayTool.Select, geometry, default, default).ShouldBeNull();
        key.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task ObjectsAreInThePagesPdfSpace_EvenOnTurnedPages()
    {
        var document = FakePdfDocument.WithPages(1);
        document.Geometry = _ => new PageGeometry(new PdfRect(50, 100, 450, 700), 90);
        await using var tab = await OpenAsync(document);
        var (editor, _, geometry) = await EditorOnPageAsync(tab, 0);

        var line = (LineOverlay)editor.NewItem(OverlayTool.Arrow, geometry, new ViewPoint(10, 20), new ViewPoint(110, 20))!;

        line.ArrowAtEnd.ShouldBeTrue();
        line.Start.ShouldBe(new PdfPoint(70, 110), "shown (10, 20) on a page turned 90° with its box at (50, 100)");
        Shown(geometry, line.End).ShouldBe(new ViewPoint(110, 20));
    }

    [Fact]
    public async Task EditsHaveTheirOwnUndo()
    {
        await using var tab = await OpenAsync(2);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 0);
        var added = editor.Add(key, Shape(geometry, 100, 100));

        editor.Nudge(10, 0).ShouldBeTrue();
        Shown(geometry, ((ShapeOverlay)added.Item).Box.Center).X.ShouldBe(110, 1e-9);
        editor.DeleteCommand.Execute(null);
        editor.Objects.ShouldBeEmpty();

        editor.UndoCommand.Execute(null);
        Shown(geometry, ((ShapeOverlay)editor.Objects.Single().Item).Box.Center).X.ShouldBe(110, 1e-9);
        editor.UndoCommand.Execute(null);
        Shown(geometry, ((ShapeOverlay)editor.Objects.Single().Item).Box.Center).X.ShouldBe(100, 1e-9);
        editor.UndoCommand.Execute(null);
        editor.Objects.ShouldBeEmpty();
        editor.CanUndo.ShouldBeFalse();
        editor.RedoCommand.Execute(null);
        editor.Objects.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Paste_OnTheSamePage_StepsAside_OnAnother_KeepsTheShownPlace()
    {
        var document = FakePdfDocument.WithPages(2);
        document.Geometry = i => i == 0 ? new PageGeometry(new PdfRect(0, 0, 595, 842), 0) : new PageGeometry(new PdfRect(20, 30, 615, 872), 90);
        await using var tab = await OpenAsync(document);
        var (editor, first, geometry) = await EditorOnPageAsync(tab, 0);
        var (_, second, other) = await EditorOnPageAsync(tab, 1);
        editor.Add(first, Shape(geometry, 100, 100));
        editor.CopyCommand.Execute(null);

        editor.PasteOn(first);
        editor.PasteOn(second);

        editor.Objects.Count.ShouldBe(3);
        Shown(geometry, ((ShapeOverlay)editor.Objects[1].Item).Box.Center).ShouldBe(new ViewPoint(110, 110));
        editor.Objects[2].PageKey.ShouldBe(second);
        Shown(other, ((ShapeOverlay)editor.Objects[2].Item).Box.Center).X.ShouldBe(100, 1e-9);
        editor.Selection.Count.ShouldBe(1, "what was pasted is selected");
    }

    [Fact]
    public async Task Parameters_UseThePagesCurrentPlaces_AndSkipEmptyText()
    {
        await using var tab = await OpenAsync(3);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 2);
        editor.Add(key, Shape(geometry, 100, 100));
        editor.Add(key, new TextOverlay(0, new OverlayBox(new PdfPoint(100, 100), new PdfSize(100, 20)), "  ", TextStyle.Default));

        tab.Apply(new MovePagesOperation(Substitute.For<Core.Organizing.IPlanMaterializer>(), Substitute.For<IPdfDocumentLoader>()), new MovePagesParams("3", 1));

        var parameters = editor.BuildParameters().ShouldNotBeNull();
        parameters.Items.ShouldHaveSingleItem().PageIndex.ShouldBe(0, "page 3 moved to the front");
    }

    [Fact]
    public async Task ApplyingAsksTheWindowForOneContentChange_AndEmptiesTheEditorWhenMade()
    {
        await using var tab = await OpenAsync(2);
        tab.AddTextCommand.Execute(null);
        tab.Editor.IsActive.ShouldBeTrue();
        tab.Editor.Tool.ShouldBe(OverlayTool.Text);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 1);
        editor.Add(key, Shape(geometry, 100, 100));
        editor.Add(key, Shape(geometry, 300, 300));
        ContentChangeRequest? request = null;
        tab.ContentChangeRequested += (_, r) => request = r;

        tab.ChangePageSizeCommand.CanExecute(null).ShouldBeFalse("objects wait in the editor");
        editor.ApplyCommand.Execute(null);

        request.ShouldNotBeNull().Operation.ShouldBeOfType<OverlayOperation>();
        ((OverlayParams)request.Parameters).Items.Select(i => i.PageIndex).ShouldBe([1, 1]);
        request.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_Overlay, 2));
        request.Completed.ShouldNotBeNull()(false);
        editor.Objects.Count.ShouldBe(2, "a failed change keeps the objects");
        request.Completed!(true);
        editor.Objects.ShouldBeEmpty();
        editor.IsActive.ShouldBeTrue("the editor stays open after Áp dụng");
    }

    [Fact]
    public async Task Done_AppliesThenCloses_AndTheOrganizerAppliesToo()
    {
        await using var tab = await OpenAsync(2);
        tab.AddShapeCommand.Execute(null);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 0);
        editor.Add(key, Shape(geometry, 100, 100));
        var requests = new List<ContentChangeRequest>();
        tab.ContentChangeRequested += (_, r) => requests.Add(r);

        tab.FinishEditingCommand.Execute(null);
        requests.Single().Completed!(true);

        editor.IsActive.ShouldBeFalse();
        editor.Objects.ShouldBeEmpty();

        tab.AddShapeCommand.Execute(null);
        editor.Add(key, Shape(geometry, 100, 100));
        tab.IsOrganizing = true;
        requests.Count.ShouldBe(2, "switching to the organizer applies what was placed");
    }

    [Fact]
    public async Task Undo_UndoesEditorEditsFirst_ThenTheDocument()
    {
        await using var tab = await OpenAsync(2);
        tab.RotateRightCommand.Execute(null);
        tab.AddShapeCommand.Execute(null);
        var (editor, key, geometry) = await EditorOnPageAsync(tab, 0);
        editor.Add(key, Shape(geometry, 100, 100));

        tab.UndoCommand.Execute(null);

        editor.Objects.ShouldBeEmpty();
        tab.Workspace.Session.Plan[0].RotationDelta.ShouldBe(90, "the document is untouched");
        tab.UndoCommand.Execute(null);
        tab.Workspace.Session.Plan[0].RotationDelta.ShouldBe(0);
    }

    private static ShapeOverlay Shape(PageGeometry geometry, double shownX, double shownY)
        => new(0, new OverlayBox(new PageCoordinateMapper(geometry, 1).ScreenToPdf(new ViewPoint(shownX, shownY)), new PdfSize(40, 20)), ShapeKind.Rectangle, new StrokeStyle(RgbColor.Red, 2));

    private static ViewPoint Shown(PageGeometry geometry, PdfPoint point) => new PageCoordinateMapper(geometry, 1).PdfToScreen(point);

    private static async Task<(PageEditorViewModel Editor, Guid Key, PageGeometry Geometry)> EditorOnPageAsync(DocumentTabViewModel tab, int index)
    {
        var key = tab.Workspace.Pages.Pages[index].Key;
        var geometry = await tab.Editor.GetGeometryAsync(key);
        return (tab.Editor, key, geometry.ShouldNotBeNull());
    }

    private Task<DocumentTabViewModel> OpenAsync(int pages) => OpenAsync(FakePdfDocument.WithPages(pages));

    private async Task<DocumentTabViewModel> OpenAsync(FakePdfDocument document)
        => _factory.Create(await _fixture.CreateAsync(document));
}
