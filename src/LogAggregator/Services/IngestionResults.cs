using System;
using System.Collections.Generic;
using System.IO;

namespace LogAggregator.Services;

public class IngestionWarning
{
    /// <summary>Which source/LogType binding this warning is about - lets the UI route a click
    /// on the warning straight to the exact chip (and its timestamp settings) that needs
    /// attention, instead of a generic dialog.</summary>
    public string SourceId { get; set; } = string.Empty;
    public string LogTypeId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    /// <summary>True when this warning represents something the user likely needs to fix
    /// themselves (an undetermined/failed timestamp pattern) rather than just an FYI - drives
    /// the small error badge shown on the affected LogType's chip.</summary>
    public bool RequiresUserAction { get; set; }

    public override string ToString() =>
        $"[{OccurredAt:HH:mm:ss}] {Path.GetFileName(FilePath)} (line {LineNumber}): {Reason}";
}

/// <summary>Per-file result. No block list - blocks are written to SQLite in small batches as
/// they're parsed, so memory never holds more than one batch (~2000 rows) regardless of how
/// large the file is.</summary>
public class FileIngestionResult
{
    public List<IngestionWarning> Warnings { get; } = new();
}

/// <summary>Per-binding result, after all of its files have finished ingesting.</summary>
public class SourceIngestionResult
{
    public List<IngestionWarning> Warnings { get; set; } = new();

    /// <summary>Total row count for this binding, queried from SQLite after ingestion - the
    /// authoritative count, not a sum of in-memory lists.</summary>
    public long TotalCount { get; set; }
}
