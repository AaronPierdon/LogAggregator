using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Reads all files belonging to one LogType binding on one Source, detects log-entry (block)
/// boundaries per that LogType's format, normalizes timestamps per that LogType's timestamp
/// profile, and writes rows into SQLite in small batches as it goes. This is the core fix for
/// the "36 million lines eats all my RAM" problem: at no point does this class hold more than
/// one batch (~2000 rows) of LogBlocks in memory, regardless of how large the source file is or
/// how many rows it ultimately produces.
///
/// Ingestion is scoped to one (Source, LogType) binding rather than a whole Source card, so
/// dropping a file onto one LogType's chip only re-syncs that binding - a source's other bound
/// LogTypes keep their already-ingested rows untouched. Within a binding, files still parse in
/// parallel (writes are serialized inside LogDatabase, since SQLite allows one writer at a time
/// - see LogDatabase.cs).
/// </summary>
public class IngestionService
{
    /// <summary>Files at or above this size stream line-by-line (StreamReader) instead of being
    /// fully buffered (File.ReadAllLines) for the *read* side. Named constant - easy to tune.</summary>
    public const long StreamingThresholdBytes = 10L * 1024 * 1024; // 10 MB

    /// <summary>How many parsed blocks accumulate in memory before being flushed to SQLite as
    /// one batch/transaction. This - not file size - is what actually bounds ingestion memory.</summary>
    private const int WriteBatchSize = 2000;

    /// <summary>How often (in records/lines) cancellation is checked - frequent enough to cancel
    /// promptly, infrequent enough not to add meaningful overhead.</summary>
    private const int CancellationCheckInterval = 1000;

    private readonly LogDatabase _database;

    public IngestionService(LogDatabase database)
    {
        _database = database;
    }

    /// <summary>Ingests one LogType binding's files for one Source. Re-sync replaces this
    /// binding's previous rows outright, rather than trying to diff which files/lines changed -
    /// simple and correct, at the cost of a full re-parse.</summary>
    public async Task<SourceIngestionResult> IngestBindingAsync(
        LogSource source, LogType logType, SourceLogType binding, CancellationToken ct)
    {
        _database.DeleteBlocksForBinding(source.Id, logType.Id);

        var allWarnings = new List<IngestionWarning>();
        var sync = new object();

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        await Parallel.ForEachAsync(binding.FilePaths, options, async (path, token) =>
        {
            var fileResult = await IngestFileAsync(path, source, logType, token).ConfigureAwait(false);
            lock (sync)
            {
                allWarnings.AddRange(fileResult.Warnings);
            }
        }).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        var totalCount = _database.CountForBinding(source.Id, logType.Id);
        return new SourceIngestionResult { Warnings = allWarnings, TotalCount = totalCount };
    }

