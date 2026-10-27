using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PdfPro.App.Resources;
using PdfPro.App.Services;
using PdfPro.App.Services.Documents;
using PdfPro.App.Services.Rendering;
using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;
using PdfPro.Pdfium;

namespace PdfPro.App.ViewModels.Viewer;

/// <summary>An entry of the zoom drop-down.</summary>
public sealed record ZoomMenuItem(string Label, ICommand Command, object? Parameter);

/// <summary>
/// State of one document view: zoom, layout mode, scroll position, current page and navigation. The view
/// (DocumentViewPanel) binds <see cref="Layout"/> and <see cref="ScrollOffset"/>, and reports its viewport size and
/// DPI back; all geometry comes from <see cref="DocumentLayout"/>.
/// </summary>
public sealed partial class DocumentViewerViewModel : ObservableObject, IDisposable
{
    internal static readonly TimeSpan ZoomSettleDelay = TimeSpan.FromMilliseconds(150);
    internal static readonly TimeSpan ScrollSettleDelay = TimeSpan.FromMilliseconds(120);

    private IReadOnlyList<PdfSize> _pageSizes;
    private readonly PageRenderScheduler _scheduler;
    private readonly IDebouncer _zoomSettle;
    private readonly IDebouncer _scrollSettle;
    private bool _zoomSettled = true;
    private bool _scrollSettled = true;
    private bool _applyingView;
    private int? _pinnedPage;
    private ViewPoint? _pinnedOffset;

    public DocumentViewerViewModel(VirtualDocument document, IPageImageRenderer renderer, RenderCache cache, IUiDispatcher ui, IDebouncerFactory debouncers, ILogger<DocumentViewerViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(debouncers);
        Document = document;
        _pageSizes = document.PageSizes;
        Pages = document.Pages.Select((p, i) => { var page = new PageViewModel(p.Key, i); page.MoveTo(i, p.QuarterTurns); return page; }).ToArray();
        _scheduler = new PageRenderScheduler(document, Pages, renderer, cache, ui, logger);
        _zoomSettle = debouncers.Create();
        _scrollSettle = debouncers.Create();
        Zoom = 1.0;
        ZoomMode = ZoomMode.FitWidth;
        LayoutMode = PageLayoutMode.Single;
        DpiScale = 1.0;
        Layout = DocumentLayout.Create(_pageSizes, Zoom, LayoutMode, 0);
    }

