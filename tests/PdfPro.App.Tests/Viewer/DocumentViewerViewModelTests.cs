using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.Viewer;

public sealed class DocumentViewerViewModelTests : IDisposable
{
    private static readonly ViewSize Viewport = new(1000, 700);

    private readonly FakePdfDocument _document = FakePdfDocument.WithPages(20);
    private readonly FakePageImageRenderer _renderer = new() { AutoComplete = true };
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly ManualDebouncerFactory _debouncers = new();
    private readonly DocumentViewerViewModel _viewer;

    public DocumentViewerViewModelTests()
    {
        _viewer = new DocumentViewerViewModel(VirtualDocuments.Of(_document), _renderer, _cache, new ImmediateUiDispatcher(), _debouncers, NullLogger<DocumentViewerViewModel>.Instance);
    }

    public void Dispose()
    {
        _viewer.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public void BeforeTheViewConnects_NothingRenders()
    {
        _viewer.Zoom.ShouldBe(1.0);
        _viewer.ZoomMode.ShouldBe(ZoomMode.FitWidth);
        _viewer.PageCount.ShouldBe(20);
        _renderer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void FirstViewport_FitsTheWidthAndRendersTheFirstPages()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.Zoom.ShouldBe(ZoomCalculator.FitWidth(_document.PageSizes, PageLayoutMode.Single, Viewport.Width), 1e-9);
        _viewer.Layout.Extent.Width.ShouldBe(Viewport.Width, 1e-6);
        _viewer.CurrentPageIndex.ShouldBe(0);
        _renderer.Calls.Select(c => c.PageIndex).ShouldContain(0);
        _viewer.Pages[0].Image.ShouldNotBeNull();
    }

    [Fact]
    public void ZoomIn_GoesToTheNextPresetAndLeavesFitMode()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.ZoomInCommand.Execute(null);

        _viewer.ZoomMode.ShouldBe(ZoomMode.Custom);
        _viewer.Zoom.ShouldBe(ZoomCalculator.ZoomIn(ZoomCalculator.FitWidth(_document.PageSizes, PageLayoutMode.Single, Viewport.Width)));
        _viewer.Layout.Zoom.ShouldBe(_viewer.Zoom);
        _viewer.ZoomText.ShouldContain("%");
    }

    [Fact]
    public void FitPage_ShowsTheWholeCurrentPage()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(4);

        _viewer.FitPageCommand.Execute(null);