    private Task<FileIngestionResult> IngestFileAsync(string path, LogSource source, LogType logType, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var info = new FileInfo(path);
            bool streaming = info.Exists && info.Length >= StreamingThresholdBytes;

            return logType.Format switch
            {
                FileType.CSV => IngestDelimited(path, source, logType, streaming, ct, delimiter: ','),
                FileType.TabDelimited => IngestTabDelimited(path, source, logType, streaming, ct),
                _ => IngestFlatText(path, source, logType, streaming, ct)
            };
        }, ct);
    }

    private void FlushBatch(List<LogBlock> pendingBatch)
    {
        if (pendingBatch.Count == 0) return;
        _database.InsertBatch(pendingBatch);
        pendingBatch.Clear();
    }

    private static LogBlock NewBlock(LogSource source, LogType logType, string path) => new()
    {
        SourceId = source.Id,
        SourceName = source.Name,
        SourceColor = source.DisplayColor,
        LogTypeId = logType.Id,
        LogTypeName = logType.Name,
        LogTypeColor = logType.ColorHex,
        LogTypeIcon = logType.IconGlyph,
        SourceFilePath = path
    };

    // ===================================================================
    // FlatText: a line starting with a matching timestamp begins a new block;
    // everything else is a continuation of the current block.
    // ===================================================================
    private FileIngestionResult IngestFlatText(string path, LogSource source, LogType logType, bool streaming, CancellationToken ct)
    {
        var result = new FileIngestionResult();
        var pendingBatch = new List<LogBlock>(WriteBatchSize);
        long insertedCount = 0;

        List<string>? currentLines = null;
        DateTime currentUtc = DateTime.MinValue;
        string currentOriginal = string.Empty;
        bool currentFailed = false;
        long lineIndex = 0;

        void Flush()
        {
            if (currentLines is null) return;
            var block = NewBlock(source, logType, path);
            block.UniversalTimestamp = currentUtc;
            block.OriginalTimestamp = currentOriginal;
            block.FullText = string.Join('\n', currentLines);
            block.TimestampParseFailed = currentFailed;
            pendingBatch.Add(block);
            insertedCount++;
            currentLines = null;

            if (pendingBatch.Count >= WriteBatchSize) FlushBatch(pendingBatch);
        }

        void ProcessLine(string line)
        {
            lineIndex++;
            if (lineIndex % CancellationCheckInterval == 0) ct.ThrowIfCancellationRequested();

            var eval = TimestampDetector.EvaluateLine(line, logType.TimestampProfile);
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
                        SourceId = source.Id,
                        LogTypeId = logType.Id,
                        FilePath = path,
                        LineNumber = (int)lineIndex,
                        RawText = line,
                        Reason = $"Timestamp-shaped text \"{eval.RawText}\" could not be parsed - block kept with a sentinel timestamp.",
                        RequiresUserAction = true
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
        FlushBatch(pendingBatch);

        // FIX (found via IngestionServiceTests.EmptyFile_* - the generated-data simulation
        // work turned into direct IngestionService testing): this used to be
        // "lineIndex > 0 && insertedCount == 0", which meant a genuinely empty (0-line) file -
        // or one truncated to 0 bytes mid-write, a real "file corruption" symptom - produced
        // NEITHER a block NOR a warning, silently. That's indistinguishable from "this binding
        // just doesn't have any files yet" anywhere in the UI. Now it always warns when zero
        // blocks resulted, with a message that tells the two situations apart.
        if (insertedCount == 0)
        {
            result.Warnings.Add(new IngestionWarning
            {
                SourceId = source.Id,
                LogTypeId = logType.Id,
                FilePath = path,
                LineNumber = 0,
                Reason = lineIndex == 0
                    ? "This file is empty (0 lines) - no blocks were created. If you expected data here, the file may still be being written, or something upstream may have truncated it."
                    : $"No line in this file matched \"{logType.Name}\"'s configured timestamp pattern - no blocks were created. Check the pattern in the LogTypes window.",
                RequiresUserAction = true
            });
        }

        return result;
    }

    // ===================================================================
    // CSV: real RFC4180 records (embedded commas/newlines inside quotes are data, not new
    // records or blocks). Each record is its own block.
    // ===================================================================
    private FileIngestionResult IngestDelimited(string path, LogSource source, LogType logType, bool streaming, CancellationToken ct, char delimiter)
    {
        var result = new FileIngestionResult();
        var pendingBatch = new List<LogBlock>(WriteBatchSize);
        long insertedCount = 0;
        TextReader reader = streaming ? new StreamReader(path) : new StringReader(File.ReadAllText(path));
        long recordIndex = -1;

        try
        {
            string[]? fields;
            while ((fields = DelimitedLineParser.ReadCsvRecord(reader, delimiter)) != null)
            {
                recordIndex++;
                if (recordIndex % CancellationCheckInterval == 0) ct.ThrowIfCancellationRequested();

                bool parsedOk = TimestampDetector.TryParseColumnTimestamp(fields, logType.TimestampProfile, out var utc, out var originalText);

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
                        SourceId = source.Id,
                        LogTypeId = logType.Id,
                        FilePath = path,
                        LineNumber = (int)recordIndex + 1,
                        RawText = string.Join(",", fields),
                        Reason = $"Could not parse a timestamp from column {logType.TimestampProfile.ColumnIndex} - block kept with a sentinel timestamp.",
                        RequiresUserAction = true
                    });
                }

                var message = string.Join(" | ", fields);

                var block = NewBlock(source, logType, path);
                block.UniversalTimestamp = utc;
                block.OriginalTimestamp = originalText;
                block.FullText = message;
                block.TimestampParseFailed = !parsedOk;
                pendingBatch.Add(block);
                insertedCount++;

                if (pendingBatch.Count >= WriteBatchSize) FlushBatch(pendingBatch);
            }
        }
        finally
        {
            reader.Dispose();
        }

        FlushBatch(pendingBatch);

        // See the matching comment in IngestFlatText above - this used to require
        // "recordIndex >= 0" (at least one record read at all), so a genuinely empty CSV file
        // produced no warning either. Now it always warns when zero blocks resulted.
        if (insertedCount == 0)
        {
            result.Warnings.Add(new IngestionWarning
            {
                SourceId = source.Id,
                LogTypeId = logType.Id,
                FilePath = path,
                LineNumber = 0,
                Reason = recordIndex < 0
                    ? "This file is empty (0 records) - no blocks were created. If you expected data here, the file may still be being written, or something upstream may have truncated it."
                    : "No record in this file produced a parseable timestamp - no blocks were created.",
                RequiresUserAction = true
            });
        }

        return result;
    }

    // ===================================================================
    // Tab-delimited: a physical line containing at least one tab is treated as a record; a
    // line with no tabs is a continuation of the previous record (wrapped text).
    // ===================================================================
    private FileIngestionResult IngestTabDelimited(string path, LogSource source, LogType logType, bool streaming, CancellationToken ct)
    {
        var result = new FileIngestionResult();
        var pendingBatch = new List<LogBlock>(WriteBatchSize);
        long insertedCount = 0;

        List<string>? currentLines = null;
        DateTime currentUtc = DateTime.MinValue;
        string currentOriginal = string.Empty;
        bool currentFailed = false;
        long lineIndex = -1;

        void Flush()
        {
            if (currentLines is null) return;
            var block = NewBlock(source, logType, path);
            block.UniversalTimestamp = currentUtc;
            block.OriginalTimestamp = currentOriginal;
            block.FullText = string.Join('\n', currentLines);
            block.TimestampParseFailed = currentFailed;
            pendingBatch.Add(block);
            insertedCount++;
            currentLines = null;

            if (pendingBatch.Count >= WriteBatchSize) FlushBatch(pendingBatch);
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

            bool parsedOk = TimestampDetector.TryParseColumnTimestamp(fields, logType.TimestampProfile, out var utc, out var originalText);

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
                    SourceId = source.Id,
                    LogTypeId = logType.Id,
                    FilePath = path,
                    LineNumber = (int)lineIndex + 1,
                    RawText = line,
                    Reason = $"Could not parse a timestamp from column {logType.TimestampProfile.ColumnIndex} - block kept with a sentinel timestamp.",
                    RequiresUserAction = true
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
        FlushBatch(pendingBatch);

        // Same fix as IngestFlatText/IngestDelimited above.
        if (insertedCount == 0)
        {
            result.Warnings.Add(new IngestionWarning
            {
                SourceId = source.Id,
                LogTypeId = logType.Id,
                FilePath = path,
                LineNumber = 0,
                Reason = lineIndex < 0
                    ? "This file is empty (0 lines) - no blocks were created. If you expected data here, the file may still be being written, or something upstream may have truncated it."
                    : "No line in this file matched the configured timestamp column - no blocks were created.",
                RequiresUserAction = true
            });
        }

        return result;
    }
}