    /// <summary>The document as the page plan shows it.</summary>
    public VirtualDocument Document { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageCount))]
    public partial IReadOnlyList<PageViewModel> Pages { get; private set; }

    public int PageCount => Pages.Count;

    /// <summary>Entries of the zoom drop-down: fit modes, actual size and the presets.</summary>
    public IReadOnlyList<ZoomMenuItem> ZoomMenu => field ??=
    [
        new(Strings.Zoom_FitWidth, FitWidthCommand, null),
        new(Strings.Zoom_FitPage, FitPageCommand, null),
        new(Strings.Zoom_ActualSize, ActualSizeCommand, null),
        .. ZoomCalculator.Presets.Select(p => new ZoomMenuItem(p.ToString("P0", CultureInfo.CurrentCulture), ZoomToCommand, p)),
    ];

    public bool IsSingleLayout
    {
        get => LayoutMode == PageLayoutMode.Single;
        set
        {
            if (value)
            {
                LayoutMode = PageLayoutMode.Single;
            }
        }
    }

    public bool IsFacingLayout
    {
        get => LayoutMode == PageLayoutMode.Facing;
        set
        {
            if (value)
            {
                LayoutMode = PageLayoutMode.Facing;
            }
        }
    }

    public bool IsFacingWithCoverLayout
    {
        get => LayoutMode == PageLayoutMode.FacingWithCover;
        set
        {
            if (value)
            {
                LayoutMode = PageLayoutMode.FacingWithCover;
            }
        }
    }

    [ObservableProperty]
    public partial DocumentLayout Layout { get; private set; }

    /// <summary>Top-left of the viewport on the layout surface; two-way bound with the panel.</summary>
    [ObservableProperty]
    public partial ViewPoint ScrollOffset { get; set; }

    /// <summary>Reported by the view.</summary>
    [ObservableProperty]
    public partial ViewSize ViewportSize { get; set; }

    /// <summary>Device pixels per view unit, reported by the view (1.25 at 125 % Windows scaling).</summary>
    [ObservableProperty]
    public partial double DpiScale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; private set; }

    [ObservableProperty]
    public partial ZoomMode ZoomMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingleLayout), nameof(IsFacingLayout), nameof(IsFacingWithCoverLayout))]
    public partial PageLayoutMode LayoutMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageNumber))]
    public partial int CurrentPageIndex { get; private set; }

    public int CurrentPageNumber => CurrentPageIndex + 1;

    public string ZoomText => Zoom.ToString("P0", CultureInfo.CurrentCulture);

    internal PageRenderScheduler Scheduler => _scheduler;

    private ViewRect Viewport => new(ScrollOffset.X, ScrollOffset.Y, ViewportSize.Width, ViewportSize.Height);

    /// <summary>
    /// The middle of the part of a page that is on screen, in points of the page as displayed (origin top-left), or
    /// null when none of it is visible (or nothing is laid out yet): where new objects go.
    /// </summary>
    public ViewPoint? VisibleCenterOf(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Layout.PageCount || ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            return null;
        }

        var page = Layout.GetPageBounds(pageIndex);
        var visible = ViewRect.Intersect(page, Viewport);
        return visible.IsEmpty
            ? null
            : new ViewPoint((visible.X + (visible.Width / 2) - page.X) / Layout.Scale, (visible.Y + (visible.Height / 2) - page.Y) / Layout.Scale);
    }

    private ViewPoint ViewportCenter => new(ViewportSize.Width / 2, ViewportSize.Height / 2);

    /// <summary>Ctrl + mouse wheel: zooms keeping the page spot under <paramref name="anchorInViewport"/> fixed.</summary>
    public void ZoomAtPoint(double notches, ViewPoint anchorInViewport)
        => ApplyView(ZoomCalculator.ZoomByWheel(Zoom, notches), ZoomMode.Custom, LayoutMode, anchorInViewport);

    /// <summary>
    /// Shows <paramref name="pageIndex"/> at the top of the viewport (optionally a point inside it, in view units).
    /// With <paramref name="centerHorizontally"/> the page's row is centred instead of keeping the horizontal scroll.
    /// </summary>
    public void NavigateTo(int pageIndex, ViewPoint? pointInPage = null, bool centerHorizontally = false)
    {
        if (PageCount == 0)
        {
            return;
        }

        pageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        var offset = Layout.OffsetToShow(pageIndex, pointInPage ?? new ViewPoint(0, 0), centerHorizontally ? null : ScrollOffset.X, ViewportSize);
        var moved = offset != ScrollOffset;
        _pinnedPage = pageIndex;
        _pinnedOffset = offset;
        ScrollOffset = offset; // refreshes through OnScrollOffsetChanged when it moved
        CurrentPageIndex = pageIndex;
        if (!moved)
        {
            Refresh(zoomChanged: false);
        }
    }

    /// <summary>
    /// The page plan changed (pages moved, turned, deleted, inserted): rebuilds the pages and the layout while keeping
    /// the current page where it is on screen. Pages that only moved keep their image.
    /// </summary>
    public void SetDocument(VirtualDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var anchorIndex = Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1));
        var anchorKey = PageCount > 0 ? Pages[anchorIndex].Key : Guid.Empty;
        var anchorBounds = PageCount > 0 ? Layout.GetPageBounds(anchorIndex) : default;
        var withinPage = new ViewPoint(ScrollOffset.X - anchorBounds.X, ScrollOffset.Y - anchorBounds.Y);

        var existing = Pages.ToDictionary(p => p.Key);
        var pages = new PageViewModel[document.PageCount];
        for (var i = 0; i < pages.Length; i++)
        {
            var virtualPage = document.Pages[i];
            pages[i] = existing.TryGetValue(virtualPage.Key, out var page) ? page : new PageViewModel(virtualPage.Key, i);
            pages[i].MoveTo(i, virtualPage.QuarterTurns);
        }

        Document = document;
        _pageSizes = document.PageSizes;
        var index = document.IndexOf(anchorKey);
        if (index < 0)
        {
            index = Math.Clamp(anchorIndex, 0, Math.Max(0, document.PageCount - 1));
        }

        _pinnedPage = null;
        _pinnedOffset = null;
        CurrentPageIndex = index;
        _scheduler.SetDocument(document, pages);

        var zoom = ZoomMode == ZoomMode.Custom ? Zoom : ComputeFitZoom(ZoomMode);
        var layout = DocumentLayout.Create(_pageSizes, zoom, LayoutMode, ViewportSize.Width);
        var offset = ScrollOffset;
        if (document.PageCount > 0)
        {
            var bounds = layout.GetPageBounds(index);
            offset = layout.ClampOffset(
                new ViewPoint(bounds.X + Math.Clamp(withinPage.X, 0, bounds.Width), bounds.Y + Math.Clamp(withinPage.Y, 0, bounds.Height)),
                ViewportSize);
        }

        _applyingView = true;
        try
        {
            Pages = pages;
            Layout = layout;
            Zoom = zoom;
            ScrollOffset = offset;
        }
        finally
        {
            _applyingView = false;
        }

        Refresh(zoomChanged: false);
    }

    public void Dispose()
    {
        _zoomSettle.Dispose();
        _scrollSettle.Dispose();
        _scheduler.Dispose();
    }

    [RelayCommand]
    private void ZoomIn() => ApplyView(ZoomCalculator.ZoomIn(Zoom), ZoomMode.Custom, LayoutMode, ViewportCenter);

    [RelayCommand]
    private void ZoomOut() => ApplyView(ZoomCalculator.ZoomOut(Zoom), ZoomMode.Custom, LayoutMode, ViewportCenter);

    [RelayCommand]
    private void ZoomTo(double zoom) => ApplyView(ZoomCalculator.Clamp(zoom), ZoomMode.Custom, LayoutMode, ViewportCenter);

    [RelayCommand]
    private void ActualSize() => ZoomTo(1.0);

    [RelayCommand]
    private void FitWidth() => ApplyView(ComputeFitZoom(ZoomMode.FitWidth), ZoomMode.FitWidth, LayoutMode, ViewportCenter);

    [RelayCommand]
    private void FitPage()
    {
        var page = CurrentPageIndex;
        ApplyView(ComputeFitZoom(ZoomMode.FitPage), ZoomMode.FitPage, LayoutMode, ViewportCenter);
        NavigateTo(page, centerHorizontally: true);
    }

    [RelayCommand]
    private void SetLayoutMode(PageLayoutMode mode) => LayoutMode = mode;

    [RelayCommand]
    private void FirstPage() => NavigateTo(0);

    [RelayCommand]
    private void LastPage() => NavigateTo(PageCount - 1);

    [RelayCommand]
    private void NextPage()
    {
        var (first, count) = ZoomCalculator.RowOf(CurrentPageIndex, PageCount, LayoutMode);
        NavigateTo(first + count);
    }

    [RelayCommand]
    private void PreviousPage()
    {
        var (first, _) = ZoomCalculator.RowOf(CurrentPageIndex, PageCount, LayoutMode);
        if (first > 0)
        {
            NavigateTo(ZoomCalculator.RowOf(first - 1, PageCount, LayoutMode).First);
        }
        else
        {
            NavigateTo(0);
        }
    }

    /// <summary>Page box in the status bar: a 1-based number typed by the user; invalid input is ignored.</summary>
    [RelayCommand]
    private void GoToPageNumber(string? text)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
        {
            NavigateTo(number - 1);
        }

        // Refresh the box even when nothing changed, so invalid input is replaced by the current page.
        OnPropertyChanged(nameof(CurrentPageNumber));
    }

    partial void OnScrollOffsetChanged(ViewPoint value)
    {
        if (_applyingView)
        {
            return;
        }

        if (_pinnedOffset is { } pinned && (Math.Abs(pinned.X - value.X) > 0.5 || Math.Abs(pinned.Y - value.Y) > 0.5))
        {
            _pinnedPage = null;
            _pinnedOffset = null;
        }

        _scrollSettled = false;
        _scrollSettle.Schedule(ScrollSettleDelay, () =>
        {
            _scrollSettled = true;
            UpdateRenders();
        });
        Refresh(zoomChanged: false);
    }

    partial void OnViewportSizeChanged(ViewSize value)
    {
        if (value.Width <= 0 || value.Height <= 0)
        {
            return;
        }

        // Keep the page spot at the top-left of the viewport in place while the window is resized.
        var zoom = ZoomMode == ZoomMode.Custom ? Zoom : ComputeFitZoom(ZoomMode);
        ApplyView(zoom, ZoomMode, LayoutMode, new ViewPoint(0, 0));
    }

    partial void OnDpiScaleChanged(double value)
    {
        if (!_applyingView)
        {
            Refresh(zoomChanged: true);
        }
    }

    partial void OnLayoutModeChanged(PageLayoutMode value)
    {
        if (Layout is null)
        {
            return; // constructor
        }

        var page = CurrentPageIndex;
        var zoom = ZoomMode == ZoomMode.Custom ? Zoom : ComputeFitZoom(ZoomMode);
        ApplyView(zoom, ZoomMode, value, ViewportCenter);
        NavigateTo(page, centerHorizontally: true);
    }

    private double ComputeFitZoom(ZoomMode mode)
    {
        if (ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            return Zoom;
        }

        return mode == ZoomMode.FitPage
            ? ZoomCalculator.FitPage(_pageSizes, LayoutMode, CurrentPageIndex, ViewportSize)
            : ZoomCalculator.FitWidth(_pageSizes, LayoutMode, ViewportSize.Width);
    }

    private void ApplyView(double zoom, ZoomMode mode, PageLayoutMode layoutMode, ViewPoint anchorInViewport)
    {
        var before = Layout;
        var after = DocumentLayout.Create(_pageSizes, zoom, layoutMode, ViewportSize.Width);
        var offset = ViewportSize.Height > 0
            ? DocumentLayout.AnchoredOffset(before, after, ScrollOffset, anchorInViewport, ViewportSize)
            : after.ClampOffset(ScrollOffset, ViewportSize);
        var zoomChanged = Math.Abs(zoom - Zoom) > 1e-9 || layoutMode != before.Mode;

        _applyingView = true;
        try
        {
            Layout = after;
            Zoom = zoom;
            ZoomMode = mode;
            ScrollOffset = offset;
        }
        finally
        {
            _applyingView = false;
        }

        Refresh(zoomChanged);
    }

    private void Refresh(bool zoomChanged)
    {
        if (ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
        {
            return;
        }

        CurrentPageIndex = _pinnedPage ?? Layout.GetCurrentPage(Viewport) ?? 0;
        if (zoomChanged)
        {
            _zoomSettled = false;
            _zoomSettle.Schedule(ZoomSettleDelay, () =>
            {
                _zoomSettled = true;
                UpdateRenders();
            });
        }

        UpdateRenders();
    }

    private void UpdateRenders()
        => _scheduler.Update(Layout, Viewport, DpiScale, _zoomSettled, _zoomSettled && _scrollSettled);
}
