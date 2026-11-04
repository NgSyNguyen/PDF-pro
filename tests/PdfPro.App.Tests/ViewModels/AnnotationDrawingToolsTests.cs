using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Annotating;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Overlay;
using PdfPro.Infrastructure.Settings;
using PdfPro.Operations.Annotating;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>The drawing tools of "Chú thích": strokes, shapes, lines, polygons, text boxes, stamps, colours.</summary>
public sealed class AnnotationDrawingToolsTests : IDisposable
{
    /// <summary>How close points must be after the trip to the screen and back.</summary>
    private const double Near = 0.01;

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public AnnotationDrawingToolsTests()
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
            NullLoggerFactory.Instance,
            settings: new FakeSettingsService(AppSettings.Default with { AnnotationAuthor = "Lan" }));

    public void Dispose()
    {
        _fixture.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task ARectangle_IsDrawnByDragging_ThenSelected()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        await tab.ShapesCommand.ExecuteAsync(null);
        tools.Tool.ShouldBe(AnnotationTool.Rectangle);

        tools.Press(Viewport(tab, new PdfPoint(100, 700)), clickCount: 1).ShouldBeTrue();
        tools.IsDrawing.ShouldBeTrue();
        tools.DragTo(Viewport(tab, new PdfPoint(250, 600)));
        tools.PreviewOn(Key(tab)).ShouldBeOfType<ShapeAnnotation>().Box.ShouldBe(new PdfRect(100, 600, 250, 700), Near);
        tools.Release();

        var shape = Single<ShapeAnnotation>(tab);
        shape.Shape.ShouldBe(NoteShape.Square);
        shape.Box.ShouldBe(new PdfRect(100, 600, 250, 700), Near);
        shape.Width.ShouldBe(2);
        shape.Color.ShouldBe(AnnotationToolsViewModel.DefaultDrawColor);
        shape.Author.ShouldBe("Lan");
        tools.Tool.ShouldBe(AnnotationTool.None, "back to selecting, to adjust what was drawn");
        tools.Selected.ShouldBe(new AnnotationTarget(Key(tab), shape.Id));
        tools.IsDrawing.ShouldBeFalse();
        tools.PreviewOn(Key(tab)).ShouldBeNull();
    }

    [Fact]
    public async Task AClick_GivesTheDefaultSize()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        tools.Tool = AnnotationTool.Ellipse;

        tools.Press(Viewport(tab, new PdfPoint(100, 700)), clickCount: 1);
        tools.Release();

        var ellipse = Single<ShapeAnnotation>(tab);
        ellipse.Shape.ShouldBe(NoteShape.Circle);
        ellipse.Box.Width.ShouldBe(AnnotationToolsViewModel.DefaultShapeSize.Width, 0.01);
        ellipse.Box.Height.ShouldBe(AnnotationToolsViewModel.DefaultShapeSize.Height, 0.01);
        ellipse.Box.Top.ShouldBe(700, 0.01, "it hangs from the click, as shown");
    }

    [Fact]
    public async Task ThePen_DrawsAStrokePerDrag_AndStaysOn()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        await tab.InkCommand.ExecuteAsync(null);
        tools.StrokeWidth = 3;

        tools.Press(Viewport(tab, new PdfPoint(100, 500)), clickCount: 1);
        foreach (var x in new[] { 110, 120, 130, 140 })
        {
            tools.DragTo(Viewport(tab, new PdfPoint(x, 500 + (x % 20))));
        }

        tools.Release();
        tools.Press(Viewport(tab, new PdfPoint(300, 300)), clickCount: 1);
        tools.Release();

        var strokes = tab.Workspace.Session.Plan[0].Annotations.Cast<InkAnnotation>().ToArray();
        strokes.Length.ShouldBe(2);
        strokes[0].Strokes.ShouldHaveSingleItem().Count.ShouldBe(5);
        strokes[0].Width.ShouldBe(3);
        strokes[1].Strokes.ShouldHaveSingleItem().ShouldHaveSingleItem("a dot");
        tools.Tool.ShouldBe(AnnotationTool.Ink);
        tools.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task AnArrow_PointsWhereTheDragEnds()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        tools.Tool = AnnotationTool.Arrow;

        tools.Press(Viewport(tab, new PdfPoint(100, 400)), clickCount: 1);
        tools.DragTo(Viewport(tab, new PdfPoint(300, 450)));
        tools.Release();

        var arrow = Single<LineAnnotation>(tab);
        arrow.Start.X.ShouldBe(100, 0.01);
        arrow.End.X.ShouldBe(300, 0.01);
        arrow.End.Y.ShouldBe(450, 0.01);
        arrow.StartEnding.ShouldBe(LineEnding.None);
        arrow.EndEnding.ShouldBe(LineEnding.OpenArrow);
    }

    [Fact]
    public async Task APolygon_TakesACornerPerClick_AndADoubleClickClosesIt()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        tools.Tool = AnnotationTool.Polygon;
        PdfPoint[] corners = [new(100, 100), new(200, 100), new(150, 180)];

        foreach (var corner in corners)
        {
            tools.Press(Viewport(tab, corner), clickCount: 1);
            tools.Release();
        }

        tools.IsDrawing.ShouldBeTrue("waiting for more corners");
        tools.DragTo(Viewport(tab, new PdfPoint(120, 160)));
        tools.PreviewOn(Key(tab)).ShouldBeOfType<PolyAnnotation>().Vertices.Count.ShouldBe(4, "the corners and the mouse");
        tools.Press(Viewport(tab, corners[2]), clickCount: 2);
        tools.Release();

        var polygon = Single<PolyAnnotation>(tab);
        polygon.Closed.ShouldBeTrue();
        polygon.Vertices.Count.ShouldBe(3);
        polygon.Vertices[1].X.ShouldBe(200, 0.01);
        tools.IsDrawing.ShouldBeFalse();
    }

    [Fact]
    public async Task APolygonOfTwoCorners_IsNotMade_AndEscDropsAnUnfinishedOne()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        tools.Tool = AnnotationTool.Polygon;

        tools.Press(Viewport(tab, new PdfPoint(100, 100)), clickCount: 1);
        tools.Release();
        tools.Press(Viewport(tab, new PdfPoint(200, 100)), clickCount: 1);
        tools.Release();
        tools.Press(Viewport(tab, new PdfPoint(200, 100)), clickCount: 2);
        tools.Release();
        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty();

        tools.Press(Viewport(tab, new PdfPoint(100, 100)), clickCount: 1);
        tools.Release();
        tools.Escape().ShouldBeTrue();
        tools.IsDrawing.ShouldBeFalse();
        tools.Tool.ShouldBe(AnnotationTool.Polygon, "Esc first drops the polygon, then the tool");
    }

    [Fact]
    public async Task ATextBox_AsksForItsText_AndGrowsToHoldIt()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        await tab.TextBoxCommand.ExecuteAsync(null);

        tools.Press(Viewport(tab, new PdfPoint(100, 700)), clickCount: 1);
        tools.DragTo(Viewport(tab, new PdfPoint(220, 690)));
        tools.Release();

        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty("added once its text is typed");
        tools.IsEditingContents.ShouldBeTrue();
        tools.ContentsHeader.ShouldBe(Strings.Annotation_TextBoxHeader);
        tools.ContentsDraft = "Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc";
        tools.CommitContentsCommand.Execute(null);

        var box = Single<FreeTextAnnotation>(tab);
        box.Contents.ShouldBe("Cộng hòa xã hội chủ nghĩa Việt Nam — Độc lập - Tự do - Hạnh phúc");
        box.Box.Left.ShouldBe(100, 0.01);
        box.Box.Right.ShouldBe(220, 0.01);
        box.Box.Top.ShouldBe(700, 0.01, "it grows downwards");
        var needed = PageEditorViewModel.TextHeight(box.Style, box.Contents, 120 - (2 * AnnotationDrawing.TextPadding)) + (2 * AnnotationDrawing.TextPadding);
        box.Box.Height.ShouldBe(needed, 0.01);
        box.Style.Color.ShouldBe(box.Color);
        box.Rotation.ShouldBe(0);
        tools.Selected.ShouldBe(new AnnotationTarget(Key(tab), box.Id));
    }

    [Fact]
    public async Task ATextBoxLeftEmptyOrCancelled_IsNotAdded()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;

        foreach (var keep in new[] { true, false })
        {
            tools.Tool = AnnotationTool.FreeText;
            tools.Press(Viewport(tab, new PdfPoint(100, 700)), clickCount: 1);
            tools.Release();
            tools.ContentsDraft = keep ? "   " : "bỏ đi";
            if (keep)
            {
                tools.CommitContentsCommand.Execute(null);
            }
            else
            {
                tools.CancelContentsCommand.Execute(null);
            }
        }

        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty();
        tab.Workspace.Session.History.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task AStamp_IsPlacedWithItsWordsAndTheDate()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        await tab.StampAnnotationCommand.ExecuteAsync(null);
        tools.StampText = Strings.AnnotationStamp_Seen;

        tools.Press(Viewport(tab, new PdfPoint(350, 780)), clickCount: 1).ShouldBeTrue();
        tools.Release();

        var stamp = Single<StampAnnotation>(tab);
        stamp.Text.ShouldNotBeNull().ShouldStartWith(Strings.AnnotationStamp_Seen + "\n");
        stamp.Text.Split('\n')[1].ShouldBe(DateTimeOffset.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        stamp.Box.Width.ShouldBe(AnnotationToolsViewModel.StampSize.Width, 0.01);
        stamp.Box.Height.ShouldBe(AnnotationToolsViewModel.StampSize.Height, 0.01);
        stamp.Box.Top.ShouldBe(780, 0.01);
        tools.Tool.ShouldBe(AnnotationTool.None);

        tools.Tool = AnnotationTool.Stamp;
        tools.StampWithDate = false;
        tools.Press(Viewport(tab, new PdfPoint(350, 600)), clickCount: 1);
        tab.Workspace.Session.Plan[0].Annotations.OfType<StampAnnotation>().Last().Text.ShouldBe(Strings.AnnotationStamp_Seen);
    }

    [Fact]
    public async Task ASwatch_RecoloursTheSelectedAnnotation_OrTheNextDrawings()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        var blue = RgbColor.TryParse("#1D4ED8", out var parsed) ? parsed.Value : default;

        tools.SetColorCommand.Execute("#1D4ED8");
        tools.DrawColor.ShouldBe(blue);
        tools.Tool = AnnotationTool.Rectangle;
        tools.Press(Viewport(tab, new PdfPoint(100, 700)), clickCount: 1);
        tools.Release();
        Single<ShapeAnnotation>(tab).Color.ShouldBe(blue);

        tools.SetColorCommand.Execute("#16A34A");
        Single<ShapeAnnotation>(tab).Color.ShouldBe(new RgbColor(0x16, 0xA3, 0x4A), "the new shape is selected: it changes");
        tools.DrawColor.ShouldBe(blue, "the next drawings keep their colour");
        tab.Workspace.Session.History.NextUndoDescription.ShouldBe(string.Format(CultureInfo.CurrentCulture, Strings.History_UpdateAnnotations, 1));
    }

    [Fact]
    public async Task TheToolBar_ShowsForATool_OrASelection()
    {
        await using var tab = await OpenAsync();
        var tools = tab.Annotations;
        tools.IsBarVisible.ShouldBeFalse();

        tools.Tool = AnnotationTool.Line;
        tools.IsBarVisible.ShouldBeTrue();
        tools.HasWidth.ShouldBeTrue();
        tools.Tool = AnnotationTool.Stamp;
        tools.HasWidth.ShouldBeFalse();
        tools.Tool = AnnotationTool.Polyline;
        tools.ToolText.ShouldNotBeNull().ShouldEndWith(Strings.Annotation_PolygonHint);

        // A double-click is a click (a point) and then a second click (the end).
        tools.Press(Viewport(tab, new PdfPoint(100, 100)), clickCount: 1);
        tools.Release();
        tools.Press(Viewport(tab, new PdfPoint(200, 150)), clickCount: 1);
        tools.Release();
        tools.Press(Viewport(tab, new PdfPoint(200, 150)), clickCount: 2);
        tools.Release();
        Single<PolyAnnotation>(tab).Closed.ShouldBeFalse();
        tools.Selected.ShouldNotBeNull();
        tools.Tool.ShouldBe(AnnotationTool.None);
        tools.IsBarVisible.ShouldBeTrue("a selection shows its colour, comment and delete");
        tools.EditCommentCommand.Execute(null);
        tools.IsEditingContents.ShouldBeTrue();
    }

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 700);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }

    private static T Single<T>(DocumentTabViewModel tab)
        where T : AnnotationItem
        => tab.Workspace.Session.Plan[0].Annotations.ShouldHaveSingleItem().ShouldBeOfType<T>();

    private static Guid Key(DocumentTabViewModel tab) => tab.Workspace.Pages.Pages[0].Key;

    /// <summary>The viewport point over a point of page 1 (PDF space).</summary>
    private static ViewPoint Viewport(DocumentTabViewModel tab, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(0);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(Key(tab)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }
}

/// <summary>Rectangles compared side by side with a tolerance.</summary>
internal static class PdfRectAssertions
{
    public static void ShouldBe(this PdfRect actual, PdfRect expected, double tolerance)
    {
        actual.Left.ShouldBe(expected.Left, tolerance);
        actual.Bottom.ShouldBe(expected.Bottom, tolerance);
        actual.Right.ShouldBe(expected.Right, tolerance);
        actual.Top.ShouldBe(expected.Top, tolerance);
    }
}
