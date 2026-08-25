using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Reads all files belonging to a LogSource, detects log-entry (block) boundaries, normalizes
/// timestamps, and returns a single sorted batch of LogBlocks. Every source syncs on its own
/// Task/CancellationToken; within a source, files are parsed in parallel.
/// </summary>
public class IngestionService
{
    /// <summary>Files at or above this size stream line-by-line (StreamReader) instead of being
    /// fully buffered (File.ReadAllLines), to cap RAM use. Named constant - easy to tune.</summary>
    public const long StreamingThresholdBytes = 10L * 1024 * 1024; // 10 MB

    /// <summary>How often (in records/lines) cancellation is checked - frequent enough to cancel
    /// promptly, infrequent enough not to add meaningful overhead.</summary>
    private const int CancellationCheckInterval = 1000;

    public async Task<SourceIngestionResult> IngestSourceAsync(LogSource source, CancellationToken ct)
    {
        var allBlocks = new List<LogBlock>();
        var allWarnings = new List<IngestionWarning>();
        var sync = new object();

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        await Parallel.ForEachAsync(source.FilePaths, options, async (path, token) =>
        {
            var fileResult = await IngestFileAsync(path, source, token).ConfigureAwait(false);
            lock (sync)
            {
                allBlocks.AddRange(fileResult.Blocks);
                allWarnings.AddRange(fileResult.Warnings);
            }
        }).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        // Single sorted batch - the caller inserts this all at once rather than re-sorting the
        // display collection per block.
        allBlocks.Sort((a, b) => a.UniversalTimestamp.CompareTo(b.UniversalTimestamp));

        return new SourceIngestionResult { Blocks = allBlocks, Warnings = allWarnings };
    }

