using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Controls;

/// <summary>
/// Multiple-selection list for the page organizer, on a <see cref="VirtualizingTilePanel"/>:
/// <list type="bullet">
/// <item>its selection is the <see cref="SelectionHost"/>'s: what the user selects is reported to it, and what it
/// selects (after an edit, from a page range) is shown, scrolled to and focused;</item>
/// <item>dragging on the empty space between tiles draws a selection rectangle (with Ctrl it adds to the selection),
/// scrolling when the mouse nears the top or bottom;</item>
/// <item>double-clicking a tile or pressing Enter runs <see cref="OpenItemCommand"/> with it.</item>
/// </list>
/// Click, Ctrl+click, Shift+click, arrow keys and Ctrl+A are the ListBox's own extended selection.
/// </summary>
public sealed class PageTileListBox : ListBox
{
    public static readonly DependencyProperty SelectionHostProperty = DependencyProperty.Register(
        nameof(SelectionHost),
        typeof(IItemSelectionHost),
        typeof(PageTileListBox),
        new PropertyMetadata(null, OnSelectionHostChanged));

    public static readonly DependencyProperty OpenItemCommandProperty = DependencyProperty.Register(
        nameof(OpenItemCommand),
        typeof(ICommand),
        typeof(PageTileListBox),
        new PropertyMetadata(null));

    /// <summary>Distance from the top or bottom edge where a selection rectangle scrolls the list.</summary>
    private const double AutoScrollEdge = 24;

    /// <summary>Smaller drags are clicks on the empty space (they only clear the selection).</summary>
    private const double MinimumBandSize = 3;

    private readonly DispatcherTimer _autoScroll;
    private bool _syncing;
    private Point? _bandStart;
    private Point _bandEnd;
    private HashSet<object> _bandBase = [];
    private BandAdorner? _band;

