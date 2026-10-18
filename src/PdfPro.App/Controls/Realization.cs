using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfPro.App.ViewModels;

namespace PdfPro.App.Controls;

/// <summary>
/// Attached behaviour for item templates of virtualized lists: tells the item's view model
/// (<see cref="IRealizable"/>) when an element starts or stops showing it, with the element's DPI. Recycled
/// containers release their previous item, so off-screen items hold no images. On an <see cref="Image"/>, a DPI
/// change (window moved to another monitor) realizes the item again at the new DPI.
/// </summary>
public static class Realization
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(Realization),
        new PropertyMetadata(false, OnIsEnabledChanged));

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
                Realize(element, element.DataContext);
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

            (element.DataContext as IRealizable)?.Release();
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        Realize(element, element.DataContext);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => (((FrameworkElement)sender).DataContext as IRealizable)?.Release();

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        (e.OldValue as IRealizable)?.Release();
        if (element.IsLoaded)
        {
            Realize(element, e.NewValue);
        }
    }

    private static void OnDpiChanged(object sender, DpiChangedEventArgs e)
        => (((FrameworkElement)sender).DataContext as IRealizable)?.Realize(e.NewDpi.DpiScaleX);

    private static void Realize(FrameworkElement element, object? item)
    {
        if (item is IRealizable realizable)
        {
            realizable.Realize(VisualTreeHelper.GetDpi(element).DpiScaleX);
        }
    }
}
