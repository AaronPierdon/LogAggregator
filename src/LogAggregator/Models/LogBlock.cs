using System;
using System.Collections.Generic;
namespace LogAggregator.Models;

/// <summary>
/// One detected log entry - the atomic unit for filtering and display. A block may span
/// multiple physical lines (continuation lines with no leading timestamp of their own).
/// </summary>
public sealed class LogBlock
{
    /// <summary>Normalized timestamp, used for sort and filter. If parsing failed this is
    /// DateTime.MinValue (see <see cref="TimestampParseFailed"/>) so the row still appears
    /// rather than being silently dropped.</summary>
    public DateTime UniversalTimestamp { get; set; }

    /// <summary>Raw timestamp text exactly as it appeared in the file.</summary>
    public string OriginalTimestamp { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    /// <summary>Denormalized for display performance (avoids a source lookup per row).</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Denormalized hex color, used by the row-tint converter without a source lookup.</summary>
    public string SourceColor { get; set; } = "#4C9BFF";

    /// <summary>All lines of this block. Lines[0] is the timestamp line; anything after is a
    /// continuation line collected until the next detected timestamp.</summary>
    public List<string> Lines { get; set; } = new();

    /// <summary>Pre-joined Lines, computed once at parse time. Filter matching and display
    /// read this - it is never re-joined at query time.</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>True if the timestamp could not be parsed with the source's pattern - the
    /// block is still ingested (never silently dropped) but sorts to the very top with a
    /// sentinel timestamp.</summary>
    public bool TimestampParseFailed { get; set; }

    /// <summary>Which physical file (within the source) this block came from - useful for
    /// diagnosing parse warnings.</summary>
    public string SourceFilePath { get; set; } = string.Empty;
}
