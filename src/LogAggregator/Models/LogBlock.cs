using System;

namespace LogAggregator.Models;

/// <summary>
/// One detected log entry - the atomic unit for filtering, display, and export. A block may
/// span multiple physical lines (continuation lines with no leading timestamp of their own);
/// those are joined into <see cref="FullText"/> with '\n' separators rather than stored as a
/// separate list, since that's the only representation that needs to round-trip through SQLite.
/// This class is a plain Dapper row-mapping target: property names here must match the
/// LogBlocks table's column names exactly (see LogDatabase.cs).
/// </summary>
public sealed class LogBlock
{
    /// <summary>SQLite rowid. 0 for a not-yet-persisted instance.</summary>
    public long Id { get; set; }

    /// <summary>Normalized timestamp, used for sort and filter. If parsing failed this is
    /// DateTime.MinValue (see <see cref="TimestampParseFailed"/>) so the row still appears
    /// rather than being silently dropped.</summary>
    public DateTime UniversalTimestamp { get; set; }

    /// <summary>Raw timestamp text exactly as it appeared in the file.</summary>
    public string OriginalTimestamp { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    /// <summary>Denormalized for display performance (avoids a join per row).</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Denormalized hex color, used by the row-tint converter without a join.</summary>
    public string SourceColor { get; set; } = "#4C9BFF";

    /// <summary>All lines of this block joined with '\n'. Filter matching and display read
    /// this directly; continuation lines (for export formatting) are recovered via
    /// <see cref="SplitLines"/> rather than being stored a second time.</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>True if the timestamp could not be parsed with the source's pattern - the
    /// block is still ingested (never silently dropped) but sorts to the very top with a
    /// sentinel timestamp.</summary>
    public bool TimestampParseFailed { get; set; }

    /// <summary>Which physical file (within the source) this block came from - useful for
    /// diagnosing parse warnings.</summary>
    public string SourceFilePath { get; set; } = string.Empty;

    public string[] SplitLines() => FullText.Split('\n');
}
