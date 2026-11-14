using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Viewer;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// Draws text highlights over one page of the viewer, placed through <see cref="PageCoordinateMapper"/>: search
/// matches (the current one stronger) and, above them, the text selection — one band per line. Purely visual: the mouse
/// goes through it to the viewer.
/// </summary>
public sealed class TextHighlightLayer : FrameworkElement
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(TextSelectionViewModel), typeof(TextHighlightLayer), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty SearchProperty = DependencyProperty.Register(
        nameof(Search), typeof(SearchViewModel), typeof(TextHighlightLayer), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(TextHighlightLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((TextHighlightLayer)d).InvalidateVisual()));

    private static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x78, 0xD4)));
    private static readonly Brush MatchFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD7, 0x00)));
    private static readonly Brush CurrentFill = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x8C, 0x00)));

    public TextHighlightLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Selection, Search);
        Unloaded += (_, _) => Detach(Selection, Search);
    }

    public TextSelectionViewModel? Selection
    {
        get => (TextSelectionViewModel?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    public SearchViewModel? Search
    {
        get => (SearchViewModel?)GetValue(SearchProperty);
        set => SetValue(SearchProperty, value);
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
        if (Search is { } search)
        {
            Fill(drawingContext, mapper, search.MatchRectsOn(PageKey), MatchFill);
            Fill(drawingContext, mapper, search.CurrentRectsOn(PageKey), CurrentFill);
        }

        Fill(drawingContext, mapper, selection.RectsOn(PageKey), SelectionFill);
    }

    private static void Fill(DrawingContext drawingContext, PageCoordinateMapper mapper, IReadOnlyList<PdfRect> rects, Brush brush)
    {
        foreach (var rect in rects)
        {
            var shown = mapper.PdfRectToScreen(rect);
            drawingContext.DrawRectangle(brush, null, new Rect(shown.X, shown.Y, shown.Width, shown.Height));
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (TextHighlightLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as TextSelectionViewModel, e.OldValue as SearchViewModel);
            layer.Attach(e.NewValue as TextSelectionViewModel, e.NewValue as SearchViewModel);
        }

        layer.InvalidateVisual();
    }

    private void Attach(TextSelectionViewModel? selection, SearchViewModel? search)
    {
        Detach(selection, search);
        if (selection is not null)
        {
            selection.Changed += OnChanged;
        }

        if (search is not null)
        {
            search.Changed += OnChanged;
        }
    }

    private void Detach(TextSelectionViewModel? selection, SearchViewModel? search)
    {
        if (selection is not null)
        {
            selection.Changed -= OnChanged;
        }

        if (search is not null)
        {
            search.Changed -= OnChanged;
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
