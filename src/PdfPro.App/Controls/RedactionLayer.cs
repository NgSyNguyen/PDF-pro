using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// "Xóa thông tin" over one page of the viewer: every mark as a red frame over a light shade (the selected one thicker),
/// and the box being dragged as a dashed frame. Shown while marks wait, also when the mode is off, so they are not
/// forgotten. Purely visual: the mouse goes to the viewer, which hands it to <see cref="RedactionViewModel"/>.
/// </summary>
public sealed class RedactionLayer : FrameworkElement
{
    public static readonly DependencyProperty RedactionProperty = DependencyProperty.Register(
        nameof(Redaction), typeof(RedactionViewModel), typeof(RedactionLayer), new PropertyMetadata(null, OnRedactionChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(RedactionLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((RedactionLayer)d).InvalidateVisual()));

    private static readonly Brush Shade = Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00)));
    private static readonly Pen MarkPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))), 1.5));
    private static readonly Pen SelectedPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))), 3));
    private static readonly Pen DragPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))), 1) { DashStyle = DashStyles.Dash });

    public RedactionLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Redaction);
        Unloaded += (_, _) => Detach(Redaction);
    }

    public RedactionViewModel? Redaction
    {
        get => (RedactionViewModel?)GetValue(RedactionProperty);
        set => SetValue(RedactionProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Redaction is not { } redaction || ActualWidth <= 0 || redaction.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        foreach (var mark in redaction.MarksOn(PageKey))
        {
            var pen = ReferenceEquals(mark, redaction.Selected) ? SelectedPen : MarkPen;
            foreach (var rect in mark.Rects)
            {
                drawingContext.DrawRectangle(Shade, pen, Frame(mapper, rect));
            }
        }

        if (redaction.Preview(PageKey) is { } preview)
        {
            drawingContext.DrawRectangle(Shade, DragPen, Frame(mapper, preview));
        }
    }

    private static Rect Frame(PageCoordinateMapper mapper, PdfRect bounds)
    {
        var shown = mapper.PdfRectToScreen(bounds);
        return new Rect(shown.X, shown.Y, shown.Width, shown.Height);
    }

    private static void OnRedactionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (RedactionLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as RedactionViewModel);
            layer.Attach(e.NewValue as RedactionViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(RedactionViewModel? redaction)
    {
        if (redaction is not null)
        {
            redaction.Changed -= OnChanged;
            redaction.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(RedactionViewModel? redaction)
    {
        if (redaction is not null)
        {
            redaction.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