    public PageTileListBox()
    {
        SelectionMode = SelectionMode.Extended;
        _autoScroll = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _autoScroll.Tick += OnAutoScrollTick;
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnAnyMouseLeftButtonDown), handledEventsToo: true);
    }

    /// <summary>The view model whose selection this list shows and edits.</summary>
    public IItemSelectionHost? SelectionHost
    {
        get => (IItemSelectionHost?)GetValue(SelectionHostProperty);
        set => SetValue(SelectionHostProperty, value);
    }

    /// <summary>Runs with the item that was double-clicked or had focus when Enter was pressed.</summary>
    public ICommand? OpenItemCommand
    {
        get => (ICommand?)GetValue(OpenItemCommandProperty);
        set => SetValue(OpenItemCommandProperty, value);
    }

    /// <summary>A selection rectangle is being drawn (tests and diagnostics).</summary>
    internal bool IsDrawingBand => _bandStart is not null;

    /// <summary>Selects <paramref name="request"/>'s items without reporting it back, then shows and focuses its focus item.</summary>
    internal void ApplySelection(ItemSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = Items.Cast<object>().ToHashSet();
        var present = request.Items.Where(items.Contains).ToList();
        _syncing = true;
        try
        {
            if (present.Count == 0)
            {
                UnselectAll();
            }
            else
            {
                SetSelectedItems(present);
            }
        }
        finally
        {
            _syncing = false;
        }

        if (request.FocusItem is { } item && items.Contains(item))
        {
            ScrollIntoView(item);

            // The container may only exist after the scroll's layout pass.
            var focus = HasLostFocus();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SettleOn(item, focus));
        }
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!_syncing && _bandStart is null)
        {
            ReportSelection();
        }
    }

    protected override void OnItemsSourceChanged(System.Collections.IEnumerable oldValue, System.Collections.IEnumerable newValue)
    {
        base.OnItemsSourceChanged(oldValue, newValue);
        if (SelectionHost is { } host)
        {
            ApplySelection(host.CurrentSelection);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && FocusedItem() is { } item && Open(item))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDoubleClick(e);
        if (e.ChangedButton == MouseButton.Left && ContainerOf(e.OriginalSource as DependencyObject) is { } container
            && ItemContainerGenerator.ItemFromContainer(container) is { } item && item != DependencyProperty.UnsetValue && Open(item))
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_bandStart is not null && Panel() is { } panel)
        {
            _bandEnd = e.GetPosition(panel);
            UpdateBand(panel);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_bandStart is not null)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndBand();
    }

    private static void OnSelectionHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var list = (PageTileListBox)d;
        if (e.OldValue is IItemSelectionHost oldHost)
        {
            oldHost.SelectionRequested -= list.OnSelectionRequested;
        }

        if (e.NewValue is IItemSelectionHost newHost)
        {
            newHost.SelectionRequested += list.OnSelectionRequested;
            list.ApplySelection(newHost.CurrentSelection);
        }
    }

    private static ListBoxItem? ContainerOf(DependencyObject? source)
    {
        for (var node = source; node is not null and not PageTileListBox; node = ParentOf(node))
        {
            if (node is ListBoxItem item)
            {
                return item;
            }
        }

        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    /// <summary>
    /// Keyboard focus is in the list, or nowhere (its focused tile was removed by an edit): the focus item should get it.
    /// </summary>
    private bool HasLostFocus()
        => IsKeyboardFocusWithin
            || Keyboard.FocusedElement is null or Window
            || (Keyboard.FocusedElement is Visual focused && PresentationSource.FromVisual(focused) is null);

    private void OnSelectionRequested(object? sender, ItemSelectionRequest request) => ApplySelection(request);

    private void ReportSelection() => SelectionHost?.OnViewSelectionChanged(SelectedItems.Cast<object>().ToList());

    /// <summary>Shift+click and Shift+arrows extend from the focus item, which also gets keyboard focus if asked.</summary>
    private void SettleOn(object item, bool focus)
    {
        if (ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
        {
            return;
        }

        if (container.IsSelected)
        {
            AnchorItem = item; // valid only for a selected item with a container
        }

        if (focus)
        {
            container.Focus();
        }
    }

    private object? FocusedItem()
        => Keyboard.FocusedElement is DependencyObject focused && ContainerOf(focused) is { } container
            ? ItemContainerGenerator.ItemFromContainer(container)
            : null;

    private bool Open(object item)
    {
        if (OpenItemCommand is { } command && command.CanExecute(item))
        {
            command.Execute(item);
            return true;
        }

        return false;
    }

    private VirtualizingTilePanel? Panel() => FindPanel(this);

    private static VirtualizingTilePanel? FindPanel(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is VirtualizingTilePanel panel)
            {
                return panel;
            }

            if (FindPanel(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A press on the empty space between tiles (not on a tile or a scroll bar) starts a selection rectangle.</summary>
    private void OnAnyMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || _bandStart is not null || Panel() is not { } panel || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        for (var node = source; node is not null && !ReferenceEquals(node, this); node = ParentOf(node))
        {
            if (node is ListBoxItem or ScrollBar or ButtonBase)
            {
                return;
            }
        }

        if (!ReferenceEquals(source, panel) && !panel.IsAncestorOf(source))
        {
            return;
        }

        Focus();
        var additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _bandBase = additive ? [.. SelectedItems.Cast<object>()] : [];
        var start = e.GetPosition(panel);
        _bandStart = new Point(start.X, start.Y + panel.VerticalOffset);
        _bandEnd = start;
        if (!additive)
        {
            _syncing = true;
            try
            {
                UnselectAll();
            }
            finally
            {
                _syncing = false;
            }
        }

        CaptureMouse();
        _autoScroll.Start();
        e.Handled = true;
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        if (_bandStart is null || Panel() is not { } panel)
        {
            _autoScroll.Stop();
            return;
        }

        if (_bandEnd.Y < AutoScrollEdge)
        {
            panel.LineUp();
        }
        else if (_bandEnd.Y > panel.ViewportHeight - AutoScrollEdge)
        {
            panel.LineDown();
        }
        else
        {
            return;
        }

        UpdateBand(panel);
    }

    /// <summary>Selects the tiles the rectangle touches (plus the selection it started from, with Ctrl).</summary>
    private void UpdateBand(VirtualizingTilePanel panel)
    {
        if (_bandStart is not { } start)
        {
            return;
        }

        var layout = panel.Layout;
        var end = new Point(
            Math.Clamp(_bandEnd.X, 0, layout.Width),
            Math.Clamp(_bandEnd.Y + panel.VerticalOffset, 0, Math.Max(layout.Height, panel.ViewportHeight)));
        var area = new Rect(start, end);
        if (area.Width < MinimumBandSize && area.Height < MinimumBandSize)
        {
            return;
        }

        ShowBand(panel, area);
        var selected = new List<object>(_bandBase);
        foreach (var index in layout.IndicesIn(area))
        {
            if (index < Items.Count && Items[index] is { } item && !_bandBase.Contains(item))
            {
                selected.Add(item);
            }
        }

        _syncing = true;
        try
        {
            if (selected.Count == 0)
            {
                UnselectAll();
            }
            else
            {
                SetSelectedItems(selected);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowBand(VirtualizingTilePanel panel, Rect area)
    {
        if (_band is null && AdornerLayer.GetAdornerLayer(panel) is { } layer)
        {
            _band = new BandAdorner(panel);
            layer.Add(_band);
        }

        _band?.Show(area);
    }

    private void EndBand()
    {
        if (_bandStart is null)
        {
            return;
        }

        _bandStart = null;
        _bandBase = [];
        _autoScroll.Stop();
        if (_band is not null)
        {
            AdornerLayer.GetAdornerLayer(_band.AdornedElement)?.Remove(_band);
            _band = null;
        }

        ReportSelection();
    }

    /// <summary>The selection rectangle, drawn over the tiles in the accent colour.</summary>
    private sealed class BandAdorner : Adorner
    {
        private readonly VirtualizingTilePanel _panel;
        private Rect _area;

        public BandAdorner(VirtualizingTilePanel panel)
            : base(panel)
        {
            _panel = panel;
            IsHitTestVisible = false;
        }

        /// <summary>Draws <paramref name="area"/> (content coordinates: the panel's scroll offset is applied when drawing).</summary>
        public void Show(Rect area)
        {
            _area = area;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            ArgumentNullException.ThrowIfNull(drawingContext);
            var accent = TryFindResource("SystemAccentColor") as Color? ?? SystemColors.HighlightColor;
            var fill = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
            var stroke = new Pen(new SolidColorBrush(accent), 1);
            var shown = _area;
            shown.Offset(0, -_panel.VerticalOffset);
            drawingContext.PushClip(new RectangleGeometry(new Rect(_panel.RenderSize)));
            drawingContext.DrawRectangle(fill, stroke, shown);
            drawingContext.Pop();
        }
    }
}
