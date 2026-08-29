using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LogAggregator.Models;
using LogAggregator.Services; // DetectionStatus lives here (see Services/MultiLineDetectionResult.cs)

namespace LogAggregator.SampleData;

/// <summary>SIMULATION SUPPORT - one line that didn't parse to the expected value (or didn't
/// parse at all). Capped per file by SimulationRunner so a systemic failure doesn't produce a
/// report with thousands of near-identical entries - the pass/fail counts already say how bad
/// it is; these are just a handful of concrete examples to debug from.</summary>
public sealed class LineFailure
{
    public int LineNumber { get; init; }
    public string LineText { get; init; } = string.Empty;
    public DateTime ExpectedUtc { get; init; }
    public DateTime? ActualUtc { get; init; }
    public string? Note { get; init; }
}

/// <summary>SIMULATION SUPPORT - the outcome for one generated file: what TimestampDetector's
/// auto-detect said about it, and (when it found something) how every data line's re-parsed
/// value compared against the known-correct value the generator recorded up front.</summary>
public sealed class FileResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FormatName { get; set; } = string.Empty;
    public FileType FileType { get; set; }
    public DetectionStatus DetectionStatus { get; set; }
    public string DetectionMessage { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
    public int TotalDataLines { get; set; }
    public int PassedLines { get; set; }
    public List<LineFailure> Failures { get; } = new();

    /// <summary>True only if detection succeeded AND every data line in the file re-parsed to
    /// the expected value. A file can have DetectionStatus.Ambiguous and still be Success=true
    /// here - "ambiguous but the top-ranked guess was still correct" is a real, useful signal,
    /// tracked separately below rather than folded into pass/fail.</summary>
    public bool Success => DetectionStatus != DetectionStatus.Failed && TotalDataLines > 0 && PassedLines == TotalDataLines;
}

/// <summary>SIMULATION SUPPORT - the full result of one SimulationRunner.Run() call: every
/// generated file's outcome, plus a plain-text report (ToReportText) suitable for pasting back
/// or attaching, since xUnit's own pass/fail output is per-[Theory]/[Fact] method, not per
/// generated record - this is the "which files/records actually failed" detail the request
/// asked for.</summary>
public sealed class SimulationReport
{
    public DateTime RunAtUtc { get; set; }
    public int Seed { get; set; }
    public List<FileResult> Files { get; } = new();

    public int TotalFiles => Files.Count;
    public int FilesPassed => Files.Count(f => f.Success);
    public int FilesFailed => TotalFiles - FilesPassed;
    public int FilesAmbiguousButCorrect => Files.Count(f => f.Success && f.DetectionStatus == DetectionStatus.Ambiguous);
    public int TotalDataLines => Files.Sum(f => f.TotalDataLines);
    public int TotalLinesPassed => Files.Sum(f => f.PassedLines);

    public string ToReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("LogAggregator simulation report");
        sb.AppendLine($"Run at (UTC):    {RunAtUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Seed:            {Seed}  (reuse this seed to reproduce this exact run)");
        sb.AppendLine($"Files:           {FilesPassed}/{TotalFiles} passed  ({FilesAmbiguousButCorrect} passed despite being flagged Ambiguous)");
        sb.AppendLine($"Data lines:      {TotalLinesPassed}/{TotalDataLines} parsed to the expected value");
        sb.AppendLine(new string('-', 78));

        foreach (var file in Files.OrderBy(f => f.Success).ThenBy(f => f.FilePath))
        {
            var status = file.Success ? "PASS" : "FAIL";
            sb.AppendLine($"[{status}] {System.IO.Path.GetFileName(file.FilePath)}  (format: {file.FormatName}, type: {file.FileType})");
            sb.AppendLine($"       detection: {file.DetectionStatus} - {file.DetectionMessage} ({file.CandidateCount} candidate(s))");
            sb.AppendLine($"       lines:     {file.PassedLines}/{file.TotalDataLines} matched expected value");

            foreach (var failure in file.Failures)
            {
                if (failure.Note is not null)
                {
                    sb.AppendLine($"         note: {failure.Note}");
                    continue;
                }
                var actualText = failure.ActualUtc is { } a ? a.ToString("yyyy-MM-dd HH:mm:ss.fffffff") : "(did not parse)";
                sb.AppendLine($"         line {failure.LineNumber}: expected {failure.ExpectedUtc:yyyy-MM-dd HH:mm:ss.fffffff} UTC, got {actualText}");
                sb.AppendLine($"           text: {failure.LineText}");
            }
        }

        return sb.ToString();
    }
}
