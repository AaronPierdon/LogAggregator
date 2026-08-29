using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LogAggregator.Converters;

/// <summary>
/// Builds one pie-wedge Geometry for the collapsed left-panel's icon rail (see
/// MainWindow.xaml's "Icon rail (collapsed)" section) - each bound Source shows up to 4 wedges,
/// one per LogType color (see SourceCardViewModel.WedgeColors), instead of a single flat circle.
/// values[0] is this wedge's index (from ItemsControl.AlternationIndex, boxed int); values[1] is
/// the total wedge count for this source (SourceCardViewModel.WedgeColors.Count). Wedges are
/// drawn within a fixed 34x34 square (matching the rail's swatch size) starting at 12 o'clock and
/// proceeding clockwise. A single-wedge source (the common case: one or zero LogTypes) just gets
/// a full circle rather than a "wedge" that happens to be 360 degrees, since an ArcSegment can't
/// represent a full circle on its own.
/// </summary>
public class PieWedgeGeometryConverter : IMultiValueConverter
{
    private const double Diameter = 34;
    private const double Radius = Diameter / 2;
    private static readonly Point Center = new(Radius, Radius);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not int index || values[1] is not int total || total <= 0)
            return Geometry.Empty;

        if (total <= 1)
            return new EllipseGeometry(Center, Radius, Radius);

        double degreesPerWedge = 360.0 / total;
        double startAngle = index * degreesPerWedge - 90.0; // start at 12 o'clock
        double endAngle = startAngle + degreesPerWedge;

        var startPoint = PointOnCircle(startAngle);
        var endPoint = PointOnCircle(endAngle);
        bool isLargeArc = degreesPerWedge > 180.0;

        var figure = new PathFigure { StartPoint = Center, IsClosed = true };
        figure.Segments.Add(new LineSegment(startPoint, true));
        figure.Segments.Add(new ArcSegment(endPoint, new Size(Radius, Radius), 0, isLargeArc,
            SweepDirection.Clockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new Point(Center.X + Radius * Math.Cos(radians), Center.Y + Radius * Math.Sin(radians));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
