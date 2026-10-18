using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Controls;

/// <summary>
/// Attached behaviour for item templates of virtualized lists: tells the item's view model
/// (<see cref="IRealizable"/>) when an element starts or stops showing it, with the element's DPI, so off-screen
/// items hold no images. On an <see cref="Image"/>, a DPI change (window moved to another monitor) realizes the
/// item again at the new DPI.
/// </summary>
/// <remarks>
/// The element remembers the item it realized and releases that one. It cannot rely on the current DataContext:
/// when a virtualizing panel recycles a container it switches the DataContext to the {DisconnectedItem} sentinel
/// without raising DataContextChanged, so by the time Unloaded arrives the shown item is no longer visible from the
/// element.
/// </remarks>
public static class Realization
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(Realization),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty RealizedItemProperty = DependencyProperty.RegisterAttached(
        "RealizedItem",
        typeof(IRealizable),
        typeof(Realization),
        new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    /// <summary>The item this element currently keeps realized (tests and diagnostics).</summary>
    internal static IRealizable? GetRealizedItem(DependencyObject element) => (IRealizable?)element.GetValue(RealizedItemProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            element.DataContextChanged += OnDataContextChanged;
            if (element is Image image)
            {
                image.DpiChanged += OnDpiChanged;
            }

            if (element.IsLoaded)
            {
                Show(element, element.DataContext, DpiOf(element));
            }
        }
        else
        {
            element.Loaded -= OnLoaded;
            element.Unloaded -= OnUnloaded;
            element.DataContextChanged -= OnDataContextChanged;
            if (element is Image image)
            {
                image.DpiChanged -= OnDpiChanged;
            }

            Hide(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        Show(element, element.DataContext, DpiOf(element));
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Hide((FrameworkElement)sender);

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.IsLoaded)
        {
            Show(element, e.NewValue, DpiOf(element));
        }
        else
        {
            Hide(element);
        }
    }

    private static void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (element.IsLoaded)
        {
            Show(element, element.DataContext, e.NewDpi.DpiScaleX);
        }
    }

    /// <summary>Realizes <paramref name="item"/> (releasing whatever the element showed before).</summary>
    private static void Show(FrameworkElement element, object? item, double dpiScale)
    {
        if (item is not IRealizable realizable)
        {
            Hide(element);
            return;
        }

        if (GetRealizedItem(element) is { } previous && !ReferenceEquals(previous, realizable))
        {
            previous.Release();
        }

        element.SetValue(RealizedItemProperty, realizable);
        realizable.Realize(dpiScale);
    }

    private static void Hide(FrameworkElement element)
    {
        if (GetRealizedItem(element) is { } realized)
        {
            element.ClearValue(RealizedItemProperty);
            realized.Release();
        }
    }

    private static double DpiOf(Visual element) => VisualTreeHelper.GetDpi(element).DpiScaleX;
}
