using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Editing;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// "Sửa nội dung" over one page of the viewer: a light frame around the drawing under the mouse, a solid frame around
/// the selected one, and a dashed frame where a drag has it. The page image shows the drawings themselves. Purely
/// visual: the mouse goes to the viewer, which hands it to <see cref="ContentEditorViewModel"/>.
/// </summary>
public sealed class ContentObjectLayer : FrameworkElement
{
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(ContentEditorViewModel), typeof(ContentObjectLayer), new PropertyMetadata(null, OnEditorChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(ContentObjectLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((ContentObjectLayer)d).InvalidateVisual()));

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
    private static readonly Pen HoverPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x78, 0xD4))), 1));
    private static readonly Pen SelectionPen = Freeze(new Pen(Accent, 1.5));
    private static readonly Pen MovePen = Freeze(new Pen(Accent, 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x78, 0xD4)));

    public ContentObjectLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Editor);
        Unloaded += (_, _) => Detach(Editor);
    }

    public ContentEditorViewModel? Editor
    {
        get => (ContentEditorViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Editor is not { IsActive: true } editor || ActualWidth <= 0)
        {
            return;
        }

        _ = editor.ObjectsOn(PageKey); // read in the background, so that the mouse can pick them
        if (editor.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        if (editor.Hovered is { } hovered && hovered.PageKey == PageKey && hovered.Object != editor.Selected?.Object)
        {
            drawingContext.DrawRectangle(null, HoverPen, Frame(mapper, hovered.Object.Bounds, 1));
        }

        if (editor.Selected is { } selected && selected.PageKey == PageKey)
        {
            drawingContext.DrawRectangle(SelectionFill, SelectionPen, Frame(mapper, selected.Object.Bounds, 2));
            var shown = editor.Shown(PageKey, selected);
            if (shown != selected.Object.Bounds)
            {
                drawingContext.DrawRectangle(null, MovePen, Frame(mapper, shown, 2));
            }
        }
    }

    private static Rect Frame(PageCoordinateMapper mapper, PdfRect bounds, double margin)
    {
        var shown = mapper.PdfRectToScreen(bounds);
        var frame = new Rect(shown.X, shown.Y, shown.Width, shown.Height);
        frame.Inflate(margin, margin);
        return frame;
    }

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (ContentObjectLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as ContentEditorViewModel);
            layer.Attach(e.NewValue as ContentEditorViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(ContentEditorViewModel? editor)
    {
        if (editor is not null)
        {
            editor.Changed -= OnChanged;
            editor.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(ContentEditorViewModel? editor)
    {
        if (editor is not null)
        {
            editor.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
