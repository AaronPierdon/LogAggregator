using System;
using System.Collections.Generic;

namespace LogAggregator.Models;

/// <summary>
/// A reusable "how do I parse this kind of log" definition - file format, timestamp profile,
/// and how it's shown (color/icon). Sources no longer own any of this directly: a Source is
/// just a named group of LogTypes (see <see cref="SourceLogType"/>), so the same LogType (e.g.
/// "Kepware") can be reused across many Source cards without re-configuring it each time.
/// </summary>
public class LogType
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public FileType Format { get; set; } = FileType.FlatText;

    public TimestampProfile TimestampProfile { get; set; } = new();

    /// <summary>Hex color string, e.g. "#4C9BFF" - used for the chip swatch, the collapsed-rail
    /// wedge, and (when the "Log line coloring" setting is set to LogType) row tinting.</summary>
    public string ColorHex { get; set; } = "#4C9BFF";

    /// <summary>A Segoe MDL2 Assets glyph codepoint - the app already uses this font for
    /// window-chrome icons, so reusing it avoids adding a separate icon-asset pipeline. Must be
    /// one of <see cref="IconChoices"/>'s glyphs (the editor only offers those), but nothing
    /// enforces that at the model level.</summary>
    public string IconGlyph { get; set; } = IconChoices[0].Glyph;

    public LogTypeDisplayMode DisplayMode { get; set; } = LogTypeDisplayMode.Both;

    public LogType Clone() => new()
    {
        Id = Id,
        Name = Name,
        Format = Format,
        TimestampProfile = TimestampProfile.Clone(),
        ColorHex = ColorHex,
        IconGlyph = IconGlyph,
        DisplayMode = DisplayMode
    };

    /// <summary>
    /// Curated set of Segoe MDL2 Assets glyphs relevant to log sources/devices, offered in the
    /// LogType editor's icon picker. Codepoints are given as C# \u escapes so they're unambiguous
    /// regardless of editor/encoding. NOTE: these are transcribed from memory of the standard
    /// Segoe MDL2 Assets glyph table - if any one of them renders as a "tofu" box instead of the
    /// intended glyph on your machine, it's purely cosmetic (doesn't affect compiling or app
    /// logic); swap the \u value using the public "Segoe MDL2 Assets icon list" cheat sheet or
    /// Windows Character Map (font: Segoe MDL2 Assets) to find the exact codepoint you want.
    /// </summary>
    public static readonly IReadOnlyList<(string Glyph, string Label)> IconChoices = new (string, string)[]
    {
        ("", "Settings"),
        ("", "Warning"),
        ("", "Edit"),
        ("", "Add"),
        ("", "Cancel"),
        ("", "Refresh"),
        ("", "Globe"),
        ("", "Home"),
        ("", "Calendar"),
        ("", "Recent"),
        ("", "Cloud"),
        ("", "Manage"),
        ("", "Chip"),
        ("", "Important"),
        ("", "Print"),
        ("", "ReportDocument"),
        ("", "Comment"),
        ("", "SetTile"),
        ("", "Certificate"),
        ("", "Streaming"),
        ("", "DeviceMonitor"),
        ("", "CalendarWeek"),
        ("", "RedEye"),
        ("", "Tag")
    };
}

/// <summary>How a LogType is rendered wherever it's shown as a chip/wedge: color swatch, icon
/// glyph, or both together. Chips get larger when Both is selected (icon over/next to swatch).</summary>
public enum LogTypeDisplayMode
{
    Color,
    Icon,
    Both
}
