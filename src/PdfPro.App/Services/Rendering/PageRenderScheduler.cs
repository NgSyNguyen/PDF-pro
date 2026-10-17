using System.Windows;
using Microsoft.Extensions.Logging;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;
using PdfPro.Pdfium;

namespace PdfPro.App.Services.Rendering;

/// <summary>
/// Decides what to render for one document view (SPEC "Luồng xử lý & đa luồng"):
/// <list type="bullet">
/// <item>only the visible pages plus <see cref="BufferPages"/> on each side; visible pages first;</item>
/// <item>renders of pages that scrolled away are cancelled and their images released to the cache;</item>
/// <item>while the zoom is still changing, pages keep their old image (scaled) and only blank pages render;</item>
/// <item>a page bigger than <see cref="MaxFullPagePixels"/> gets a reduced full image plus a sharp tile of the
/// visible part (requested once scrolling pauses), so memory stays bounded at any zoom.</item>
/// </list>
/// Must be used on the UI thread.
/// </summary>
public sealed class PageRenderScheduler : IDisposable
{
    public const int BufferPages = 1;
    public const long MaxFullPagePixels = 16_000_000;

    /// <summary>Extra area around the visible part included in a detail tile, as a share of the viewport.</summary>
    private const double DetailMargin = 0.25;

    /// <summary>Detail regions snap to this grid so small scrolls reuse the same tile.</summary>
    private const int DetailGrid = 128;

    private readonly IPdfDocument _document;
    private readonly IReadOnlyList<PageViewModel> _pages;
    private readonly IPageImageRenderer _renderer;
    private readonly RenderCache _cache;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly Dictionary<(int Page, bool Detail), InFlight> _inFlight = [];
    private readonly HashSet<int> _failureLogged = [];
    private readonly HashSet<int> _realized = [];
    private DocumentLayout? _layout;
    private bool _disposed;

    public PageRenderScheduler(IPdfDocument document, IReadOnlyList<PageViewModel> pages, IPageImageRenderer renderer, RenderCache cache, IUiDispatcher ui, ILogger logger)
    {
        _document = document;
        _pages = pages;
        _renderer = renderer;
        _cache = cache;
        _ui = ui;
        _logger = logger;
    }

    /// <summary>Number of renders currently queued or running (for tests and diagnostics).</summary>
    public int PendingCount => _inFlight.Count;

