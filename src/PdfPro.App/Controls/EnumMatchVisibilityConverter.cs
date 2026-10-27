using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PdfPro.App.Controls;

/// <summary>Visible when the enum value is the member named by the converter parameter, collapsed otherwise.</summary>
[ValueConversion(typeof(Enum), typeof(Visibility))]
public sealed class EnumMatchVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Enum member && parameter is string name && string.Equals(member.ToString(), name, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