        _viewer.ZoomMode.ShouldBe(ZoomMode.FitPage);
        _viewer.CurrentPageIndex.ShouldBe(4);
        var page = _viewer.Layout.GetPageBounds(4);
        page.Height.ShouldBeLessThanOrEqualTo(Viewport.Height);
        page.Y.ShouldBeGreaterThanOrEqualTo(_viewer.ScrollOffset.Y);
        page.Bottom.ShouldBeLessThanOrEqualTo(_viewer.ScrollOffset.Y + Viewport.Height + 0.01);
    }

    [Fact]
    public void GoToPageNumber_ScrollsToThePage()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.GoToPageNumberCommand.Execute("10");

        _viewer.CurrentPageIndex.ShouldBe(9);
        _viewer.CurrentPageNumber.ShouldBe(10);
        _viewer.ScrollOffset.Y.ShouldBe(_viewer.Layout.GetPageBounds(9).Y - (LayoutSpacing.Default.RowGap / 2), 1e-6);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void GoToPageNumber_InvalidInput_KeepsThePageButRefreshesTheBox(string? text)
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(3);
        var raised = new List<string?>();
        _viewer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _viewer.GoToPageNumberCommand.Execute(text);

        _viewer.CurrentPageIndex.ShouldBe(3);
        raised.ShouldContain(nameof(DocumentViewerViewModel.CurrentPageNumber));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("999", 19)]
    public void GoToPageNumber_ClampsToTheDocument(string text, int expectedIndex)
    {
        _viewer.ViewportSize = Viewport;

        _viewer.GoToPageNumberCommand.Execute(text);

        _viewer.CurrentPageIndex.ShouldBe(expectedIndex);
    }

    [Fact]
    public void PageNavigationCommands()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.NextPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(1);
        _viewer.LastPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(19, "the last page is reported even though it cannot scroll to the top");
        _viewer.PreviousPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(18);
        _viewer.FirstPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(0);
        _viewer.ScrollOffset.Y.ShouldBe(0);
    }

    [Fact]
    public void FacingLayout_NavigatesByRow()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.LayoutMode = PageLayoutMode.Facing;

        _viewer.NextPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(2);
        _viewer.NextPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(4);
        _viewer.PreviousPageCommand.Execute(null);
        _viewer.CurrentPageIndex.ShouldBe(2);
        _viewer.IsFacingLayout.ShouldBeTrue();
        _viewer.IsSingleLayout.ShouldBeFalse();
    }

    [Fact]
    public void ChangingTheLayoutMode_KeepsTheCurrentPage()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(7);

        _viewer.IsFacingWithCoverLayout = true;

        _viewer.LayoutMode.ShouldBe(PageLayoutMode.FacingWithCover);
        _viewer.CurrentPageIndex.ShouldBe(7);
        var row = _viewer.Layout.Rows[_viewer.Layout.GetRowOfPage(7)];
        _viewer.Layout.GetPageBounds(row.FirstPage).Y.ShouldBeGreaterThanOrEqualTo(_viewer.ScrollOffset.Y - 0.01);
    }

    [Fact]
    public void Scrolling_UpdatesTheCurrentPage()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.ScrollOffset = new ViewPoint(0, _viewer.Layout.GetPageBounds(12).Y + 50);

        _viewer.CurrentPageIndex.ShouldBe(12);
    }

    [Fact]
    public void ZoomAtPoint_KeepsTheSpotUnderTheMouse()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(5);
        var anchor = new ViewPoint(500, 300);
        var before = PagePointAt(anchor);

        _viewer.ZoomAtPoint(notches: 3, anchor);

        _viewer.ZoomMode.ShouldBe(ZoomMode.Custom);
        _viewer.Zoom.ShouldBeGreaterThan(1.0);
        var after = PagePointAt(anchor);
        after.Page.ShouldBe(before.Page);
        after.Y.ShouldBe(before.Y, 0.01);
    }

    [Fact]
    public void ResizingInFitWidthMode_RefitsTheZoom()
    {
        _viewer.ViewportSize = Viewport;
        var first = _viewer.Zoom;

        _viewer.ViewportSize = new ViewSize(1400, 700);

        _viewer.Zoom.ShouldBeGreaterThan(first);
        _viewer.Zoom.ShouldBe(ZoomCalculator.FitWidth(_document.PageSizes, PageLayoutMode.Single, 1400), 1e-9);
    }

    [Fact]
    public void ResizingInCustomMode_KeepsTheZoom()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.ZoomToCommand.Execute(2.0);

        _viewer.ViewportSize = new ViewSize(1400, 700);

        _viewer.Zoom.ShouldBe(2.0);
    }

    [Fact]
    public void ZoomMenu_OffersFitModesAndPresets()
    {
        _viewer.ViewportSize = Viewport;

        _viewer.ZoomMenu.Count.ShouldBe(3 + ZoomCalculator.Presets.Count);
        var twoHundred = _viewer.ZoomMenu.Single(i => Equals(i.Parameter, 2.0));
        twoHundred.Command.Execute(twoHundred.Parameter);

        _viewer.Zoom.ShouldBe(2.0);
        _viewer.ZoomMenu[0].Command.Execute(null);
        _viewer.ZoomMode.ShouldBe(ZoomMode.FitWidth);
    }

    [Fact]
    public void ZoomRendersWaitUntilTheZoomSettles()
    {
        _viewer.ViewportSize = Viewport;
        _debouncers.FireAll();
        var calls = _renderer.Calls.Count;

        _viewer.ZoomInCommand.Execute(null);
        _viewer.ZoomInCommand.Execute(null);
        _renderer.Calls.Skip(calls).Where(c => c.PageIndex == 0).ShouldBeEmpty("page 0 keeps its image while zooming");

        _debouncers.FireAll();
        _renderer.Calls.Skip(calls).Where(c => c.PageIndex == 0).ShouldHaveSingleItem().Scale
            .ShouldBe(_viewer.Layout.Scale, 1e-9);
    }

    [Fact]
    public void SetDocument_MovedPage_KeepsItsViewModelImageAndPlaceOnScreen()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(5);
        var page = _viewer.Pages[5];
        var image = page.Image;
        var renders = _renderer.Calls.Count;

        _viewer.SetDocument(VirtualDocuments.Of(_viewer.Document.Plan.Move([5], 0), _document));

        _viewer.Pages[0].ShouldBeSameAs(page);
        page.Index.ShouldBe(0);
        page.Number.ShouldBe(1);
        page.Image.ShouldBeSameAs(image);
        _viewer.CurrentPageIndex.ShouldBe(0, "the page the user was reading stays the current one");
        _viewer.ScrollOffset.Y.ShouldBe(_viewer.Layout.GetPageBounds(0).Y, 1);
        _renderer.Calls.Skip(renders).ShouldNotContain(c => c.PageIndex == 5, "the moved page keeps its render");
    }

    [Fact]
    public void SetDocument_TurnedPage_RendersItTurned()
    {
        _viewer.ViewportSize = Viewport;
        _debouncers.FireAll(); // the first fit-width zoom has settled
        var page = _viewer.Pages[0];

        _viewer.SetDocument(VirtualDocuments.Of(_viewer.Document.Plan.Rotate([0], 90), _document));

        page.QuarterTurns.ShouldBe(1);
        _renderer.Calls.ShouldContain(c => c.PageIndex == 0 && c.Options.ExtraQuarterTurns == 1);
        page.ImageQuarterTurns.ShouldBe(1);
        page.ImageAngle.ShouldBe(0);
        _viewer.Layout.GetPageBounds(0).Width.ShouldBeGreaterThan(_viewer.Layout.GetPageBounds(0).Height);
    }

    [Fact]
    public void TurnedPage_ShowsItsOldImageTurnedUntilTheNewRenderArrives()
    {
        var page = new PageViewModel(Guid.NewGuid(), 0) { Image = FakePageImageRenderer.CreateImage() };

        page.MoveTo(0, quarterTurns: 3);

        page.ImageAngle.ShouldBe(270);
        page.ImageQuarterTurns = 3;
        page.ImageAngle.ShouldBe(0);
    }

    [Fact]
    public void SetDocument_DeletedCurrentPage_MovesToANeighbour()
    {
        _viewer.ViewportSize = Viewport;
        _viewer.NavigateTo(19);

        _viewer.SetDocument(VirtualDocuments.Of(_viewer.Document.Plan.Delete([18, 19]), _document));

        _viewer.PageCount.ShouldBe(18);
        _viewer.CurrentPageIndex.ShouldBe(17);
    }

    private (int Page, double X, double Y) PagePointAt(ViewPoint anchor)
    {
        var surface = _viewer.ScrollOffset + anchor;
        var page = _viewer.Layout.GetPageAt(surface)!.Value;
        var bounds = _viewer.Layout.GetPageBounds(page);
        return (page, (surface.X - bounds.X) / _viewer.Layout.Scale, (surface.Y - bounds.Y) / _viewer.Layout.Scale);
    }
}
