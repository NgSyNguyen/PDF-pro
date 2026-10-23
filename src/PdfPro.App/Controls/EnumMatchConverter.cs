using System.Globalization;
using System.Windows.Data;

namespace PdfPro.App.Controls;

/// <summary>
/// Radio buttons bound to an enum: true when the value is the member named by the converter parameter; checking the
/// button sets that member.
/// </summary>
[ValueConversion(typeof(Enum), typeof(bool))]
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Enum member && parameter is string name && string.Equals(member.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