    private Task<FileIngestionResult> IngestFileAsync(string path, LogSource source, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var info = new FileInfo(path);
            bool streaming = info.Exists && info.Length >= StreamingThresholdBytes;

            return source.Type switch
            {
                FileType.CSV => IngestDelimited(path, source, streaming, ct, delimiter: ','),
                FileType.TabDelimited => IngestTabDelimited(path, source, streaming, ct),
                _ => IngestFlatText(path, source, streaming, ct)
            };
        }, ct);
    }

    // ===================================================================
    // FlatText: a line starting with a matching timestamp begins a new block;
    // everything else is a continuation of the current block.
    // ===================================================================
    private static FileIngestionResult IngestFlatText(string path, LogSource source, bool streaming, CancellationToken ct)
    {
        var result = new FileIngestionResult();
        List<string>? currentLines = null;
        DateTime currentUtc = DateTime.MinValue;
        string currentOriginal = string.Empty;
        bool currentFailed = false;
        long lineIndex = 0;

        void Flush()
        {
            if (currentLines is null) return;
            result.Blocks.Add(new LogBlock
            {
                UniversalTimestamp = currentUtc,
                OriginalTimestamp = currentOriginal,
                SourceId = source.Id,
                SourceName = source.Name,
                SourceColor = source.DisplayColor,
                Lines = currentLines,
                FullText = string.Join(Environment.NewLine, currentLines),
                TimestampParseFailed = currentFailed,
                SourceFilePath = path
            });
            currentLines = null;
        }

        void ProcessLine(string line)
        {
            lineIndex++;
            if (lineIndex % CancellationCheckInterval == 0) ct.ThrowIfCancellationRequested();

            var eval = TimestampDetector.EvaluateLine(line, source.TimestampProfile);
            if (eval.Matched)
            {
                Flush();
                currentLines = new List<string> { line };
                currentUtc = eval.Utc;
                currentOriginal = eval.RawText;
                currentFailed = !eval.Parsed;

                if (!eval.Parsed)
                {
                    result.Warnings.Add(new IngestionWarning
                    {
                        FilePath = path,
                        LineNumber = (int)lineIndex,
                        RawText = line,
                        Reason = $"Timestamp-shaped text \"{eval.RawText}\" could not be parsed - block kept with a sentinel timestamp."
                    });
                }
            }
            else if (currentLines is not null)
            {
                currentLines.Add(line);
            }
            // else: preamble before the first recognized timestamp (banners, blank lines) -
            // intentionally dropped since there is no block yet to attach it to.
        }

        if (streaming)
        {
            using var sr = new StreamReader(path);
            string? line;
            while ((line = sr.ReadLine()) != null)
                ProcessLine(line);
        }
        else
        {
            foreach (var line in File.ReadAllLines(path))
                ProcessLine(line);
        }

        Flush();

        if (result.Blocks.Count == 0 && lineIndex > 0)
        {
            result.Warnings.Add(new IngestionWarning
            {
                FilePath = path,
                LineNumber = 0,
                Reason = "No line in this file matched the configured timestamp pattern - no blocks were created. Check the pattern in the source's wizard."
            });
        }

        return result;
    }

    // ===================================================================
    // CSV: real RFC4180 records (embedded commas/newlines inside quotes are data, not new
    // records or blocks). Each record is its own block.
    // ===================================================================
    private static FileIngestionResult IngestDelimited(string path, LogSource source, bool streaming, CancellationToken ct, char delimiter)
    {
        var result = new FileIngestionResult();
        TextReader reader = streaming ? new StreamReader(path) : new StringReader(File.ReadAllText(path));

        try
        {
            long recordIndex = -1;
            string[]? fields;
            while ((fields = DelimitedLineParser.ReadCsvRecord(reader, delimiter)) != null)
            {
                recordIndex++;
                if (recordIndex % CancellationCheckInterval == 0) ct.ThrowIfCancellationRequested();

                bool parsedOk = TimestampDetector.TryParseColumnTimestamp(fields, source.TimestampProfile, out var utc, out var originalText);

                if (recordIndex == 0 && !parsedOk)
                {
                    // First record with no parseable timestamp - almost certainly the header row.
                    continue;
                }

                if (!parsedOk)
                {
                    utc = DateTime.MinValue;
                    result.Warnings.Add(new IngestionWarning
                    {
                        FilePath = path,
                        LineNumber = (int)recordIndex + 1,
                        RawText = string.Join(",", fields),
                        Reason = $"Could not parse a timestamp from column {source.TimestampProfile.ColumnIndex} - block kept with a sentinel timestamp."
                    });
                }

                var message = string.Join(" | ", fields);
                var lines = message.Contains('\n')
                    ? message.Split('\n').Select(l => l.TrimEnd('\r')).ToList()
                    : new List<string> { message };

                result.Blocks.Add(new LogBlock
                {
                    UniversalTimestamp = utc,
                    OriginalTimestamp = originalText,
                    SourceId = source.Id,
                    SourceName = source.Name,
                    SourceColor = source.DisplayColor,
                    Lines = lines,
                    FullText = message,
                    TimestampParseFailed = !parsedOk,
                    SourceFilePath = path
                });
            }
        }
        finally
        {
            reader.Dispose();
        }

        return result;
    }

    // ===================================================================
    // Tab-delimited: a physical line containing at least one tab is treated as a record; a
    // line with no tabs is a continuation of the previous record (wrapped text).
    // ===================================================================
    private static FileIngestionResult IngestTabDelimited(string path, LogSource source, bool streaming, CancellationToken ct)
    {
        var result = new FileIngestionResult();
        List<string>? currentLines = null;
        DateTime currentUtc = DateTime.MinValue;
        string currentOriginal = string.Empty;
        bool currentFailed = false;
        long lineIndex = -1;

        void Flush()
        {
            if (currentLines is null) return;
            result.Blocks.Add(new LogBlock
            {
                UniversalTimestamp = currentUtc,
                OriginalTimestamp = currentOriginal,
                SourceId = source.Id,
                SourceName = source.Name,
                SourceColor = source.DisplayColor,
                Lines = currentLines,
                FullText = string.Join(Environment.NewLine, currentLines),
                TimestampParseFailed = currentFailed,
                SourceFilePath = path
            });
            currentLines = null;
        }

        void ProcessLine(string line)
        {
            lineIndex++;
            if (lineIndex % CancellationCheckInterval == 0) ct.ThrowIfCancellationRequested();

            var fields = DelimitedLineParser.SplitTab(line);
            if (fields.Length <= 1)
            {
                if (currentLines is not null) currentLines.Add(line);
                return;
            }

            bool parsedOk = TimestampDetector.TryParseColumnTimestamp(fields, source.TimestampProfile, out var utc, out var originalText);

            if (lineIndex == 0 && !parsedOk)
                return; // header row

            Flush();
            currentLines = new List<string> { string.Join(" | ", fields) };
            currentUtc = parsedOk ? utc : DateTime.MinValue;
            currentOriginal = originalText;
            currentFailed = !parsedOk;

            if (!parsedOk)
            {
                result.Warnings.Add(new IngestionWarning
                {
                    FilePath = path,
                    LineNumber = (int)lineIndex + 1,
                    RawText = line,
                    Reason = $"Could not parse a timestamp from column {source.TimestampProfile.ColumnIndex} - block kept with a sentinel timestamp."
                });
            }
        }

        if (streaming)
        {
            using var sr = new StreamReader(path);
            string? line;
            while ((line = sr.ReadLine()) != null)
                ProcessLine(line);
        }
        else
        {
            foreach (var line in File.ReadAllLines(path))
                ProcessLine(line);
        }

        Flush();

        if (result.Blocks.Count == 0 && lineIndex >= 0)
        {
            result.Warnings.Add(new IngestionWarning
            {
                FilePath = path,
                LineNumber = 0,
                Reason = "No line in this file matched the configured timestamp column - no blocks were created."
            });
        }

        return result;
    }
}
