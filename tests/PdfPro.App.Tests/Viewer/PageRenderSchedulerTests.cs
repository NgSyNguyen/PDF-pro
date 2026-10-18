using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Viewer;

public sealed class PageRenderSchedulerTests : IDisposable
{
    private readonly FakePdfDocument _document = FakePdfDocument.WithPages(50);
    private readonly FakePageImageRenderer _renderer = new();
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly PageViewModel[] _pages;
    private readonly PageRenderScheduler _scheduler;

    public PageRenderSchedulerTests()
    {
        _pages = Enumerable.Range(0, _document.PageCount).Select(i => new PageViewModel(i)).ToArray();
        _scheduler = new PageRenderScheduler(_document, _pages, _renderer, _cache, new ImmediateUiDispatcher(), NullLogger.Instance);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public void Update_RendersVisiblePagesFirstThenOneBufferPageEachSide()
    {
        var layout = Layout(zoom: 1.0);
        var viewport = ViewportAt(layout, page: 10, height: 900);

        _scheduler.Update(layout, viewport, dpiScale: 1.0, zoomSettled: true, detailAllowed: true);

        var visible = layout.GetVisiblePages(viewport)!.Value;
        var expected = Enumerable.Range(visible.First - 1, visible.Last - visible.First + 3).ToArray();
        _renderer.Calls.Select(c => c.PageIndex).Order().ShouldBe(expected);
        _renderer.Calls.Where(c => c.PageIndex >= visible.First && c.PageIndex <= visible.Last).ShouldAllBe(c => c.Priority == PdfiumPriority.High);
        _renderer.Calls.Where(c => c.PageIndex < visible.First || c.PageIndex > visible.Last).ShouldAllBe(c => c.Priority == PdfiumPriority.Normal);
        _renderer.Calls.Take(visible.Last - visible.First + 1).ShouldAllBe(c => c.Priority == PdfiumPriority.High, "visible pages are requested first");
    }

    [Fact]
    public void CompletedRender_SetsTheImageAndFillsTheCache()
    {
        var layout = Layout(zoom: 1.0);
        _scheduler.Update(layout, ViewportAt(layout, 0, 900), 1.0, true, true);

        _renderer.CompleteAll();

        _pages[0].Image.ShouldNotBeNull();
        _pages[0].RenderFailed.ShouldBeFalse();
        _cache.Count.ShouldBe(_renderer.Calls.Count);
        _scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void CachedPages_AreShownWithoutRendering()
    {
        var layout = Layout(zoom: 1.0);
        var viewport = ViewportAt(layout, 0, 900);
        _scheduler.Update(layout, viewport, 1.0, true, true);
        _renderer.CompleteAll();
        var calls = _renderer.Calls.Count;

        // Scroll far away (images released), then come back.
        _scheduler.Update(layout, ViewportAt(layout, 30, 900), 1.0, true, true);
        _pages[0].Image.ShouldBeNull("off-screen pages release their images to the cache");
        _scheduler.Update(layout, viewport, 1.0, true, true);

        _pages[0].Image.ShouldNotBeNull();
        _renderer.Calls.Skip(calls).Select(c => c.PageIndex).ShouldNotContain(0, "page 0 came from the cache");
    }

    [Fact]
    public void BlankPage_ShowsItsSidebarThumbnailUntilTheRenderArrives()
    {
        var thumbnail = FakePageImageRenderer.CreateImage();
        _cache.Add(RenderKey.Thumbnail(_document.Id, 0), thumbnail, 4);
        var layout = Layout(zoom: 1.0);

        _scheduler.Update(layout, ViewportAt(layout, 0, 900), 1.0, true, true);

        DisplayImage.SourceOf(_pages[0].Image).ShouldBeSameAs(thumbnail);
        _renderer.Calls.ShouldContain(c => c.PageIndex == 0, "the real render is still requested");

        _renderer.CompleteAll();

        _pages[0].Image.ShouldNotBeNull();
        DisplayImage.SourceOf(_pages[0].Image).ShouldNotBeSameAs(thumbnail);
        _pages[1].Image.ShouldNotBeNull("pages without a thumbnail simply wait for their render");
    }

    [Fact]
    public void ScrollingAway_CancelsPendingRenders()
    {
        var layout = Layout(zoom: 1.0);
        _scheduler.Update(layout, ViewportAt(layout, 0, 900), 1.0, true, true);
        var first = _renderer.Calls.ToList();

        _scheduler.Update(layout, ViewportAt(layout, 40, 900), 1.0, true, true);

        first.ShouldAllBe(c => c.Token.IsCancellationRequested);
        _renderer.CompleteAll();
        _pages[0].Image.ShouldBeNull("a cancelled render must not show up later");
    }

    [Fact]
    public void WhileZooming_PagesKeepTheirImageAndOnlyBlankPagesRender()
    {
        var layout = Layout(zoom: 1.0);
        var viewport = ViewportAt(layout, 0, 900);
        _scheduler.Update(layout, viewport, 1.0, true, true);
        _renderer.CompleteAll();
        var oldImage = _pages[0].Image;
        var calls = _renderer.Calls.Count;

        var zoomed = Layout(zoom: 1.5);
        _scheduler.Update(zoomed, viewport, 1.0, zoomSettled: false, detailAllowed: false);

        _pages[0].Image.ShouldBeSameAs(oldImage, "the old image is shown scaled until the zoom settles");
        _renderer.Calls.Skip(calls).Select(c => c.PageIndex).ShouldNotContain(0);

        _scheduler.Update(zoomed, viewport, 1.0, zoomSettled: true, detailAllowed: true);
        var rerender = _renderer.Calls.Skip(calls).Where(c => c.PageIndex == 0).ShouldHaveSingleItem();
        rerender.Scale.ShouldBe(zoomed.Scale, 1e-9);
    }

    [Fact]
    public void Scale_IncludesTheDpiScale()
    {
        var layout = Layout(zoom: 1.0);

        _scheduler.Update(layout, ViewportAt(layout, 0, 900), dpiScale: 1.5, true, true);

        _renderer.Calls[0].Scale.ShouldBe(1.0 * 96 / 72 * 1.5, 1e-9);
    }

    [Fact]
    public void HugePage_GetsABoundedBaseImageAndADetailTile()
    {
        var layout = Layout(zoom: 8.0);
        var bounds = layout.GetPageBounds(5);
        var viewport = new ViewRect(bounds.X + 2000, bounds.Y + 3000, 1200, 800);

        _scheduler.Update(layout, viewport, dpiScale: 1.25, zoomSettled: true, detailAllowed: false);

        var baseCall = _renderer.Calls.First(c => c.PageIndex == 5 && c.Region is null);
        var basePixels = (long)PixelSize.FromPoints(595, baseCall.Scale) * PixelSize.FromPoints(842, baseCall.Scale);
        basePixels.ShouldBeLessThanOrEqualTo(PageRenderScheduler.MaxFullPagePixels + 10_000);
        _renderer.Calls.ShouldNotContain(c => c.Region != null, "detail tiles wait until scrolling pauses");

        _scheduler.Update(layout, viewport, dpiScale: 1.25, zoomSettled: true, detailAllowed: true);

        var detail = _renderer.Calls.Single(c => c.PageIndex == 5 && c.Region != null);
        detail.Scale.ShouldBe(layout.Scale * 1.25, 1e-9);
        var fullWidth = PixelSize.FromPoints(595, detail.Scale);
        var fullHeight = PixelSize.FromPoints(842, detail.Scale);
        var region = detail.Region!.Value;
        new PixelRect(0, 0, fullWidth, fullHeight).Contains(region).ShouldBeTrue();
        region.Contains(new PixelRect(2500, 3750, 1500, 1000)).ShouldBeTrue("the tile covers the visible part (2000,3000 + 1200x800 at 1.25)");

        detail.Complete();
        _pages[5].DetailImage.ShouldNotBeNull();
        _pages[5].DetailBounds.Width.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FailedRender_MarksThePage()
    {
        _renderer.FailWith = new InvalidOperationException("damaged content stream");
        var layout = Layout(zoom: 1.0);

        _scheduler.Update(layout, ViewportAt(layout, 0, 900), 1.0, true, true);

        _pages[0].RenderFailed.ShouldBeTrue();
        _pages[0].Image.ShouldBeNull();
    }

    [Fact]
    public void Dispose_CancelsEverything()
    {
        var layout = Layout(zoom: 1.0);
        _scheduler.Update(layout, ViewportAt(layout, 0, 900), 1.0, true, true);

        _scheduler.Dispose();

        _renderer.Calls.ShouldAllBe(c => c.Token.IsCancellationRequested);
        _scheduler.PendingCount.ShouldBe(0);
    }

    private DocumentLayout Layout(double zoom) => DocumentLayout.Create(_document.PageSizes, zoom, PageLayoutMode.Single, 1200);

    private static ViewRect ViewportAt(DocumentLayout layout, int page, double height)
        => new(0, layout.GetPageBounds(page).Y, 1200, height);
}
