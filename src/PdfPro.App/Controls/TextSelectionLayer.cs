using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// Draws the text selection over one page of the viewer: a translucent band per selected line, placed through
/// <see cref="PageCoordinateMapper"/>. Purely visual: the mouse goes through it to the viewer.
/// </summary>
public sealed class TextSelectionLayer : FrameworkElement
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(TextSelectionViewModel), typeof(TextSelectionLayer), new PropertyMetadata(null, OnSelectionChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(TextSelectionLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((TextSelectionLayer)d).InvalidateVisual()));

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x78, 0xD4)));

    public TextSelectionLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Selection);
        Unloaded += (_, _) => Detach(Selection);
    }

    public TextSelectionViewModel? Selection
    {
        get => (TextSelectionViewModel?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Selection is not { } selection || selection.GeometryOf(PageKey) is not { } geometry || ActualWidth <= 0)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        foreach (var rect in selection.RectsOn(PageKey))
        {
            var shown = mapper.PdfRectToScreen(rect);
            drawingContext.DrawRectangle(Fill, null, new Rect(shown.X, shown.Y, shown.Width, shown.Height));
        }
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (TextSelectionLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as TextSelectionViewModel);
            layer.Attach(e.NewValue as TextSelectionViewModel);
        }

        layer.InvalidateVisual();
    }

    private void Attach(TextSelectionViewModel? selection)
    {
        if (selection is not null)
        {
            selection.Changed -= OnChanged;
            selection.Changed += OnChanged;
        }
    }

    private void Detach(TextSelectionViewModel? selection)
    {
        if (selection is not null)
        {
            selection.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
