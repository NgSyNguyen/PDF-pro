using System.Windows;
using System.Windows.Media;
using PdfPro.App.ViewModels.Security;
using PdfPro.Core.Geometry;

namespace PdfPro.App.Controls;

/// <summary>
/// "Ký số" over one page of the viewer: the box of the signature being dragged. Purely visual: the mouse goes to the
/// viewer, which hands it to <see cref="SignaturePlacementViewModel"/>.
/// </summary>
public sealed class SignaturePlacementLayer : FrameworkElement
{
    public static readonly DependencyProperty PlacementProperty = DependencyProperty.Register(
        nameof(Placement), typeof(SignaturePlacementViewModel), typeof(SignaturePlacementLayer), new PropertyMetadata(null, OnPlacementChanged));

    public static readonly DependencyProperty PageKeyProperty = DependencyProperty.Register(
        nameof(PageKey), typeof(Guid), typeof(SignaturePlacementLayer), new PropertyMetadata(Guid.Empty, (d, _) => ((SignaturePlacementLayer)d).InvalidateVisual()));

    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4)));
    private static readonly Pen Frame = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))), 1.5) { DashStyle = DashStyles.Dash });

    public SignaturePlacementLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Attach(Placement);
        Unloaded += (_, _) => Detach(Placement);
    }

    public SignaturePlacementViewModel? Placement
    {
        get => (SignaturePlacementViewModel?)GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    /// <summary>The plan's key of the page under the layer.</summary>
    public Guid PageKey
    {
        get => (Guid)GetValue(PageKeyProperty);
        set => SetValue(PageKeyProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Placement is not { } placement || ActualWidth <= 0 || placement.Preview(PageKey) is not { } preview || placement.GeometryOf(PageKey) is not { } geometry)
        {
            return;
        }

        var mapper = new PageCoordinateMapper(geometry, ActualWidth / geometry.DisplaySize.Width);
        var shown = mapper.PdfRectToScreen(preview);
        drawingContext.DrawRectangle(Fill, Frame, new Rect(shown.X, shown.Y, shown.Width, shown.Height));
    }

    private static void OnPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (SignaturePlacementLayer)d;
        if (layer.IsLoaded)
        {
            layer.Detach(e.OldValue as SignaturePlacementViewModel);
            layer.Attach(e.NewValue as SignaturePlacementViewModel);
        }

        layer.InvalidateVisual();
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void Attach(SignaturePlacementViewModel? placement)
    {
        if (placement is not null)
        {
            placement.Changed -= OnChanged;
            placement.Changed += OnChanged;
        }

        InvalidateVisual();
    }

    private void Detach(SignaturePlacementViewModel? placement)
    {
        if (placement is not null)
        {
            placement.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();
}
