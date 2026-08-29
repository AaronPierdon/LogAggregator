using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LogAggregator.Models;

namespace LogAggregator.Converters;

/// <summary>
/// Picks which denormalized color tints a log grid row's background, per the "Log line
/// coloring" setting (Models.AppConfig.AppSettings.LogLineColorMode): the row's LogType color
/// (default - the more specific unit now that a Source can hold several differently-colored
/// LogTypes), the row's Source color (same for every row from that card), or no tint at all.
/// Replaces the old single-value SourceTintConverter binding on Style.LogDataGridRow's
/// Background in Themes/DataGrid.xaml with a MultiBinding of
/// [LogBlock.LogTypeColor, LogBlock.SourceColor, MainViewModel.LogLineColorMode] - same low-alpha
/// tint math as SourceTintConverter, just picking the input hex first.
/// </summary>
public class RowTintConverter : IMultiValueConverter
{
    /// <summary>Alpha applied to the chosen color when tinting a row. Low enough to stay
    /// subtle on a near-black background.</summary>
    public byte Alpha { get; set; } = 26; // ~10% opacity

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Brushes.Transparent;

        var logTypeColor = values[0] as string;
        var sourceColor = values[1] as string;
        var mode = values[2] is LogLineColorMode m ? m : LogLineColorMode.LogType;

        string? hex = mode switch
        {
            LogLineColorMode.LogType => logTypeColor,
            LogLineColorMode.Source => sourceColor,
            _ => null // None - no tint at all
        };

        if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;

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

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
