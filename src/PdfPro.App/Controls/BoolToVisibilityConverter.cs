using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdfPro.App.Controls;

/// <summary>true → Visible, anything else → Collapsed; <see cref="IsInverted"/> swaps the two.</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool IsInverted { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) != IsInverted ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility visibility ? (visibility == Visibility.Visible) != IsInverted : Binding.DoNothing;
}
