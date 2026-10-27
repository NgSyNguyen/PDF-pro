using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Forms;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// "Thiết kế biểu mẫu" over one page of the viewer: a tinted frame with the field's name for every widget, the selected
/// one in the accent colour with its four corner handles, a dashed frame where a drag moves or resizes it, and the box
/// of a field being placed. Purely visual: the mouse goes to the viewer, which hands it to
/// <see cref="FormDesignerViewModel"/>.
/// </summary>
public sealed class FormDesignLayer : FrameworkElement
{
    public static readonly DependencyProperty DesignerProperty = DependencyProperty.Register(
        nameof(Designer), typeof(FormDesignerViewModel), typeof(FormDesignLayer), new PropertyMetadata(null, OnDesignerChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(FormDesignLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((FormDesignLayer)d).InvalidateVisual()));

    private const double LabelSize = 10;

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
    private static readonly Brush FieldFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x4A, 0x90, 0xE2)));
    private static readonly Pen FieldPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x2F, 0x6F, 0xD6))), 1));
    private static readonly Pen SelectionPen = Freeze(new Pen(Accent, 2));
    private static readonly Pen DragPen = Freeze(new Pen(Accent, 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x2F, 0x6F, 0xD6)));
    private static readonly Typeface LabelFace = new(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public FormDesignLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Designer);
        Unloaded += (_, _) => Detach(Designer);
    }

    public FormDesignerViewModel? Designer
    {
        get => (FormDesignerViewModel?)GetValue(DesignerProperty);
        set => SetValue(DesignerProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Designer is not { IsActive: true } designer || ActualWidth <= 0 || designer.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var widget in designer.WidgetsOn(PageKey))
        {
            var frame = Frame(mapper, widget.Widget.Rect);
            var selected = widget.IsSame(designer.Selected);
            drawingContext.DrawRectangle(FieldFill, selected ? SelectionPen : FieldPen, frame);
            DrawLabel(drawingContext, widget.Field.Name, frame, dpi);
            if (!selected)
            {
                continue;
            }

            var shown = designer.Shown(widget);
            var target = shown == widget.Widget.Rect ? frame : Frame(mapper, shown);
            if (shown != widget.Widget.Rect)
            {
                drawingContext.DrawRectangle(null, DragPen, target);
            }

            foreach (var corner in new[] { target.TopLeft, target.TopRight, target.BottomLeft, target.BottomRight })
            {
                var size = FormDesignerViewModel.HandlePixels;
                drawingContext.DrawRectangle(Brushes.White, SelectionPen, new Rect(corner.X - size, corner.Y - size, 2 * size, 2 * size));
            }
        }

        if (designer.Preview(PageKey) is { } preview)
        {
            drawingContext.DrawRectangle(FieldFill, DragPen, Frame(mapper, preview));
        }
    }

    private static Rect Frame(PageCoordinateMapper mapper, PdfRect bounds)
    {
        var shown = mapper.PdfRectToScreen(bounds);
        return new Rect(shown.X, shown.Y, shown.Width, shown.Height);
    }

    /// <summary>The field's name on a small tag above its frame (inside it when there is no room above).</summary>
    private static void DrawLabel(DrawingContext drawingContext, string name, Rect frame, double dpi)
    {
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, LabelSize, Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(40, frame.Width + 60),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var top = frame.Top - text.Height - 2 >= 0 ? frame.Top - text.Height - 2 : frame.Top + 1;
        drawingContext.DrawRectangle(LabelBackground, null, new Rect(frame.Left, top, text.Width + 6, text.Height + 1));
        drawingContext.DrawText(text, new Point(frame.Left + 3, top));
    }

    private static void OnDesignerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (FormDesignLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as FormDesignerViewModel);
            layer.Attach(e.NewValue as FormDesignerViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(FormDesignerViewModel? designer)
    {
        if (designer is not null)
        {
            designer.Changed -= OnChanged;
            designer.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(FormDesignerViewModel? designer)
    {
        if (designer is not null)
        {
            designer.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
