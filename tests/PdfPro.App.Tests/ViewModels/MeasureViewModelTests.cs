using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PdfPro.App.Services;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;
using PdfPro.Core.Measuring;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.ViewModels;

/// <summary>"Đo": distances, lengths and areas clicked on the viewer, and saved on the pages as comments.</summary>
public sealed class MeasureViewModelTests : IDisposable
{
    private const double Cm = 720 / 25.4;

    private readonly WorkspaceFixture _fixture = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly DocumentTabFactory _factory;

    public MeasureViewModelTests()
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
    public async Task TheTool_TakesTheViewer_AndGivesItBackToTheOtherModes()
    {
        var tab = await OpenAsync();
        tab.ToggleRedactionCommand.Execute(null);

        tab.ToggleMeasureCommand.Execute(null);

        tab.IsMeasuring.ShouldBeTrue();
        tab.Redaction.IsActive.ShouldBeFalse();
        tab.Forms.IsActive.ShouldBeFalse("the form's inputs give the mouse to the tool");
        tab.Annotations.Tool = PdfPro.App.ViewModels.Annotating.AnnotationTool.Rectangle;
        tab.IsMeasuring.ShouldBeFalse("an annotation tool takes the viewer back");
    }

    [Fact]
    public async Task TwoClicks_MeasureADistance_ShiftKeepingIt45Degrees()
    {
        var tab = await MeasuringAsync();
        var measure = tab.Measure;
        measure.Unit = LengthUnit.Centimetre;

        measure.Press(Viewport(tab, 0, new PdfPoint(100, 500)), 1, snap: false).ShouldBeTrue();
        measure.IsMeasuring.ShouldBeTrue();
        measure.MoveTo(Viewport(tab, 0, new PdfPoint(100 + (5 * Cm), 503)), snap: true);
        measure.ResultText.ShouldBe(Format(PdfPro.App.Resources.Strings.Measure_Result_Distance, "5 cm", "0"), "Shift: level");
        measure.Press(Viewport(tab, 0, new PdfPoint(100 + (5 * Cm), 503)), 1, snap: true);

        var measurement = measure.Measurements.ShouldHaveSingleItem();
        measurement.Kind.ShouldBe(MeasureKind.Distance);
        measurement.Points[1].Y.ShouldBe(500, 0.01);
        measure.LabelOf(measurement).ShouldBe("5 cm");
        measure.IsMeasuring.ShouldBeFalse();
        tab.IsDirty.ShouldBeFalse("measuring changes nothing");
    }

    [Fact]
    public async Task AnArea_IsClosedOnItsFirstCorner_AndAPlanScaleGivesSquareMetres()
    {
        var tab = await MeasuringAsync();
        var measure = tab.Measure;
        measure.IsAreaTool = true;
        measure.Unit = LengthUnit.Metre;
        measure.Scale = 100;
        PdfPoint[] room = [new(100, 100), new(100 + (2 * Cm), 100), new(100 + (2 * Cm), 100 + (3 * Cm)), new(100, 100 + (3 * Cm))];

        foreach (var corner in room)
        {
            measure.Press(Viewport(tab, 0, corner), 1, snap: false);
        }

        measure.Press(Viewport(tab, 0, room[0]), 1, snap: false);

        var area = measure.Measurements.ShouldHaveSingleItem();
        area.Points.Count.ShouldBe(4);
        measure.LabelOf(area).ShouldBe(Format(PdfPro.App.Resources.Strings.Measure_Label_Area, Measurements.Format(6, LengthUnit.Metre, squared: true), Measurements.Format(10, LengthUnit.Metre)));
    }

