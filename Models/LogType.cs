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

    /// <summary>A key into <see cref="Converters.IconGeometry"/>'s vector icon set (e.g.
    /// "gear") - NOT a font glyph. This used to hold a Segoe MDL2 Assets codepoint, but those
    /// were hand-transcribed from memory and, confirmed by the user's own screenshot of the
    /// running app, rendered as blank/invisible. Must be one of <see cref="IconChoices"/>'s keys
    /// (the editor only offers those), but nothing enforces that at the model level - an unknown
    /// key just falls back to the default icon (see IconGeometry.Resolve), never a blank one.</summary>
    public string IconGlyph { get; set; } = IconChoices[0].Key;

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
    /// Curated set of vector icon keys (see <see cref="Converters.IconGeometry"/>) relevant to
    /// log sources/devices, offered in the LogType editor's icon picker. These used to be Segoe
    /// MDL2 Assets glyph characters; they're plain string keys now, resolved to hand-authored
    /// Path geometry instead of a font, so there's no font-availability or codepoint-transcription
    /// risk - see IconGeometry's class comment for the full story.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label)> IconChoices = new (string, string)[]
    {
        ("gear", "Settings"),
        ("warning", "Warning"),
        ("pencil", "Edit"),
        ("plus", "Add"),
        ("cross", "Cancel"),
        ("refresh", "Refresh"),
        ("globe", "Globe"),
        ("home", "Home"),
        ("calendar", "Calendar"),
        ("clock", "Recent"),
        ("cloud", "Cloud"),
        ("manage", "Manage"),
        ("chip", "Chip"),
        ("star", "Important"),
        ("print", "Print"),
        ("reportdocument", "ReportDocument"),
        ("comment", "Comment"),
        ("settile", "SetTile"),
        ("certificate", "Certificate"),
        ("streaming", "Streaming"),
        ("monitor", "DeviceMonitor"),
        ("calendarweek", "CalendarWeek"),
        ("redeye", "RedEye"),
        ("tag", "Tag")
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
