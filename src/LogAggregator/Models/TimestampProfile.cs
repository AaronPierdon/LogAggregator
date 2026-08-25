using System;
namespace LogAggregator.Models;

/// <summary>
/// Where, within a record, the timestamp lives.
/// </summary>
public enum TimestampLocationMode
{
    /// <summary>FlatText sources: the timestamp is at the start of the line, extracted with
    /// a leading regex.</summary>
    LineStart,

    /// <summary>CSV/TabDelimited sources: the timestamp is the full value of a single column.</summary>
    DelimitedColumn,

    /// <summary>CSV/TabDelimited sources: the date and time live in two separate adjacent
    /// columns (e.g. a "Date" column and a "Time" column) and must be concatenated before
    /// parsing.</summary>
    DelimitedTwoColumn
}

/// <summary>
/// Describes how to locate and parse the timestamp for a given LogSource. Produced either by
/// TimestampDetector.AutoDetectFromLines() against real sample lines read from the file, or
/// built/edited by hand in the wizard's manual override.
/// </summary>
public class TimestampProfile
{
    public TimestampLocationMode Mode { get; set; } = TimestampLocationMode.LineStart;

    /// <summary>
    /// Leading regex for LineStart mode. Must contain a capture group named "ts" that
    /// captures the raw timestamp text. Example: <c>^\s*(?&lt;ts&gt;\d{2}-\d{2}-\d{4}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)</c>
    /// </summary>
    public string RegexPattern { get; set; } = string.Empty;

    /// <summary>
    /// Zero-based column index for DelimitedColumn mode, or the "date" column for
    /// DelimitedTwoColumn mode.
    /// </summary>
    public int ColumnIndex { get; set; } = -1;

    /// <summary>
    /// The "time" column for DelimitedTwoColumn mode.
    /// </summary>
    public int SecondColumnIndex { get; set; } = -1;

    /// <summary>
    /// A specific .NET custom DateTime format string to try first (e.g. "MM-dd-yyyy HH:mm:ss").
    /// May be empty, in which case only the built-in candidate library + DateTime.TryParse
    /// fallback are used. Fractional seconds of any length are normalized before parsing, so
    /// a format containing "fffffff" will match ".14983", ".1234567", ".9", etc.
    /// </summary>
    public string FormatString { get; set; } = string.Empty;

    /// <summary>
    /// A short human-readable description shown in the wizard, e.g. "Column 1 (M/d/yyyy h:mm:ss tt)".
    /// Purely informational - not used for parsing.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public TimestampProfile Clone() => new()
    {
        Mode = Mode,
        RegexPattern = RegexPattern,
        ColumnIndex = ColumnIndex,
        SecondColumnIndex = SecondColumnIndex,
        FormatString = FormatString,
        Description = Description
    };
}
