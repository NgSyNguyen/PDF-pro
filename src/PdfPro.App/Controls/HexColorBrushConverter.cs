using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PdfPro.Core.Overlay;

namespace PdfPro.App.Controls;

/// <summary>"#RRGGBB" to a brush of that colour (transparent for anything else).</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class HexColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && RgbColor.TryParse(hex, out var color))
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.Value.R, color.Value.G, color.Value.B));
            brush.Freeze();
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
