using System;
namespace LogAggregator.Models;

/// <summary>
/// The file format a LogSource's files are stored in. Extensible - add a new value here plus
/// a branch in IngestionService/TimestampDetector if you need another format later.
/// </summary>
public enum FileType
{
    /// <summary>Comma-separated values. Parsed with full RFC4180 quoting rules, so quoted
    /// fields may contain embedded commas and even embedded newlines (e.g. Windows Event
    /// Viewer CSV exports with multi-line descriptions).</summary>
    CSV,

    /// <summary>Delimited by a single tab or comma-with-repeated-whitespace layout. Each
    /// physical line is one record; lines that don't parse into the expected shape are
    /// treated as continuation lines of the previous block.</summary>
    TabDelimited,

    /// <summary>Plain text log. A new block starts whenever a line's leading text matches the
    /// source's timestamp pattern; every other line is a continuation of the current block.
    /// This is the default and most common format.</summary>
    FlatText
}
