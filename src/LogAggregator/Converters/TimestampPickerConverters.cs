using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LogAggregator.Services;

namespace LogAggregator.Converters;

/// <summary>Tints a timestamp-picker token button by the app's soft guess about what it is -
/// purely a visual hint, since every token stays independently selectable regardless of its
/// hint (see TimestampTokenizer/TimestampPickerViewModel).</summary>
public class TimestampTokenHintToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            TimestampTokenHint.LikelyDatePart => new SolidColorBrush(Color.FromArgb(0x33, 0x3D, 0xDC, 0x97)),
            TimestampTokenHint.LikelyTimePart => new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0x9B, 0xFF)),
            _ => System.Windows.Media.Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when the bound string is non-empty - used for the picker's test-result
/// banner, which starts blank before Apply has been clicked once.</summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
