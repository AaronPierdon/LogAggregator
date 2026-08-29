using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LogAggregator.Models;
using LogAggregator.Services;
using Xunit;

namespace LogAggregator.Tests;

/// <summary>
/// Tests IngestionService directly - not just TimestampDetector/FileTypeDetector (the wizard's
/// auto-detect step, already covered by TimestampFormatCatalogTests/AmbiguityDetectionTests) -
/// against a real temp-file-backed LogDatabase (SQLite), the same way the app itself does. This
/// is the actual production path that produces IngestionWarning, which is what
/// SourceLogType.HasError/ErrorMessage and the chip's error badge are driven from - so this is
/// the level "what should the error chip catch" needs to be answered at, not just detection.
///
/// Covers: blank lines, banner/preamble lines, multi-line continuation ("stack trace") blocks,
/// an all-garbage file, an empty file, and a truncated/short CSV row - the "noisy/malformed
/// files, catch file corruption" half of the request.
/// </summary>
public class IngestionServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly List<string> _dbPaths = new();

    public IngestionServiceTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "LogAggregator.Tests.Ingestion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    // ===================================================================
    // Test fixture helper - writes lines to a real file, builds a real (Source, LogType,
    // SourceLogType) triple, and runs them through the real IngestionService + a real
    // temp-file-backed SQLite LogDatabase. No shortcuts: this is exactly what happens when you
    // drop a file onto a source card.
    // ===================================================================

    private (LogDatabase Database, LogSource Source, LogType LogType, SourceIngestionResult Result) RunIngestion(
        string fileName, IEnumerable<string> lines, FileType fileType, TimestampProfile profile)
    {
        var filePath = Path.Combine(_workDir, fileName);
        File.WriteAllLines(filePath, lines);

        var dbPath = Path.Combine(_workDir, $"{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        var database = new LogDatabase(dbPath);
        database.Initialize();

        var source = new LogSource { Name = "TestSource" };
        var logType = new LogType { Name = "TestLogType", Format = fileType, TimestampProfile = profile };
        var binding = new SourceLogType { LogTypeId = logType.Id, FilePaths = new List<string> { filePath } };

        var service = new IngestionService(database);
        var result = service.IngestBindingAsync(source, logType, binding, CancellationToken.None)
            .GetAwaiter().GetResult();

        return (database, source, logType, result);
    }

    /// <summary>Auto-detects a real profile from well-formed sample lines, exactly like the
    /// wizard does (FileTypeDetector -> TimestampDetector.AutoDetectFromLines) - used so these
    /// tests exercise "a real, correctly-detected profile applied to a problematic file",
    /// rather than starting from an empty/fabricated profile.</summary>
    private static TimestampProfile DetectProfile(IReadOnlyList<string> sampleLines, FileType fileType, char delimiter = ',')
    {
        var result = TimestampDetector.AutoDetectFromLines(sampleLines.ToList(), fileType, delimiter);
        Assert.True(result.Candidates.Count > 0, $"Test setup failure: could not auto-detect a profile from the sample lines ({result.Status}: {result.Message}).");
        return result.Candidates[0].Profile;
    }

    // ===================================================================
    // Blank lines / preamble
    // ===================================================================

    [Fact]
    public void BlankLinesInterspersed_DoNotChangeBlockCount()
    {
        var wellFormed = Enumerable.Range(0, 8)
            .Select(i => $"2026-06-{i + 1:D2} 10:00:00 INFO worker: tick {i}")
            .ToList();
        var profile = DetectProfile(wellFormed, FileType.FlatText);

        var withBlanks = new List<string>();
        foreach (var line in wellFormed)
        {
            withBlanks.Add(line);
            withBlanks.Add(""); // blank line after every entry
        }

        var (database, source, logType, result) = RunIngestion("blank-lines.txt", withBlanks, FileType.FlatText, profile);

        Assert.Equal(8, database.CountForBinding(source.Id, logType.Id));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BannerPreambleBeforeFirstTimestamp_IsDroppedNotCounted()
    {
        var wellFormed = Enumerable.Range(0, 5)
            .Select(i => $"2026-06-{i + 1:D2} 10:00:00 INFO worker: tick {i}")
            .ToList();
        var profile = DetectProfile(wellFormed, FileType.FlatText);

        var withBanner = new List<string> { "=== Log started ===", "", "build 4.2.1" };
        withBanner.AddRange(wellFormed);

        var (database, source, logType, result) = RunIngestion("banner.txt", withBanner, FileType.FlatText, profile);

        Assert.Equal(5, database.CountForBinding(source.Id, logType.Id));
        Assert.Empty(result.Warnings);
    }

    // ===================================================================
    // Multi-line ("stack trace"-style) continuation blocks
    // ===================================================================

    [Fact]
    public void MultiLineContinuationBlocks_FoldIntoPrecedingBlock_NotCountedSeparately()
    {
        var wellFormed = Enumerable.Range(0, 4)
            .Select(i => $"2026-06-{i + 1:D2} 10:00:00 ERROR worker: unhandled exception")
            .ToList();
        var profile = DetectProfile(wellFormed, FileType.FlatText);

        var lines = new List<string>();
        foreach (var line in wellFormed)
        {
            lines.Add(line);
            lines.Add("   at Worker.Run()");
            lines.Add("   at Program.Main()");
        }

        var (database, source, logType, result) = RunIngestion("stacktraces.txt", lines, FileType.FlatText, profile);

        Assert.Equal(4, database.CountForBinding(source.Id, logType.Id));
        Assert.Empty(result.Warnings);

        var blocks = database.QueryAllMatchingUnbuffered(
            new[] { source.Id }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            SortColumn.UniversalTimestamp, descending: false).ToList();

        Assert.Equal(4, blocks.Count);
        foreach (var block in blocks)
        {
            var blockLines = block.FullText.Split('\n');
            Assert.Equal(3, blockLines.Length); // the timestamped line + its 2 continuation lines
            Assert.Contains("at Worker.Run()", block.FullText);
            Assert.Contains("at Program.Main()", block.FullText);
        }
    }

    // ===================================================================
    // Whole-file failure modes
    // ===================================================================

    [Fact]
    public void AllGarbageFile_ProducesExactlyOneNoMatchWarning_AndNoBlocks()
    {
        var reference = Enumerable.Range(0, 5)
            .Select(i => $"2026-06-{i + 1:D2} 10:00:00 INFO worker: tick {i}")
            .ToList();
        var profile = DetectProfile(reference, FileType.FlatText);

        var garbage = new List<string>
        {
            "the quick brown fox",
            "jumps over the lazy dog",
            "no timestamps anywhere in this file",
            "just prose"
        };

        var (database, source, logType, result) = RunIngestion("garbage.txt", garbage, FileType.FlatText, profile);

        Assert.Equal(0, database.CountForBinding(source.Id, logType.Id));
        var warning = Assert.Single(result.Warnings);
        Assert.True(warning.RequiresUserAction);
        Assert.Contains("No line in this file matched", warning.Reason);
    }

    [Fact]
    public void EmptyFile_ProducesNoBlocksButNowWarns()
    {
        // Was a real gap this same test file found: IngestFlatText used to guard its "no line
        // matched" warning behind "lineIndex > 0", so a genuinely empty (0-line) file produced
        // neither blocks nor a warning - indistinguishable anywhere in the UI from "this
        // binding just has no files yet". Fixed in IngestionService.cs (all three Ingest*
        // methods) - a zero-byte file (e.g. truncated mid-write by something upstream) now
        // always produces exactly one warning, distinct in wording from "had content but
        // nothing matched".
        var reference = Enumerable.Range(0, 3)
            .Select(i => $"2026-06-{i + 1:D2} 10:00:00 INFO worker: tick {i}")
            .ToList();
        var profile = DetectProfile(reference, FileType.FlatText);

        var (database, source, logType, result) = RunIngestion("empty.txt", Array.Empty<string>(), FileType.FlatText, profile);

        Assert.Equal(0, database.CountForBinding(source.Id, logType.Id));
        var warning = Assert.Single(result.Warnings);
        Assert.True(warning.RequiresUserAction);
        Assert.Contains("empty", warning.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyCsvFile_ProducesNoBlocksButNowWarns()
    {
        var reference = new List<string> { "TimeStamp,Message" };
        reference.AddRange(Enumerable.Range(0, 4).Select(i => $"2026-06-{i + 1:D2} 10:00:00,tick {i}"));
        var profile = DetectProfile(reference, FileType.CSV);

        var (database, source, logType, result) = RunIngestion("empty.csv", Array.Empty<string>(), FileType.CSV, profile);

        Assert.Equal(0, database.CountForBinding(source.Id, logType.Id));
        var warning = Assert.Single(result.Warnings);
        Assert.True(warning.RequiresUserAction);
        Assert.Contains("empty", warning.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyTabDelimitedFile_ProducesNoBlocksButNowWarns()
    {
        var reference = new List<string> { "TimeStamp\tMessage" };
        reference.AddRange(Enumerable.Range(0, 4).Select(i => $"2026-06-{i + 1:D2} 10:00:00\ttick {i}"));
        var profile = DetectProfile(reference, FileType.TabDelimited, delimiter: '\t');

        var (database, source, logType, result) = RunIngestion("empty.tsv", Array.Empty<string>(), FileType.TabDelimited, profile);

        Assert.Equal(0, database.CountForBinding(source.Id, logType.Id));
        var warning = Assert.Single(result.Warnings);
        Assert.True(warning.RequiresUserAction);
        Assert.Contains("empty", warning.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ===================================================================
    // Truncated / corrupted CSV row
    // ===================================================================

    [Fact]
    public void TruncatedCsvRow_BecomesSentinelWarning_NotCrashOrDroppedRow()
    {
        // Timestamp deliberately in the RIGHTMOST column (index 2) - both to match "predominantly
        // left, but not exclusively" from the earlier generator work, and because it's what
        // makes a truncated (short) row actually exercise TryParseColumnTimestamp's bounds
        // check: a row missing its later columns has no column 2 to read at all, simulating a
        // line cut off mid-write (a classic real-world file-corruption symptom).
        var lines = new List<string> { "Message,OtherData,TimeStamp" };
        for (int i = 0; i < 8; i++)
        {
            if (i == 5)
            {
                lines.Add("truncated mid-write"); // only 1 field - no OtherData, no TimeStamp column at all
            }
            else
            {
                lines.Add($"tick {i},tag=t{i},2026-06-{i + 1:D2} 10:00:00");
            }
        }

        var profile = DetectProfile(lines, FileType.CSV);
        Assert.Equal(TimestampLocationMode.DelimitedColumn, profile.Mode);
        Assert.Equal(2, profile.ColumnIndex); // sanity check detection actually found the rightmost column

        var (database, source, logType, result) = RunIngestion("truncated.csv", lines, FileType.CSV, profile);

        // All 8 data rows still become blocks - a parse failure keeps the row with a sentinel
        // timestamp rather than silently dropping it (see IngestDelimited).
        Assert.Equal(8, database.CountForBinding(source.Id, logType.Id));

        var warning = Assert.Single(result.Warnings);
        Assert.True(warning.RequiresUserAction);
        Assert.Equal("truncated mid-write", warning.RawText);
    }
}
