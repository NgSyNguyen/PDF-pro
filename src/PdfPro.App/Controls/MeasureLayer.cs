using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;
using PdfPro.Core.Measuring;

namespace PdfPro.App.Controls;

/// <summary>
/// "Đo" over one page of the viewer: each measurement as a blue line or polygon with its corners and its value, and the
/// one being made (to the mouse) dashed. Shown while the tool is on. Purely visual: the mouse goes to the viewer, which
/// hands it to <see cref="MeasureViewModel"/>.
/// </summary>
public sealed class MeasureLayer : FrameworkElement
{
    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(
        nameof(Tool), typeof(MeasureViewModel), typeof(MeasureLayer), new PropertyMetadata(null, OnToolChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(MeasureLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((MeasureLayer)d).InvalidateVisual()));

    private static readonly Brush InkBrush = Freeze(new SolidColorBrush(Color.FromRgb(MeasureViewModel.Ink.R, MeasureViewModel.Ink.G, MeasureViewModel.Ink.B)));
    private static readonly Brush AreaFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, MeasureViewModel.Ink.R, MeasureViewModel.Ink.G, MeasureViewModel.Ink.B)));
    private static readonly Brush LabelBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen LinePen = Freeze(new Pen(InkBrush, 1.5));
    private static readonly Pen PreviewPen = Freeze(new Pen(InkBrush, 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public MeasureLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Tool);
        Unloaded += (_, _) => Detach(Tool);
    }

    /// <summary>The measuring tool of the tab.</summary>
    public MeasureViewModel? Tool
    {
        get => (MeasureViewModel?)GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Tool is not { IsActive: true } measure || ActualWidth <= 0 || measure.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        foreach (var measurement in measure.MeasurementsOn(PageKey))
        {
            Draw(drawingContext, mapper, measure, measurement, LinePen);
        }

        if (measure.PreviewOn(PageKey) is { } preview)
        {
            Draw(drawingContext, mapper, measure, preview, PreviewPen);
        }
    }

    private static void Draw(DrawingContext drawingContext, PageCoordinateMapper mapper, MeasureViewModel measure, Measurement measurement, Pen pen)
    {
        var points = measurement.Points.Select(p => ToPoint(mapper.PdfToScreen(p))).ToList();
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count > 1)
        {
            var figure = new PathFigure { StartPoint = points[0], IsClosed = measurement.Kind == MeasureKind.Area && points.Count > 2 };
            figure.Segments.Add(new PolyLineSegment(points.Skip(1), isStroked: true));
            var geometry = new PathGeometry([figure]);
            drawingContext.DrawGeometry(measurement.Kind == MeasureKind.Area ? AreaFill : null, pen, geometry);
        }

        foreach (var point in points)
        {
            drawingContext.DrawEllipse(Brushes.White, LinePen, point, 3, 3);
        }

        var label = measure.LabelOf(measurement);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 12, InkBrush, 1.0);
        var at = ToPoint(mapper.PdfToScreen(MeasureViewModel.LabelPoint(measurement)));
        var origin = new Point(at.X - (text.Width / 2), at.Y - text.Height - 6);
        drawingContext.DrawRoundedRectangle(LabelBackground, LinePen, new Rect(origin.X - 4, origin.Y - 2, text.Width + 8, text.Height + 4), 3, 3);
        drawingContext.DrawText(text, origin);
    }

    private static Point ToPoint(ViewPoint point) => new(point.X, point.Y);

    private static void OnToolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (MeasureLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as MeasureViewModel);
            layer.Attach(e.NewValue as MeasureViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(MeasureViewModel? measure)
    {
        if (measure is not null)
        {
            measure.Changed -= OnChanged;
            measure.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(MeasureViewModel? measure)
    {
        if (measure is not null)
        {
            measure.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
