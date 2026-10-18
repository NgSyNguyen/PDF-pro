using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfPro.App.Resources;
using PdfPro.App.Services.Rendering;
using PdfPro.App.Tests.TestSupport;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Infrastructure.Settings;
using PdfPro.Pdfium;

namespace PdfPro.App.Tests.Viewer;

public sealed class BookmarkTreeViewModelTests : IDisposable
{
    private static readonly ViewSize Viewport = new(1000, 700);

    private readonly FakePdfDocument _document = FakePdfDocument.WithPages(30);
    private readonly RenderCache _cache = new(new FakeSettingsService(AppSettings.Default));
    private readonly CapturingLogger<BookmarkTreeViewModel> _logger = new();
    private readonly DocumentViewerViewModel _viewer;

    public BookmarkTreeViewModelTests()
    {
        _viewer = new DocumentViewerViewModel(_document, new FakePageImageRenderer { AutoComplete = true }, _cache, new ImmediateUiDispatcher(), new ManualDebouncerFactory(), NullLogger<DocumentViewerViewModel>.Instance);
        _viewer.ViewportSize = Viewport;
    }

    public void Dispose()
    {
        _viewer.Dispose();
        _cache.Dispose();
    }

    [Fact]
    public async Task Load_BuildsTheTreeWithCleanTitles()
    {
        _document.Bookmarks =
        [
            Bookmark("Chương 1\r\n Giới thiệu", 0,
            [
                Bookmark("Mục 1.1", 2),
                Bookmark("   ", 3),
            ]),
            Bookmark("Chương 2", 10),
            new PdfBookmark("Phụ lục", null, []),
        ];
        using var tree = Create();
        tree.State.ShouldBe(BookmarkTreeState.Loading);
        tree.IsLoading.ShouldBeTrue();

        await tree.LoadAsync();

        tree.State.ShouldBe(BookmarkTreeState.Ready);
        tree.HasItems.ShouldBeTrue();
        tree.IsLoading.ShouldBeFalse();
        tree.Items.Select(i => i.Title).ShouldBe(["Chương 1 Giới thiệu", "Chương 2", "Phụ lục"]);
        tree.Items[0].Children.Select(i => i.Title).ShouldBe(["Mục 1.1".Normalize(NormalizationForm.FormC), Strings.Bookmark_Untitled]);
        tree.Items[0].Children[0].Title.IsNormalized(NormalizationForm.FormC).ShouldBeTrue();
        tree.Items[2].Destination.ShouldBeNull();
        tree.Items.ShouldAllBe(i => !i.IsExpanded, "several roots start collapsed");
    }

    [Fact]
    public async Task Load_ExpandsALoneRoot()
    {
        _document.Bookmarks = [Bookmark("Báo cáo", 0, [Bookmark("Phần A", 1), Bookmark("Phần B", 2)])];
        using var tree = Create();

        await tree.LoadAsync();

        tree.Items.ShouldHaveSingleItem().IsExpanded.ShouldBeTrue();
    }

    [Fact]
    public async Task Load_WithoutBookmarks_IsEmpty()
    {
        using var tree = Create();

        await tree.LoadAsync();

        tree.State.ShouldBe(BookmarkTreeState.Empty);
        tree.IsEmpty.ShouldBeTrue();
        tree.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Load_Failure_IsShownAndLogged()
    {
        _document.BookmarksError = new InvalidOperationException("damaged outline");
        using var tree = Create();

        await tree.LoadAsync();

        tree.State.ShouldBe(BookmarkTreeState.Failed);
        tree.IsFailed.ShouldBeTrue();
        _logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Dispose_WhileLoading_StopsQuietly()
    {
        _document.PendingBookmarks = new TaskCompletionSource<IReadOnlyList<PdfBookmark>>();
        var tree = Create();
        var loading = tree.LoadAsync();

        tree.Dispose();
        await loading;

        tree.State.ShouldBe(BookmarkTreeState.Loading);
        _logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Navigate_WithoutCoordinates_ShowsTheTopOfThePage()
    {
        _document.Bookmarks = [Bookmark("Chương 3", 12)];
        using var tree = Create();
        await tree.LoadAsync();

        await tree.NavigateCommand.ExecuteAsync(tree.Items[0]);

        _viewer.CurrentPageIndex.ShouldBe(12);
        _viewer.ScrollOffset.Y.ShouldBe(_viewer.Layout.GetPageBounds(12).Y - _viewer.Layout.Spacing.RowGap / 2, 1e-6);
    }

    [Fact]
    public async Task Navigate_WithATop_ScrollsToThatSpotOfThePage()
    {
        _document.Bookmarks = [new PdfBookmark("Mục 4.2", new PdfDestination(5, null, 442, null), [])];
        using var tree = Create();
        await tree.LoadAsync();

        await tree.NavigateCommand.ExecuteAsync(tree.Items[0]);

        // A4 is 842 pt tall, so a top of 442 pt is 400 pt below the page's top edge.
        var expected = _viewer.Layout.GetPageBounds(5).Y + (400 * _viewer.Layout.Scale) - (_viewer.Layout.Spacing.RowGap / 2);
        _viewer.CurrentPageIndex.ShouldBe(5);
        _viewer.ScrollOffset.Y.ShouldBe(expected, 1e-6);
    }

    [Fact]
    public async Task Navigate_UsesTheGeometryOfRotatedPages()
    {
        // Rotated a quarter turn: PDF x runs down the displayed page.
        _document.Geometry = i => new PageGeometry(new PdfRect(0, 0, 842, 595), 90);
        _document.Bookmarks = [new PdfBookmark("Bảng", new PdfDestination(2, 300, 100, null), [])];
        using var tree = Create();
        await tree.LoadAsync();

        await tree.NavigateCommand.ExecuteAsync(tree.Items[0]);

        var expected = _viewer.Layout.GetPageBounds(2).Y + (300 * _viewer.Layout.Scale) - (_viewer.Layout.Spacing.RowGap / 2);
        _viewer.ScrollOffset.Y.ShouldBe(expected, 1e-6);
    }

    [Fact]
    public async Task Navigate_IgnoresEntriesWithoutAValidDestination()
    {
        _document.Bookmarks = [new PdfBookmark("Nhóm", null, []), Bookmark("Ngoài tài liệu", 99)];
        using var tree = Create();
        await tree.LoadAsync();
        _viewer.NavigateTo(3);

        await tree.NavigateCommand.ExecuteAsync(tree.Items[0]);
        await tree.NavigateCommand.ExecuteAsync(tree.Items[1]);
        await tree.NavigateCommand.ExecuteAsync(null);

        _viewer.CurrentPageIndex.ShouldBe(3);
    }

    [Fact]
    public async Task Navigate_AfterDispose_DoesNothing()
    {
        _document.Bookmarks = [Bookmark("Chương 2", 8)];
        var tree = Create();
        await tree.LoadAsync();
        tree.Dispose();

        await tree.NavigateCommand.ExecuteAsync(tree.Items[0]);

        _viewer.CurrentPageIndex.ShouldBe(0);
    }

    private static PdfBookmark Bookmark(string title, int page, IReadOnlyList<PdfBookmark>? children = null)
        => new(title, new PdfDestination(page, null, null, null), children ?? []);

    private BookmarkTreeViewModel Create() => new(_document, _viewer, new ImmediateUiDispatcher(), _logger);
}