    /// <param name="layout">Current layout (zoom, page positions).</param>
    /// <param name="viewport">Visible part of the layout surface.</param>
    /// <param name="dpiScale">Device pixels per view unit.</param>
    /// <param name="zoomSettled">False while the user is still zooming: keep scaled images, render only blank pages.</param>
    /// <param name="detailAllowed">True once scrolling has paused: detail tiles may be requested.</param>
    public void Update(DocumentLayout layout, ViewRect viewport, double dpiScale, bool zoomSettled, bool detailAllowed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (_disposed || layout.PageCount != _pages.Count)
        {
            return;
        }

        _layout = layout;
        var deviceScale = layout.Scale * Math.Max(dpiScale, 0.25);
        var visible = layout.GetVisiblePages(viewport);
        var wanted = new HashSet<int>();
        if (visible is { } range)
        {
            for (var i = Math.Max(0, range.First - BufferPages); i <= Math.Min(_pages.Count - 1, range.Last + BufferPages); i++)
            {
                wanted.Add(i);
            }
        }

        foreach (var page in _realized.Where(p => !wanted.Contains(p)).ToList())
        {
            Release(page);
        }

        foreach (var index in wanted.OrderBy(i => visible is { } r && i >= r.First && i <= r.Last ? 0 : 1).ThenBy(i => i))
        {
            _realized.Add(index);
            var isVisible = visible is { } r2 && index >= r2.First && index <= r2.Last;
            UpdatePage(index, layout, viewport, deviceScale, dpiScale, isVisible, zoomSettled, detailAllowed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var inFlight in _inFlight.Values)
        {
            inFlight.Cancellation.Cancel();
            inFlight.Cancellation.Dispose();
        }

        _inFlight.Clear();
    }

    private void UpdatePage(int index, DocumentLayout layout, ViewRect viewport, double deviceScale, double dpiScale, bool isVisible, bool zoomSettled, bool detailAllowed)
    {
        var page = _pages[index];
        var size = _document.PageSizes[index];
        var fullPixels = (long)PixelSize.FromPoints(size.Width, deviceScale) * PixelSize.FromPoints(size.Height, deviceScale);
        var needsDetail = fullPixels > MaxFullPagePixels;
        var baseScale = needsDetail ? deviceScale * Math.Sqrt((double)MaxFullPagePixels / fullPixels) : deviceScale;
        var baseKey = new RenderKey(_document.Id, index, RenderKey.ToScaleKey(baseScale));

        if (page.ImageKey != baseKey)
        {
            if (_cache.TryGet(baseKey, out var cached))
            {
                page.Image = cached;
                page.ImageKey = baseKey;
                page.RenderFailed = false;
            }
            else if (zoomSettled || page.Image is null)
            {
                Request(index, detail: false, baseKey, baseScale, region: null, isVisible ? PdfiumPriority.High : PdfiumPriority.Normal);
            }
        }

        var bounds = layout.GetPageBounds(index);
        if (!needsDetail)
        {
            CancelRequest(index, detail: true);
            if (page.DetailImage is not null)
            {
                page.ClearDetail();
            }

            return;
        }

        // The detail tile (if any) follows the page when the zoom changes; it is replaced when it no longer fits.
        if (page.DetailImage is not null)
        {
            var stale = page.DetailKey is { } key && key.ScaleKey != RenderKey.ToScaleKey(deviceScale);
            if (stale && zoomSettled)
            {
                page.ClearDetail();
            }
            else
            {
                page.DetailBounds = Scale(page.DetailFraction, bounds.Width, bounds.Height);
            }
        }

        if (!isVisible || !zoomSettled || !detailAllowed)
        {
            return;
        }

        var visiblePart = ViewRect.Intersect(bounds, viewport);
        if (visiblePart.IsEmpty)
        {
            return;
        }

        var fullWidth = PixelSize.FromPoints(size.Width, deviceScale);
        var fullHeight = PixelSize.FromPoints(size.Height, deviceScale);
        var region = DetailRegion(visiblePart.Offset(-bounds.X, -bounds.Y), viewport, dpiScale, fullWidth, fullHeight);
        var detailKey = new RenderKey(_document.Id, index, RenderKey.ToScaleKey(deviceScale), region);
        if (page.DetailKey is { Region: { } current } existing && existing.ScaleKey == detailKey.ScaleKey
            && current.Contains(ToPixels(visiblePart.Offset(-bounds.X, -bounds.Y), dpiScale, fullWidth, fullHeight)))
        {
            return; // the current tile still covers what is on screen
        }

        if (_cache.TryGet(detailKey, out var cachedDetail))
        {
            ApplyDetail(page, detailKey, cachedDetail, region, fullWidth, fullHeight);
            return;
        }

        Request(index, detail: true, detailKey, deviceScale, region, PdfiumPriority.High);
    }

    private static PixelRect DetailRegion(ViewRect visibleInPage, ViewRect viewport, double dpiScale, int fullWidth, int fullHeight)
    {
        var marginX = viewport.Width * DetailMargin;
        var marginY = viewport.Height * DetailMargin;
        var left = Snap(Math.Floor((visibleInPage.X - marginX) * dpiScale), down: true);
        var top = Snap(Math.Floor((visibleInPage.Y - marginY) * dpiScale), down: true);
        var right = Snap(Math.Ceiling((visibleInPage.Right + marginX) * dpiScale), down: false);
        var bottom = Snap(Math.Ceiling((visibleInPage.Bottom + marginY) * dpiScale), down: false);
        left = Math.Clamp(left, 0, fullWidth - 1);
        top = Math.Clamp(top, 0, fullHeight - 1);
        right = Math.Clamp(right, left + 1, fullWidth);
        bottom = Math.Clamp(bottom, top + 1, fullHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static PixelRect ToPixels(ViewRect rect, double dpiScale, int fullWidth, int fullHeight)
    {
        var left = Math.Clamp((int)Math.Floor(rect.X * dpiScale), 0, fullWidth);
        var top = Math.Clamp((int)Math.Floor(rect.Y * dpiScale), 0, fullHeight);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * dpiScale), left, fullWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * dpiScale), top, fullHeight);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static int Snap(double value, bool down)
        => (int)(down ? Math.Floor(value / DetailGrid) * DetailGrid : Math.Ceiling(value / DetailGrid) * DetailGrid);

    private static Rect Scale(Rect fraction, double width, double height)
        => fraction.IsEmpty ? Rect.Empty : new Rect(fraction.X * width, fraction.Y * height, fraction.Width * width, fraction.Height * height);

    private void ApplyDetail(PageViewModel page, RenderKey key, System.Windows.Media.ImageSource image, PixelRect region, int fullWidth, int fullHeight)
    {
        page.DetailImage = image;
        page.DetailKey = key;
        page.DetailFraction = new Rect(
            (double)region.X / fullWidth,
            (double)region.Y / fullHeight,
            (double)region.Width / fullWidth,
            (double)region.Height / fullHeight);
        if (_layout is { } layout)
        {
            var bounds = layout.GetPageBounds(page.Index);
            page.DetailBounds = Scale(page.DetailFraction, bounds.Width, bounds.Height);
        }
    }

    private void Request(int index, bool detail, RenderKey key, double scale, PixelRect? region, PdfiumPriority priority)
    {
        var slot = (index, detail);
        if (_inFlight.TryGetValue(slot, out var existing))
        {
            if (existing.Key == key)
            {
                return;
            }

            existing.Cancellation.Cancel();
            existing.Cancellation.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _inFlight[slot] = new InFlight(key, cancellation);
        _ = RenderAsync(index, detail, key, scale, region, priority, cancellation.Token);
    }

    private async Task RenderAsync(int index, bool detail, RenderKey key, double scale, PixelRect? region, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        try
        {
            var rendered = await _renderer.RenderAsync(_document, index, scale, region, priority, cancellationToken).ConfigureAwait(false);
            _cache.Add(key, rendered.Image, rendered.ByteSize);
            _ui.Post(() => Complete(index, detail, key, rendered));
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or zoomed again: nothing to show.
        }
        catch (ObjectDisposedException)
        {
            // The document was closed while the render was queued.
        }
        catch (Exception ex)
        {
            _ui.Post(() => Fail(index, detail, key, ex));
        }
    }

    private void Complete(int index, bool detail, RenderKey key, RenderedPageImage rendered)
    {
        if (_disposed || !_inFlight.TryGetValue((index, detail), out var inFlight) || inFlight.Key != key)
        {
            return;
        }

        _inFlight.Remove((index, detail));
        inFlight.Cancellation.Dispose();
        if (!_realized.Contains(index))
        {
            return;
        }

        var page = _pages[index];
        if (detail)
        {
            ApplyDetail(page, key, rendered.Image, rendered.Region, rendered.PagePixelWidth, rendered.PagePixelHeight);
        }
        else
        {
            page.Image = rendered.Image;
            page.ImageKey = key;
            page.RenderFailed = false;
        }
    }

    private void Fail(int index, bool detail, RenderKey key, Exception error)
    {
        if (_inFlight.TryGetValue((index, detail), out var inFlight) && inFlight.Key == key)
        {
            _inFlight.Remove((index, detail));
            inFlight.Cancellation.Dispose();
        }

        if (_failureLogged.Add(index))
        {
            _logger.LogWarning(error, "Rendering page {PageNumber} failed", index + 1);
        }

        if (!detail && !_disposed)
        {
            _pages[index].RenderFailed = true;
        }
    }

    private void Release(int index)
    {
        CancelRequest(index, detail: false);
        CancelRequest(index, detail: true);
        _pages[index].ClearImages();
        _realized.Remove(index);
    }

    private void CancelRequest(int index, bool detail)
    {
        if (_inFlight.Remove((index, detail), out var inFlight))
        {
            inFlight.Cancellation.Cancel();
            inFlight.Cancellation.Dispose();
        }
    }

    private sealed record InFlight(RenderKey Key, CancellationTokenSource Cancellation);
}
