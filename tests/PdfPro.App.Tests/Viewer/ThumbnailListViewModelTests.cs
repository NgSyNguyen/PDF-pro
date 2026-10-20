using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;

namespace PdfPro.App.Tests.Viewer;

public sealed class ThumbnailListViewModelTests : IDisposable
{
    private static readonly ViewSize Viewport = new(1000, 700);
    private static readonly PdfSize Landscape = new(842, 595);

    private readonly FakePdfDocument _document = new([FakePdfDocument.A4, Landscape, .. Enumerable.Repeat(FakePdfDocument.A4, 18)]);
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly FakeThumbnailProvider _provider = new();
    private readonly CapturingLogger<ThumbnailListViewModel> _logger = new();
    private readonly DocumentViewerViewModel _viewer;
    private readonly VirtualDocument _pages;

    public ThumbnailListViewModelTests()
    {
        _pages = VirtualDocuments.Of(_document);
        _viewer = new DocumentViewerViewModel(_pages, new FakePageImageRenderer { AutoComplete = true }, _cache, new ImmediateUiDispatcher(), new ManualDebouncerFactory(), NullLogger<DocumentViewerViewModel>.Instance);
        _viewer.ViewportSize = Viewport;
    }

    public void Dispose()
    {
        _viewer.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public void Items_HaveTheThumbnailSizeOfEachPage()
    {
        using var list = Create();

        list.Items.Count.ShouldBe(20);
        list.Items.Select(i => i.Number).ShouldBe(Enumerable.Range(1, 20));
        list.Items[0].Width.ShouldBe(ThumbnailGeometry.SizeFor(FakePdfDocument.A4).Width, 1e-9);
        list.Items[0].Height.ShouldBe(ThumbnailGeometry.MaxHeight, 1e-9);
        list.Items[1].Width.ShouldBe(ThumbnailGeometry.MaxWidth, 1e-9);
        list.Items[1].Height.ShouldBeLessThan(list.Items[1].Width, "a landscape page gives a wide thumbnail");
        list.Items.ShouldAllBe(i => i.Image == null);
        _provider.Loads.ShouldBeEmpty("nothing loads before the list shows the items");
    }

    [Fact]
    public void Realize_LoadsTheThumbnailAtTheDpiOfTheView()
    {
        using var list = Create();
        var item = list.Items[3];

        item.Realize(1.5);

        var load = _provider.Loads.ShouldHaveSingleItem();
        load.Request.PageIndex.ShouldBe(3);
        load.Request.DpiScale.ShouldBe(1.5);
        list.PendingCount.ShouldBe(1);

        load.Complete();

        DisplayImage.SourceOf(item.Image).ShouldBeSameAs(load.Result);
        item.LoadFailed.ShouldBeFalse();
        list.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void Realize_IsIdempotent()
    {
        using var list = Create();
        var item = list.Items[0];

        item.Realize(1.0);
        item.Realize(1.0);
        _provider.Loads.Count.ShouldBe(1, "a pending load is not repeated");

        _provider.Loads[0].Complete();
        item.Realize(1.0);
        _provider.Loads.Count.ShouldBe(1, "a loaded thumbnail is not loaded again");
    }

    [Fact]
    public void Realize_AtAnotherDpi_ReplacesThePendingLoad()
    {
        using var list = Create();
        var item = list.Items[0];

        item.Realize(1.0);
        item.Realize(2.0);

        _provider.Loads.Count.ShouldBe(2);
        _provider.Loads[0].Token.IsCancellationRequested.ShouldBeTrue();
        _provider.Loads[1].Request.DpiScale.ShouldBe(2.0);
        list.PendingCount.ShouldBe(1);
    }

    [Fact]
    public void Realize_AtAnotherDpi_KeepsTheOldImageUntilTheSharperOneArrives()
    {
        using var list = Create();
        var item = list.Items[0];
        item.Realize(1.0);
        _provider.Loads[0].Complete();
        var first = item.Image;

        item.Realize(2.0);
        item.Image.ShouldBeSameAs(first);

        _provider.Loads[1].Complete();
        DisplayImage.SourceOf(item.Image).ShouldBeSameAs(_provider.Loads[1].Result);
    }

    [Fact]
    public void Realize_UsesTheMemoryCacheWithoutLoading()
    {
        using var list = Create();
        var cached = FakePageImageRenderer.CreateImage();
        _provider.AddCached(_document, 2, 1.25, cached);

        list.Items[2].Realize(1.25);

        DisplayImage.SourceOf(list.Items[2].Image).ShouldBeSameAs(cached);
        _provider.Loads.ShouldBeEmpty();
        list.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void Release_CancelsTheLoadAndDropsTheImage()
    {
        using var list = Create();
        var loaded = list.Items[0];
        var pending = list.Items[1];
        loaded.Realize(1.0);
        _provider.Loads[0].Complete();
        pending.Realize(1.0);

        loaded.Release();
        pending.Release();

        loaded.Image.ShouldBeNull();
        _provider.Loads[1].Token.IsCancellationRequested.ShouldBeTrue();
        pending.Image.ShouldBeNull();
        list.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void LoadThatFinishesAfterRelease_IsIgnored()
    {
        var ui = new QueuedUiDispatcher();
        using var list = Create(ui);
        var item = list.Items[0];
        item.Realize(1.0);
        _provider.Loads[0].Complete(); // result posted to the (busy) UI thread

        item.Release();
        ui.RunPending();

        item.Image.ShouldBeNull();
        list.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void FailedLoad_MarksTheItemAndLogsOnce()
    {
        using var list = Create();
        _provider.FailWith = new InvalidOperationException("broken page");

        list.Items[0].Realize(1.0);
        list.Items[1].Realize(1.0);

        list.Items[0].LoadFailed.ShouldBeTrue();
        list.Items[1].LoadFailed.ShouldBeTrue();
        list.PendingCount.ShouldBe(0);
        _logger.Entries.Count(e => e.Level == LogLevel.Warning).ShouldBe(1);
        _logger.Entries[0].Message.ShouldContain("page 1");
    }

    [Fact]
    public void Selection_FollowsTheViewerPage()
    {
        using var list = Create();
        list.SelectedItem.ShouldBeSameAs(list.Items[0]);

        _viewer.NavigateTo(7);

        list.SelectedItem.ShouldBeSameAs(list.Items[7]);
    }

    [Fact]
    public void SelectingAThumbnail_ShowsItsPage()
    {
        using var list = Create();

        list.SelectedItem = list.Items[12];

        _viewer.CurrentPageIndex.ShouldBe(12);
        list.SelectedItem.ShouldBeSameAs(list.Items[12]);
    }

    [Fact]
    public void ClearedSelection_IsRestoredToTheCurrentPage()
    {
        var ui = new QueuedUiDispatcher();
        using var list = Create(ui);
        _viewer.NavigateTo(4);

        list.SelectedItem = null; // the list does this while it switches documents
        ui.RunPending();

        list.SelectedItem.ShouldBeSameAs(list.Items[4]);
        _viewer.CurrentPageIndex.ShouldBe(4);
    }

    [Fact]
    public void ShowPage_GoesToTheTopOfThePageEvenWhenItIsSelected()
    {
        using var list = Create();
        _viewer.NavigateTo(5, new ViewPoint(0, 400));
        var scrolledInside = _viewer.ScrollOffset;

        list.ShowPageCommand.Execute(list.Items[5]);

        _viewer.CurrentPageIndex.ShouldBe(5);
        _viewer.ScrollOffset.Y.ShouldBeLessThan(scrolledInside.Y);
    }

    [Fact]
    public void Dispose_CancelsLoadsAndStopsFollowingTheViewer()
    {
        var list = Create();
        list.Items[0].Realize(1.0);

        list.Dispose();
        _viewer.NavigateTo(9);
        list.Items[1].Realize(1.0);

        _provider.Loads[0].Token.IsCancellationRequested.ShouldBeTrue();
        _provider.Loads.Count.ShouldBe(1, "a disposed list loads nothing");
        _provider.Forgotten.ShouldBe([_document.Id]);
        list.SelectedItem.ShouldBeSameAs(list.Items[0]);
    }

    private ThumbnailListViewModel Create(IUiDispatcher? ui = null)
        => new(_pages, _ => @"C:\docs\sample.pdf", _viewer, _provider, ui ?? new ImmediateUiDispatcher(), _logger);
}
