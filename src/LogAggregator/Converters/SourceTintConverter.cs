using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LogAggregator.Converters;

/// <summary>
/// Converts a LogBlock's denormalized SourceColor hex string into a low-alpha SolidColorBrush,
/// so each row's background is a subtle tint of the source's color - visible, but not garish
/// against the dark surface. Bound directly to LogBlock.SourceColor (see spec: "Applied via
/// IValueConverter bound to SourceId" - using the denormalized color avoids a source lookup
/// per row for display performance).
/// </summary>
public class SourceTintConverter : IValueConverter
{
    /// <summary>Alpha applied to the source color when tinting a row. Low enough to stay
    /// subtle on a near-black background.</summary>
    public byte Alpha { get; set; } = 26; // ~10% opacity

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return Brushes.Transparent;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            color.A = Alpha;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
