using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LogAggregator.Converters;

/// <summary>
/// Hand-authored vector icon set for LogType chips and the icon picker, replacing the previous
/// Segoe MDL2 Assets font-glyph codepoints. Those codepoints were hand-transcribed from memory of
/// the font's glyph table and - confirmed by the user's own screenshot of the running app -
/// rendered as blank/invisible rather than the intended symbols, regardless of whether the
/// codepoints themselves were technically correct. Vector Path geometry (the same technique
/// already used for this app's other chrome icons, e.g. the drop-zone "+" and the sync spinner)
/// has no font-availability or codepoint-transcription risk: a shape either parses or it doesn't,
/// and <see cref="Resolve"/> always falls back to a plain default rather than ever showing
/// nothing. <see cref="Models.LogType.IconGlyph"/> now stores one of these string keys (e.g.
/// "gear") instead of a font character - the property name is unchanged to keep the rest of the
/// app's diff small; only what the string means changed.
/// </summary>
public static class IconGeometry
{
    public const string DefaultKey = "gear";

    /// <summary>
    /// Each entry is raw WPF Path mini-language data on a 20x20 design grid, rendered with
    /// Stretch="Uniform" wherever it's used so the actual on-screen size is whatever the Path
    /// element specifies. These are simple, deliberately low-risk geometric shapes rather than
    /// pixel-perfect pictograms - the goal is that every one of them reliably renders as a real,
    /// distinct, recognizable (with its tooltip label) shape, never a blank box.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PathData = new Dictionary<string, string>
    {
        // Settings: a ring (gear stand-in) via the "two arcs" full-circle idiom, with a smaller
        // concentric circle cut out of the middle.
        ["gear"] = "M10,10 m -8,0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0 M10,10 m -3.2,0 a3.2,3.2 0 1,0 6.4,0 a3.2,3.2 0 1,0 -6.4,0",

        // Warning: filled triangle with an exclamation mark cut out.
        ["warning"] = "M10,1 L19,18 L1,18 Z M9.3,7 L10.7,7 L10.3,13.5 L9.7,13.5 Z M9.3,15 L10.7,15 L10.7,16.4 L9.3,16.4 Z",

        // Edit: a simple pencil silhouette (shaft + angled tip).
        ["pencil"] = "M2,18 L2,15 L13,4 L16,7 L5,18 Z M13,4 L16,1 L19,4 L16,7 Z",

        // Add: a plus sign.
        ["plus"] = "M8,1 L12,1 L12,8 L19,8 L19,12 L12,12 L12,19 L8,19 L8,12 L1,12 L1,8 L8,8 Z",

        // Cancel: an X.
        ["cross"] = "M3,1 L6,1 L10,7 L14,1 L17,1 L12,10 L17,19 L14,19 L10,13 L6,19 L3,19 L8,10 Z",

        // Refresh: a 4-bladed pinwheel suggesting rotation.
        ["refresh"] = "M10,10 L10,2 L14,4 Z M10,10 L18,10 L16,14 Z M10,10 L10,18 L6,16 Z M10,10 L2,10 L4,6 Z",

        // Globe: a ring with a cross cut out of it (meridian/equator lines).
        ["globe"] = "M10,10 m -8,0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0 M2,9.3 L18,9.3 L18,10.7 L2,10.7 Z M9.3,2 L10.7,2 L10.7,18 L9.3,18 Z",

        // Home: a simple house silhouette with a door cut-out.
        ["home"] = "M10,2 L19,10 L16,10 L16,18 L11,18 L11,13 L9,13 L9,18 L4,18 L4,10 L1,10 Z",

        // Calendar: a page with two hanger tabs and a header rule.
        ["calendar"] = "M2,4 L18,4 L18,18 L2,18 Z M5,1 L6.5,1 L6.5,5 L5,5 Z M13.5,1 L15,1 L15,5 L13.5,5 Z M2,7 L18,7 L18,8.4 L2,8.4 Z",

        // Recent: a clock face (solid disc) with hour/minute hands cut out.
        ["clock"] = "M10,10 m -8,0 a8,8 0 1,0 16,0 a8,8 0 1,0 -16,0 M9.3,4 L10.7,4 L10.7,10.5 L9.3,10.5 Z M10,9.3 L15,9.3 L15,10.7 L10,10.7 Z",

        // Cloud: three overlapping circles plus a base band.
        ["cloud"] = "M6,15 m -3,0 a3,3 0 1,0 6,0 a3,3 0 1,0 -6,0 M11,12 m -4,0 a4,4 0 1,0 8,0 a4,4 0 1,0 -8,0 M15,14 m -2.5,0 a2.5,2.5 0 1,0 5,0 a2.5,2.5 0 1,0 -5,0 M3,15 L17,15 L17,18 L3,18 Z",

        // Manage: three stacked bars.
        ["manage"] = "M2,4 L18,4 L18,6.5 L2,6.5 Z M2,8.75 L18,8.75 L18,11.25 L2,11.25 Z M2,13.5 L18,13.5 L18,16 L2,16 Z",

        // Chip: a square body with small pins on all four sides.
        ["chip"] = "M5,5 L15,5 L15,15 L5,15 Z M8,1 L9,1 L9,5 L8,5 Z M11,1 L12,1 L12,5 L11,5 Z M8,15 L9,15 L9,19 L8,19 Z M11,15 L12,15 L12,19 L11,19 Z M1,8 L5,8 L5,9 L1,9 Z M1,11 L5,11 L5,12 L1,12 Z M15,8 L19,8 L19,9 L15,9 Z M15,11 L19,11 L19,12 L15,12 Z",

        // Important: a 5-point star.
        ["star"] = "M10,1 L12.12,7.09 L18.56,7.22 L13.42,11.11 L15.29,17.28 L10,13.6 L4.71,17.28 L6.58,11.11 L1.44,7.22 L7.88,7.09 Z",

        // Print: a printer body between the page above and the printed sheet below.
        ["print"] = "M2,7 L18,7 L18,14 L2,14 Z M5,2 L15,2 L15,7 L5,7 Z M5,14 L15,14 L15,19 L5,19 Z",

        // ReportDocument: a page with a folded corner and three text lines.
        ["reportdocument"] = "M4,1 L13,1 L17,5 L17,19 L4,19 Z M13,1 L17,5 L13,5 Z M6,9 L15,9 L15,10.2 L6,10.2 Z M6,12 L15,12 L15,13.2 L6,13.2 Z M6,15 L12,15 L12,16.2 L6,16.2 Z",

        // Comment: a speech bubble with a pointer tail.
        ["comment"] = "M2,3 L18,3 L18,14 L9,14 L6,18 L6,14 L2,14 Z",

        // SetTile: a 2x2 grid of tiles.
        ["settile"] = "M2,2 L9,2 L9,9 L2,9 Z M11,2 L18,2 L18,9 L11,9 Z M2,11 L9,11 L9,18 L2,18 Z M11,11 L18,11 L18,18 L11,18 Z",

        // Certificate: a circular seal with two ribbon tails.
        ["certificate"] = "M3,8 A7,7 0 1,0 17,8 A7,7 0 1,0 3,8 Z M7,14 L10,12 L7,19 Z M13,14 L10,12 L13,19 Z",

        // Streaming: three ascending signal bars.
        ["streaming"] = "M2,13 L6,13 L6,18 L2,18 Z M8,9 L12,9 L12,18 L8,18 Z M14,3 L18,3 L18,18 L14,18 Z",

        // DeviceMonitor: a screen with a hollow display area, on a small stand.
        ["monitor"] = "M2,3 L18,3 L18,13 L2,13 Z M4,5 L16,5 L16,11 L4,11 Z M8,15 L12,15 L12,17 L8,17 Z M6,17 L14,17 L14,18.5 L6,18.5 Z",

        // CalendarWeek: a calendar page with a punched-out day grid.
        ["calendarweek"] = "M2,3 L18,3 L18,18 L2,18 Z M2,3 L18,3 L18,6.5 L2,6.5 Z M4,9 L6.5,9 L6.5,11.5 L4,11.5 Z M8.75,9 L11.25,9 L11.25,11.5 L8.75,11.5 Z M13.5,9 L16,9 L16,11.5 L13.5,11.5 Z M4,13 L6.5,13 L6.5,15.5 L4,15.5 Z M8.75,13 L11.25,13 L11.25,15.5 L8.75,15.5 Z M13.5,13 L16,13 L16,15.5 L13.5,15.5 Z",

        // RedEye: an almond eye shape with a pupil cut out.
        ["redeye"] = "M2,10 Q10,2 18,10 Q10,18 2,10 Z M10,10 m -3.5,0 a3.5,3.5 0 1,0 7,0 a3.5,3.5 0 1,0 -7,0",

        // Tag: a price-tag silhouette with a punched hole.
        ["tag"] = "M2,2 L11,2 L18,9 L11,16 L2,16 Z M5,5 m -1.3,0 a1.3,1.3 0 1,0 2.6,0 a1.3,1.3 0 1,0 -2.6,0"
    };

    /// <summary>Resolves an icon key to a frozen Geometry, falling back to the default "gear"
    /// shape for an unknown/blank key or (defensively) a malformed path string - an icon can look
    /// generic but must never render as nothing.</summary>
    public static Geometry Resolve(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && PathData.TryGetValue(key, out var data))
        {
            try
            {
                var geometry = Geometry.Parse(data);
                geometry.Freeze();
                return geometry;
            }
            catch
            {
                // Fall through to the default below rather than let a bad path string blank the icon.
            }
        }

        var fallback = Geometry.Parse(PathData[DefaultKey]);
        fallback.Freeze();
        return fallback;
    }
}

/// <summary>Resolves a LogType's icon key (<see cref="Models.LogType.IconGlyph"/>) to its vector
/// Geometry for rendering in a Path - see <see cref="IconGeometry"/> for the actual shape data.</summary>
public class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        IconGeometry.Resolve(value as string);

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