    [Fact]
    public async Task ALine_IsFinishedByADoubleClick_BackspaceTakingBackACorner_AndEscDroppingIt()
    {
        var tab = await MeasuringAsync();
        var measure = tab.Measure;
        measure.IsPerimeterTool = true;
        measure.Unit = LengthUnit.Point;

        measure.Press(Viewport(tab, 0, new PdfPoint(100, 100)), 1, false);
        measure.Press(Viewport(tab, 0, new PdfPoint(200, 100)), 1, false);
        measure.Press(Viewport(tab, 0, new PdfPoint(300, 300)), 1, false);
        measure.RemoveLastPoint().ShouldBeTrue();
        measure.Press(Viewport(tab, 0, new PdfPoint(200, 200)), 1, false);
        measure.Press(Viewport(tab, 0, new PdfPoint(200, 200)), 2, false);

        measure.LabelOf(measure.Measurements.ShouldHaveSingleItem()).ShouldBe(Measurements.Format(200, LengthUnit.Point));
        measure.Press(Viewport(tab, 1, new PdfPoint(50, 50)), 1, false);
        measure.Escape().ShouldBeTrue("drops the line being made");
        measure.IsMeasuring.ShouldBeFalse();
        measure.IsActive.ShouldBeTrue();
        measure.Escape().ShouldBeTrue("then turns the tool off");
        measure.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task SavingOnThePage_AddsTheMeasurementsAsCommentsWithTheirValues_InOneUndoableStep()
    {
        var tab = await MeasuringAsync();
        var measure = tab.Measure;
        measure.Press(Viewport(tab, 0, new PdfPoint(100, 500)), 1, false);
        measure.Press(Viewport(tab, 0, new PdfPoint(100 + (5 * Cm), 500)), 1, false);
        measure.IsAreaTool = true;
        foreach (var corner in new PdfPoint[] { new(100, 100), new(200, 100), new(200, 200) })
        {
            measure.Press(Viewport(tab, 1, corner), 1, false);
        }

        measure.Press(Viewport(tab, 1, new PdfPoint(200, 200)), 2, false);

        measure.SaveCommand.Execute(null);

        measure.HasMeasurements.ShouldBeFalse("they are comments now");
        var first = tab.Workspace.Session.Plan[0].Annotations;
        var line = first.OfType<LineAnnotation>().ShouldHaveSingleItem();
        line.Contents.ShouldBe("50 mm");
        line.StartEnding.ShouldBe(LineEnding.ClosedArrow);
        first.OfType<FreeTextAnnotation>().ShouldHaveSingleItem().Contents.ShouldBe("50 mm");
        tab.Workspace.Session.Plan[1].Annotations.OfType<PolyAnnotation>().ShouldHaveSingleItem().Closed.ShouldBeTrue();
        tab.UndoCommand.Execute(null);
        tab.Workspace.Session.Plan[0].Annotations.ShouldBeEmpty("one step takes them all back");
    }

    [Fact]
    public async Task MeasurementsOfAPageDeleted_AreDropped()
    {
        var tab = await MeasuringAsync();
        var measure = tab.Measure;
        measure.Press(Viewport(tab, 1, new PdfPoint(100, 100)), 1, false);
        measure.Press(Viewport(tab, 1, new PdfPoint(200, 100)), 1, false);
        measure.HasMeasurements.ShouldBeTrue();

        tab.Apply(TestOperations.Catalog().Get<PdfPro.Operations.Organizing.DeletePagesOperation>(), new PdfPro.Operations.Organizing.DeletePagesParams("2")).ShouldBeTrue();

        measure.HasMeasurements.ShouldBeFalse();
    }

    private static string Format(string format, params object[] values) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, values);

    private static Guid PageKey(DocumentTabViewModel tab, int index) => tab.Workspace.Pages.Pages[index].Key;

    private static ViewPoint Viewport(DocumentTabViewModel tab, int page, PdfPoint point)
    {
        var layout = tab.Viewer.Layout;
        var bounds = layout.GetPageBounds(page);
        var mapper = new PageCoordinateMapper(tab.Editor.GeometryOf(PageKey(tab, page)).ShouldNotBeNull(), layout.Scale);
        var shown = mapper.PdfToScreen(point);
        return new ViewPoint(bounds.X + shown.X - tab.Viewer.ScrollOffset.X, bounds.Y + shown.Y - tab.Viewer.ScrollOffset.Y);
    }

    private async Task<DocumentTabViewModel> MeasuringAsync()
    {
        var tab = await OpenAsync();
        tab.ToggleMeasureCommand.Execute(null);
        return tab;
    }

    private async Task<DocumentTabViewModel> OpenAsync()
    {
        var tab = _factory.Create(await _fixture.CreateAsync(FakePdfDocument.WithPages(2)));
        tab.Viewer.ViewportSize = new ViewSize(1000, 3000);
        foreach (var page in tab.Workspace.Pages.Pages)
        {
            await tab.Editor.GetGeometryAsync(page.Key);
        }

        return tab;
    }
}
