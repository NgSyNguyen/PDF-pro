using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.ViewModels.Security;

/// <summary>Where the user put a visible signature: the page (0-based, as shown) and its box in the page's PDF space.</summary>
public sealed record SignatureBox(int PageIndex, Guid PageKey, PdfRect Rect);

/// <summary>
/// "Ký số" → "Kéo khung trên trang": the viewer's mouse places the box of a visible signature, once. A drag gives the
/// box; a click gives a box of the usual size around the point (upright as the page is shown). The box is kept inside
/// the page. Esc, another mode taking the viewer, or closing the document gives nothing. Must be used on the UI thread.
/// </summary>
public sealed partial class SignaturePlacementViewModel : ObservableObject
{
    /// <summary>The box a click gives, as it shows on the page (points).</summary>
    public const double DefaultWidth = 180;

    public const double DefaultHeight = 60;

    /// <summary>A drag smaller than this (points) counts as a click.</summary>
    public const double MinimumSize = 12;

    private readonly DocumentViewerViewModel _viewer;
    private readonly Func<Guid, PageGeometry?> _geometryOf;
    private TaskCompletionSource<SignatureBox?>? _pending;
    private (Guid PageKey, PdfPoint Start, PdfPoint End)? _drag;

    /// <param name="viewer">The viewer whose page gets the signature.</param>
    /// <param name="geometryOf">The PDF space of a page as it is written, when known.</param>
    public SignaturePlacementViewModel(DocumentViewerViewModel viewer, Func<Guid, PageGeometry?> geometryOf)
    {
        _viewer = viewer;
        _geometryOf = geometryOf;
    }

    /// <summary>The box being dragged changed: pages redraw it.</summary>
    public event EventHandler? Changed;

    /// <summary>A box is awaited: the viewer's mouse places it.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>A box is being dragged (the mouse is captured for it).</summary>
    public bool IsDragging => _drag is not null;

    public PageGeometry? GeometryOf(Guid pageKey) => _geometryOf(pageKey);

    /// <summary>The box being dragged on a page, or null.</summary>
    public PdfRect? Preview(Guid pageKey)
        => _drag is { } drag && drag.PageKey == pageKey ? Box(drag.Start, drag.End) : null;

    /// <summary>Waits for the box: it is the user's next drag or click on a page, or null when placing is given up.</summary>
    public Task<SignatureBox?> PlaceAsync()
    {
        Cancel();
        var pending = new TaskCompletionSource<SignatureBox?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        IsActive = true;
        return pending.Task;
    }

    /// <summary>A press on the viewer starts the box when it is on a page. False when the press is not for placing.</summary>
    public bool Press(ViewPoint viewport)
    {
        if (!IsActive || Locate(viewport) is not { } found)
        {
            return false;
        }

        _drag = (found.PageKey, found.Point, found.Point);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DragTo(ViewPoint viewport)
    {
        if (_drag is not { } drag || PointOn(_viewer.Document.IndexOf(drag.PageKey), viewport) is not { } point)
        {
            return;
        }

        _drag = drag with { End = point };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The end of the drag: the box (or the usual box around a click), kept inside its page, is the answer.</summary>
    public void Release()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        var index = _viewer.Document.IndexOf(drag.PageKey);
        if (index < 0 || _geometryOf(drag.PageKey) is not { } geometry)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var box = Box(drag.Start, drag.End);
        box = box.Width < MinimumSize || box.Height < MinimumSize ? Around(drag.Start, geometry) : Clip(box, geometry.Box);
        Finish(new SignatureBox(index, drag.PageKey, box));
    }

    /// <summary>Esc, another mode, or the document closing: placing is given up (the answer is null).</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (_pending is null && _drag is null)
        {
            return;
        }

        _drag = null;
        Finish(null);
    }

    /// <summary>The usual box centred on a point, upright as the page is shown, moved inside the page.</summary>
    internal static PdfRect Around(PdfPoint point, PageGeometry geometry)
    {
        var sideways = geometry.RotationDegrees is 90 or 270;
        var page = geometry.Box;
        var width = Math.Min(sideways ? DefaultHeight : DefaultWidth, page.Width);
        var height = Math.Min(sideways ? DefaultWidth : DefaultHeight, page.Height);
        var left = Math.Clamp(point.X - (width / 2), page.Left, page.Right - width);
        var bottom = Math.Clamp(point.Y - (height / 2), page.Bottom, page.Top - height);
        return new PdfRect(left, bottom, left + width, bottom + height);
    }

    private void Finish(SignatureBox? box)
    {
        var pending = _pending;
        _pending = null;
        IsActive = false;
        Changed?.Invoke(this, EventArgs.Empty);
        pending?.TrySetResult(box);
    }

    private static PdfRect Box(PdfPoint a, PdfPoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static PdfRect Clip(PdfRect rect, PdfRect bounds)
        => new(Math.Max(rect.Left, bounds.Left), Math.Max(rect.Bottom, bounds.Bottom), Math.Min(rect.Right, bounds.Right), Math.Min(rect.Top, bounds.Top));

    private (Guid PageKey, PdfPoint Point)? Locate(ViewPoint viewport)
    {
        var extent = new ViewPoint(viewport.X + _viewer.ScrollOffset.X, viewport.Y + _viewer.ScrollOffset.Y);
        if (_viewer.Layout.GetPageAt(extent) is not { } index || index < 0 || index >= _viewer.PageCount)
        {
            return null;
        }

        return PointOn(index, viewport) is { } point ? (_viewer.Document.Pages[index].Key, point) : null;
    }

    /// <summary>A viewport point on page <paramref name="index"/> (even outside it), in its PDF space.</summary>
    private PdfPoint? PointOn(int index, ViewPoint viewport)
    {
        if (index < 0 || index >= _viewer.PageCount || _geometryOf(_viewer.Document.Pages[index].Key) is not { } geometry)
        {
            return null;
        }

        var bounds = _viewer.Layout.GetPageBounds(index);
        var mapper = new PageCoordinateMapper(geometry, _viewer.Layout.Scale);
        return mapper.ScreenToPdf(new ViewPoint(viewport.X + _viewer.ScrollOffset.X - bounds.X, viewport.Y + _viewer.ScrollOffset.Y - bounds.Y));
    }
}
