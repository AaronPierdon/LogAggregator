using System;
using System.Collections.Generic;
using System.IO;
using LogAggregator.Models;

namespace LogAggregator.Services;

public class IngestionWarning
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.Now;

    public override string ToString() =>
        $"[{OccurredAt:HH:mm:ss}] {Path.GetFileName(FilePath)} (line {LineNumber}): {Reason}";
}

public class FileIngestionResult
{
    public List<LogBlock> Blocks { get; } = new();
    public List<IngestionWarning> Warnings { get; } = new();
}

public class SourceIngestionResult
{
    public List<LogBlock> Blocks { get; set; } = new();
    public List<IngestionWarning> Warnings { get; set; } = new();
}
