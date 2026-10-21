using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PdfPro.App.Controls;

/// <summary>
/// Virtualizing grid of equal tiles for a <see cref="ListBox"/> (the page organizer): items flow left to right, then
/// top to bottom, in cells of <see cref="TileWidth"/> × <see cref="TileHeight"/>; the panel scrolls vertically and only
/// the rows in view (plus one above and below) have containers, which are recycled, so a thousand-page document costs
/// the same as a ten-page one. Changing the tile size keeps the first visible tile in view. Layout math:
/// <see cref="TileLayout"/>.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth),
        typeof(double),
        typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTileSizeChanged),
        IsValidSize);

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight),
        typeof(double),
        typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnTileSizeChanged),
        IsValidSize);

    private const int ExtraRows = 1;

    private TileLayout _layout = new(0, 200, 280, 200);
    private Size _extent;
    private Size _viewport;
    private double _offset;
    private int? _anchorIndex;

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    /// <summary>The current layout (rubber band selection, tests).</summary>
    public TileLayout Layout => _layout;

    public bool CanVerticallyScroll { get; set; }

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offset;

    public ScrollViewer? ScrollOwner { get; set; }

    private double LineDelta => Math.Max(16, TileHeight / 6);

    public void LineUp() => SetVerticalOffset(_offset - LineDelta);

    public void LineDown() => SetVerticalOffset(_offset + LineDelta);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(_offset - (3 * LineDelta));

    public void MouseWheelDown() => SetVerticalOffset(_offset + (3 * LineDelta));

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset))
        {
            return;
        }

        var clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(clamped - _offset) < 0.01)
        {
            return;
        }

        _offset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <summary>Scrolls the tile holding <paramref name="visual"/> into view (keyboard focus, BringIntoView).</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is null || rectangle.IsEmpty || ReferenceEquals(visual, this) || !IsAncestorOf(visual))
        {
            return Rect.Empty;
        }

        DependencyObject node = visual;
        while (VisualTreeHelper.GetParent(node) is { } parent && !ReferenceEquals(parent, this))
        {
            node = parent;
        }

        if (node is UIElement child && InternalChildren.IndexOf(child) is var childIndex and >= 0)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (index >= 0 && index < _layout.Count)
            {
                SetVerticalOffset(_layout.OffsetToShow(index, _offset, _viewport.Height));
            }
        }

        return visual.TransformToAncestor(this).TransformBounds(rectangle);
    }

    /// <summary>Scrolls to tile <paramref name="index"/> and makes sure it has a container.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _layout.Count)
        {
            return;
        }

        SetVerticalOffset(_layout.OffsetToShow(index, _offset, _viewport.Height));
        InvalidateMeasure();
        UpdateLayout();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Reading InternalChildren first connects the panel to its items owner's generator.
        _ = InternalChildren;
        var owner = ItemsControl.GetItemsOwner(this);
        var count = owner?.HasItems == true ? owner.Items.Count : 0;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : TileWidth;
        _layout = new TileLayout(count, TileWidth, TileHeight, width);
        var viewport = new Size(width, double.IsFinite(availableSize.Height) ? availableSize.Height : _layout.Height);
        if (_anchorIndex is { } anchor && anchor < count)
        {
            _offset = _layout.TileRect(anchor).Top;
        }

        _anchorIndex = null;
        UpdateScrollInfo(viewport);

        var (first, last) = _layout.RangeFor(_offset, viewport.Height, ExtraRows);
        Realize(first, last);
        CleanUp(first, last);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0 || index >= _layout.Count)
            {
                continue;
            }

            var tile = _layout.TileRect(index);
            tile.Offset(0, -_offset);
            children[i].Arrange(tile);
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    private static bool IsValidSize(object value) => value is double size && double.IsFinite(size) && size > 0;

    private static void OnTileSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (VirtualizingTilePanel)d;
        if (panel._layout.Count > 0)
        {
            panel._anchorIndex = panel._layout.RangeFor(panel._offset, 0).First;
        }
    }

    private void UpdateScrollInfo(Size viewport)
    {
        var extent = new Size(viewport.Width, _layout.Height);
        var offset = Math.Clamp(_offset, 0, Math.Max(0, extent.Height - viewport.Height));
        if (extent != _extent || viewport != _viewport || offset != _offset)
        {
            _extent = extent;
            _viewport = viewport;
            _offset = offset;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    /// <summary>Creates (or reuses recycled) containers for the tiles first…last, in item order.</summary>
    private void Realize(int first, int last)
    {
        if (last < first)
        {
            return;
        }

        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                if (generator.GenerateNext(out _) is not UIElement child)
                {
                    break;
                }

                // A container already in place is a realized item. Anything else is new, or a recycled container,
                // which the generator reports as not newly realized although it is no longer a child.
                if (childIndex >= children.Count || !ReferenceEquals(children[childIndex], child))
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(TileWidth, TileHeight));
            }
        }
    }

    /// <summary>
    /// Recycles the containers outside first…last, except the one holding keyboard focus (the list would lose focus
    /// and keyboard navigation would start over).
    /// </summary>
    private void CleanUp(int first, int last)
    {
        var generator = ItemContainerGenerator;
        var recycling = VirtualizingPanel.GetVirtualizationMode(ItemsControl.GetItemsOwner(this) ?? (DependencyObject)this) == VirtualizationMode.Recycling
            ? generator as IRecyclingItemContainerGenerator
            : null;
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last || (index >= 0 && children[i].IsKeyboardFocusWithin))
            {
                continue;
            }

            if (recycling is not null)
            {
                recycling.Recycle(position, 1);
            }
            else
            {
                generator.Remove(position, 1);
            }

            RemoveInternalChildRange(i, 1);
        }
    }
}
