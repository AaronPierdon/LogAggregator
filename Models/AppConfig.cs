using System;
using System.Collections.Generic;

namespace LogAggregator.Models;

/// <summary>Root object persisted to sources.config.json.</summary>
public class AppConfig
{
    /// <summary>Bumped to 3 because LogType.IconGlyph's meaning changed from a Segoe MDL2 Assets
    /// font-glyph character to a vector icon key (see Converters.IconGeometry) - old persisted
    /// icon values would just be meaningless strings under the new system. ConfigService.LoadAsync
    /// treats any other value (including a missing/old file) as "start fresh" - this is a clean
    /// cutover, not a migration, per the app's early-development stage.</summary>
    public int Version { get; set; } = CurrentVersion;

    public const int CurrentVersion = 3;

    public List<LogSource> Sources { get; set; } = new();

    /// <summary>Reusable "how to parse this kind of log" definitions, managed from the
    /// "Manage Log Types" window and referenced by Sources' LogType bindings by Id.</summary>
    public List<LogType> LogTypes { get; set; } = new();

    public AppSettings Settings { get; set; } = new();
}

/// <summary>App-wide interface preferences, distinct from any one source.</summary>
public class AppSettings
{
    /// <summary>Whether the quick-add drag &amp; drop banner is shown above the filter bar.
    /// The user can hide it to save space (Settings menu > Interface) - hiding it shows a
    /// toast explaining how to bring it back rather than just disappearing silently.</summary>
    public bool ShowDropZone { get; set; } = true;

    /// <summary>Which color drives each log row's background tint in the main grid.</summary>
    public LogLineColorMode LogLineColorMode { get; set; } = LogLineColorMode.LogType;
}

/// <summary>How rows in the main log grid are tinted.</summary>
public enum LogLineColorMode
{
    /// <summary>Tint by the row's LogType color (default - the more specific unit now that a
    /// source can hold several differently-colored LogTypes).</summary>
    LogType,

    /// <summary>Tint by the row's Source color, same as every row from that card regardless of
    /// which LogType produced it.</summary>
    Source,

    /// <summary>No row coloring at all.</summary>
    None
}
