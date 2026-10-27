using System.Windows;
using System.Windows.Media;
using PdfPro.App.Services.Annotating;
using PdfPro.App.ViewModels.Annotating;
using PdfPro.Core.Annotations;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// Draws the annotations made in the session over one page of the viewer (the page image comes from the source file,
/// which does not have them yet), the one being moved where the mouse has it, and a frame around the selected one —
/// for an annotation already in the file (drawn by the page image), the frame alone.
/// Purely visual: the mouse goes to the viewer, which hands it to <see cref="AnnotationToolsViewModel"/>.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    public static readonly DependencyProperty AnnotationsProperty = DependencyProperty.Register(
        nameof(Annotations), typeof(AnnotationToolsViewModel), typeof(AnnotationLayer), new PropertyMetadata(null, OnAnnotationsChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(AnnotationLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((AnnotationLayer)d).InvalidateVisual()));

    private static readonly Pen SelectionPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))), 1) { DashStyle = DashStyles.Dash });

    public AnnotationLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Annotations);
        Unloaded += (_, _) => Detach(Annotations);
    }

    public AnnotationToolsViewModel? Annotations
    {
        get => (AnnotationToolsViewModel?)GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Annotations is not { } annotations || ActualWidth <= 0)
        {
            return;
        }

        var items = annotations.AnnotationsOn(PageKey);
        var preview = annotations.PreviewOn(PageKey);
        var fileSelection = annotations.FileSelectionOn(PageKey);
        _ = annotations.FileAnnotationsOn(PageKey); // read in the background, so that a click can pick them
        if ((items.Count == 0 && preview is null && fileSelection is null) || annotations.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var selected = annotations.Selected is { } target && target.PageKey == PageKey ? target.Id : null;
        foreach (var item in items)
        {
            var shown = annotations.Shown(PageKey, item);
            try
            {
                AnnotationDrawing.Draw(drawingContext, shown, mapper, annotations.Fonts, annotations.Images, pixelsPerDip);
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
            {
                // A stamp picture that cannot be read (or a font gone missing): its place stays outlined.
                drawingContext.DrawRectangle(null, SelectionPen, AnnotationDrawing.ScreenBounds(shown, mapper));
            }

            if (item.Id == selected)
            {
                var frame = AnnotationDrawing.ScreenBounds(shown, mapper);
                frame.Inflate(3, 3);
                drawingContext.DrawRectangle(null, SelectionPen, frame);
            }
        }

        if (fileSelection is { } rect)
        {
            // An annotation of the file is drawn by the page image: only its frame shows (where the drag has it).
            var bounds = mapper.PdfRectToScreen(rect);
            var frame = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            frame.Inflate(3, 3);
            drawingContext.DrawRectangle(null, SelectionPen, frame);
        }

        if (preview is not null)
        {
            // What is being drawn; a text box has no text yet, so its place is outlined.
            AnnotationDrawing.Draw(drawingContext, preview, mapper, annotations.Fonts, annotations.Images, pixelsPerDip);
            if (preview is FreeTextAnnotation)
            {
                drawingContext.DrawRectangle(null, SelectionPen, AnnotationDrawing.ScreenBounds(preview, mapper));
            }
        }
    }

    private static void OnAnnotationsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (AnnotationLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as AnnotationToolsViewModel);
            layer.Attach(e.NewValue as AnnotationToolsViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(AnnotationToolsViewModel? annotations)
    {
        if (annotations is not null)
        {
            annotations.Changed -= OnChanged;
            annotations.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(AnnotationToolsViewModel? annotations)
    {
        if (annotations is not null)
        {
            annotations.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
