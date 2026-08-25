using System;
using System.Collections.Generic;
using System.Collections.Generic;

namespace LogAggregator.Models;

/// <summary>Root object persisted to sources.config.json.</summary>
public class AppConfig
{
    public int Version { get; set; } = 1;
    public List<LogSource> Sources { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>App-wide interface preferences, distinct from any one source.</summary>
public class AppSettings
{
    /// <summary>Whether the quick-add drag &amp; drop banner is shown above the filter bar.
    /// The user can hide it to save space (Settings menu > Interface) - hiding it shows a
    /// toast explaining how to bring it back rather than just disappearing silently.</summary>
    public bool ShowDropZone { get; set; } = true;
}
