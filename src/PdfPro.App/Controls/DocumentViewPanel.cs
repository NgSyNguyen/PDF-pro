using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PdfPro.Core.Geometry;
using PdfPro.Core.Viewing;

namespace PdfPro.App.Controls;

/// <summary>
/// Scrolling, virtualizing surface for a <see cref="DocumentLayout"/> (host it in a ScrollViewer with
/// CanContentScroll="True"). Only the visible pages plus one page on each side get an element; elements are
/// recycled. Positions come exclusively from the layout, so scrolling, "go to page" and anchored zoom are exact
/// even when pages have different sizes. Pure view code: state lives in the bound view model.
/// </summary>
public sealed class DocumentViewPanel : Panel, IScrollInfo
{
    public static readonly DependencyProperty PageLayoutProperty = DependencyProperty.Register(
        nameof(PageLayout),
        typeof(DocumentLayout),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnScrollGeometryChanged));

    public static readonly DependencyProperty PagesProperty = DependencyProperty.Register(
        nameof(Pages),
        typeof(IList),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnPagesChanged));

    public static readonly DependencyProperty PageTemplateProperty = DependencyProperty.Register(
        nameof(PageTemplate),
        typeof(DataTemplate),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnPageTemplateChanged));

    /// <summary>Top-left of the viewport on the layout surface (two-way with the view model).</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset),
        typeof(ViewPoint),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(default(ViewPoint), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsMeasure, OnScrollGeometryChanged));

    /// <summary>
    /// Size of the viewport, owned by the panel (two-way: when the bound view model changes and brings another
    /// value, the panel writes the real size back).
    /// </summary>
    public static readonly DependencyProperty ViewportSizeProperty = DependencyProperty.Register(
        nameof(ViewportSize),
        typeof(ViewSize),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(default(ViewSize), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewStateChanged));

    /// <summary>Device pixels per view unit, owned by the panel (two-way, like <see cref="ViewportSize"/>).</summary>
    public static readonly DependencyProperty DpiScaleProperty = DependencyProperty.Register(
        nameof(DpiScale),
        typeof(double),
        typeof(DocumentViewPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewStateChanged));

    private const double LineSize = 40;

    private readonly Dictionary<int, ContentPresenter> _realized = [];
    private readonly Stack<ContentPresenter> _pool = new();
    private Size _viewport;
    private double _dpiScale = 1.0;

    public DocumentViewPanel()
    {
        Loaded += (_, _) =>
        {
            _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            SetCurrentValue(DpiScaleProperty, _dpiScale);
        };
    }

    public DocumentLayout? PageLayout
    {
        get => (DocumentLayout?)GetValue(PageLayoutProperty);
        set => SetValue(PageLayoutProperty, value);
    }

    public IList? Pages
    {
        get => (IList?)GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    public DataTemplate? PageTemplate
    {
        get => (DataTemplate?)GetValue(PageTemplateProperty);
        set => SetValue(PageTemplateProperty, value);
    }

    public ViewPoint Offset
    {
        get => (ViewPoint)GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public ViewSize ViewportSize
    {
        get => (ViewSize)GetValue(ViewportSizeProperty);
        set => SetValue(ViewportSizeProperty, value);
    }

    public double DpiScale
    {
        get => (double)GetValue(DpiScaleProperty);
        set => SetValue(DpiScaleProperty, value);
    }

    /// <summary>Pages that currently have an element (for UI tests and diagnostics).</summary>
    public IReadOnlyCollection<int> RealizedPages => _realized.Keys;

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; } = true;

    public double ExtentWidth => PageLayout?.Extent.Width ?? 0;

    public double ExtentHeight => PageLayout?.Extent.Height ?? 0;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => Offset.X;

    public double VerticalOffset => Offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineSize);

    public void LineDown() => SetVerticalOffset(VerticalOffset + LineSize);

    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - LineSize);

    public void LineRight() => SetHorizontalOffset(HorizontalOffset + LineSize);

    public void PageUp() => SetVerticalOffset(VerticalOffset - (ViewportHeight * 0.9));

    public void PageDown() => SetVerticalOffset(VerticalOffset + (ViewportHeight * 0.9));

    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - (ViewportWidth * 0.9));

    public void PageRight() => SetHorizontalOffset(HorizontalOffset + (ViewportWidth * 0.9));

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - WheelSize);

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + WheelSize);

    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - WheelSize);

    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + WheelSize);

    public void SetHorizontalOffset(double offset) => SetOffset(new ViewPoint(offset, VerticalOffset));

    public void SetVerticalOffset(double offset) => SetOffset(new ViewPoint(HorizontalOffset, offset));

    /// <summary>Keyboard focus moving between pages must not scroll the document.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    protected override Size MeasureOverride(Size availableSize)
    {
        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        if (viewport != _viewport)
        {
            _viewport = viewport;
            SetCurrentValue(ViewportSizeProperty, new ViewSize(viewport.Width, viewport.Height));
        }

        var layout = PageLayout;
        var pages = Pages;
        if (layout is null || pages is null || layout.PageCount != pages.Count)
        {
            RecycleAll();
            ScrollOwner?.InvalidateScrollInfo();
            return viewport;
        }

        var offset = Clamp(Offset);
        if (offset != Offset)
        {
            SetCurrentValue(OffsetProperty, offset);
        }

        var wanted = new HashSet<int>();
        if (layout.GetVisiblePages(new ViewRect(offset.X, offset.Y, viewport.Width, viewport.Height)) is { } range)
        {
            for (var i = Math.Max(0, range.First - 1); i <= Math.Min(pages.Count - 1, range.Last + 1); i++)
            {
                wanted.Add(i);
            }
        }

        foreach (var index in _realized.Keys.Where(i => !wanted.Contains(i)).ToList())
        {
            Recycle(index);
        }

        foreach (var index in wanted)
        {
            if (!_realized.TryGetValue(index, out var presenter))
            {
                presenter = _pool.Count > 0 ? _pool.Pop() : new ContentPresenter();
                presenter.ContentTemplate = PageTemplate;
                presenter.Content = pages[index];
                InternalChildren.Add(presenter);
                _realized[index] = presenter;
            }

            var bounds = layout.GetPageBounds(index);
            presenter.Measure(new Size(bounds.Width, bounds.Height));
        }

        ScrollOwner?.InvalidateScrollInfo();
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = PageLayout;
        if (layout is null)
        {
            return finalSize;
        }

        var offset = Clamp(Offset);
        foreach (var (index, presenter) in _realized)
        {
            if (index >= layout.PageCount)
            {
                continue;
            }

            var bounds = layout.GetPageBounds(index);
            presenter.Arrange(new Rect(bounds.X - offset.X, bounds.Y - offset.Y, bounds.Width, bounds.Height));
        }

        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        SetCurrentValue(DpiScaleProperty, _dpiScale);
    }

    /// <summary>
    /// TabControl reuses one view for every document tab and only swaps the DataContext, so a newly bound view model
    /// pushes its own (stale) viewport size or DPI into the panel. The panel owns these values: write the real ones
    /// back, which the two-way binding forwards to the new view model.
    /// </summary>
    private static void OnViewStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (DocumentViewPanel)d;
        var actualSize = new ViewSize(panel._viewport.Width, panel._viewport.Height);
        var sizeIsStale = e.Property == ViewportSizeProperty && actualSize != default && (ViewSize)e.NewValue != actualSize;
        var dpiIsStale = e.Property == DpiScaleProperty && Math.Abs((double)e.NewValue - panel._dpiScale) > 1e-6;
        if (sizeIsStale || dpiIsStale)
        {
            panel.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                panel.SetCurrentValue(ViewportSizeProperty, new ViewSize(panel._viewport.Width, panel._viewport.Height));
                panel.SetCurrentValue(DpiScaleProperty, panel._dpiScale);
            });
        }
    }

    private static double WheelSize => Math.Max(1, SystemParameters.WheelScrollLines) * LineSize;

    private static void OnScrollGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (DocumentViewPanel)d;
        panel.InvalidateArrange();
        panel.ScrollOwner?.InvalidateScrollInfo();
    }

    private static void OnPagesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DocumentViewPanel)d).RecycleAll();

    private static void OnPageTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (DocumentViewPanel)d;
        foreach (var presenter in panel._realized.Values)
        {
            presenter.ContentTemplate = (DataTemplate?)e.NewValue;
        }
    }

    private ViewPoint Clamp(ViewPoint offset)
    {
        var layout = PageLayout;
        return layout is null
            ? new ViewPoint(0, 0)
            : layout.ClampOffset(offset, new ViewSize(_viewport.Width, _viewport.Height));
    }

    private void SetOffset(ViewPoint requested)
    {
        var clamped = Clamp(requested);
        if (clamped != Offset)
        {
            SetCurrentValue(OffsetProperty, clamped);
        }
    }

    private void Recycle(int index)
    {
        if (_realized.Remove(index, out var presenter))
        {
            InternalChildren.Remove(presenter);
            presenter.Content = null;
            _pool.Push(presenter);
        }
    }

    private void RecycleAll()
    {
        foreach (var index in _realized.Keys.ToList())
        {
            Recycle(index);
        }
    }
}
