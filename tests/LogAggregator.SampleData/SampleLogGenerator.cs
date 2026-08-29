using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LogAggregator.Models;

namespace LogAggregator.SampleData;

/// <summary>SIMULATION SUPPORT - one generated file: its path once written, which format/column
/// it used, and (critically) the known-correct UTC value for every data line, so
/// SimulationRunner can check the real engine's parsed value against ground truth rather than
/// just "did something parse."</summary>
public sealed class GeneratedFile
{
    public string FilePath { get; set; } = string.Empty;
    public FileType FileType { get; init; }
    public SampleTimestampFormat Format { get; init; } = null!;

    /// <summary>CSV/TabDelimited only: which column (0-based) holds the timestamp. -1 for FlatText.</summary>
    public int TimestampColumnIndex { get; init; } = -1;

    /// <summary>FlatText only: whether the timestamp was wrapped in "[...]".</summary>
    public bool UsedBrackets { get; init; }

    public List<string> Lines { get; init; } = new();

    /// <summary>Parallel to Lines. Null for a non-data line (the CSV header row); the known-
    /// correct UTC instant for every data line.</summary>
    public List<DateTime?> ExpectedUtcPerLine { get; init; } = new();
}

/// <summary>SIMULATION SUPPORT - two files (one CSV, one TXT) that describe the SAME underlying
/// log records, per the user's request: "the first pair, csv and txt will have the same logs."
/// Each file still picks its own timestamp shape/column/location independently.</summary>
public sealed class GeneratedPair
{
    public string Name { get; init; } = string.Empty;
    public List<RandomLogRecord> Records { get; init; } = new();
    public GeneratedFile CsvFile { get; set; } = null!;
    public GeneratedFile TxtFile { get; set; } = null!;
}

/// <summary>SIMULATION SUPPORT - "4 files per group" (2 CSV + 2 TXT, as two same-content
/// pairs). Every file in a group uses a different timestamp shape from the catalog so one
/// GenerateGroup() call exercises 4 distinct shapes at once.</summary>
public sealed class GeneratedGroup
{
    public int GroupIndex { get; init; }
    public GeneratedPair PairA { get; init; } = null!;
    public GeneratedPair PairB { get; init; } = null!;

    public IEnumerable<GeneratedFile> AllFiles()
    {
        yield return PairA.CsvFile;
        yield return PairA.TxtFile;
        yield return PairB.CsvFile;
        yield return PairB.TxtFile;
    }
}

/// <summary>
/// SIMULATION SUPPORT - generates groups of synthetic log files (random content + random-but-
/// tracked timestamp patterns) used to stress-test TimestampDetector via SimulationRunner. Fully
/// deterministic for a given seed, so a failing run can be reproduced exactly by reusing the
/// same seed.
///
/// Column layout: per the request, columns are "predominantly on the left" (the timestamp is
/// column 0 about 70% of the time) because the point of this generator is to exercise
/// TIMESTAMP PATTERN variety, not column-position variety - the app's column search already
/// loops over every column regardless of position, so a little position variety is enough to
/// confirm that still works without making position the focus.
///
/// Field content deliberately never contains a comma, tab, or quote character, so plain
/// comma/tab splitting is always correct and no RFC4180 quoting logic is needed here (the real
/// app's DelimitedLineParser does support quoting - this generator just doesn't need to
/// exercise that, and keeping it out keeps this class simple per "don't overcomplicate").
/// </summary>
public sealed class SampleLogGenerator
{
    /// <summary>The seed this generator was constructed with - same seed, same generated
    /// files, always (see the class doc comment).</summary>
    public int Seed { get; }

    private readonly Random _rng;
    private readonly RandomDataFactory _dataFactory;

    private readonly List<SampleTimestampFormat> _delimitedPool;
    private readonly List<SampleTimestampFormat> _allPool;
    private int _delimitedCursor;
    private int _allCursor;

    public SampleLogGenerator(int seed)
    {
        Seed = seed;
        _rng = new Random(seed);
        // A different (but still deterministic) seed than _rng, so the two random streams
        // (layout/format choices vs. record content) don't shadow each other.
        _dataFactory = new RandomDataFactory(unchecked(seed * 31 + 17));

        _delimitedPool = Shuffle(TimestampFormatCatalog.DelimitedEligible().ToList());
        // Not TimestampFormatCatalog.All - a handful of shapes (currently just "compact") are
        // documented as not auto-detectable at all (see SupportsAutoDetection), so drawing them
        // here would just generate a guaranteed, already-understood "failure" on the TXT side
        // too, for both the sweep report and TimestampFormatCatalogTests.
        _allPool = Shuffle(TimestampFormatCatalog.AutoDetectionEligible().ToList());
    }

    public List<GeneratedGroup> GenerateGroups(int groupCount, int recordsPerFile = 30)
    {
        var groups = new List<GeneratedGroup>(groupCount);
        for (int i = 1; i <= groupCount; i++)
            groups.Add(GenerateGroup(i, recordsPerFile));
        return groups;
    }

    public GeneratedGroup GenerateGroup(int groupIndex, int recordsPerFile = 30)
    {
        return new GeneratedGroup
        {
            GroupIndex = groupIndex,
            PairA = GeneratePair($"group{groupIndex}-pairA", recordsPerFile),
            PairB = GeneratePair($"group{groupIndex}-pairB", recordsPerFile)
        };
    }

    /// <summary>Writes every file in the group to <paramref name="outputDir"/> and fills in
    /// each GeneratedFile.FilePath. Safe to call more than once with a fresh directory.</summary>
    public void WriteToDisk(GeneratedGroup group, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WriteFile(group.PairA.CsvFile, outputDir, $"group{group.GroupIndex}_pairA");
        WriteFile(group.PairA.TxtFile, outputDir, $"group{group.GroupIndex}_pairA");
        WriteFile(group.PairB.CsvFile, outputDir, $"group{group.GroupIndex}_pairB");
        WriteFile(group.PairB.TxtFile, outputDir, $"group{group.GroupIndex}_pairB");
    }

    /// <summary>Writes one already-built GeneratedFile to disk and fills in its FilePath.
    /// Public so targeted single-format tests (see GenerateSingleCsvFile/GenerateSingleTxtFile
    /// below) can write a file without going through a full GenerateGroup/WriteToDisk pass.</summary>
    public static void WriteFile(GeneratedFile file, string outputDir, string baseName)
    {
        Directory.CreateDirectory(outputDir);
        var ext = file.FileType == FileType.CSV ? "csv" : "txt";
        file.FilePath = Path.Combine(outputDir, $"{baseName}_{file.Format.Name}.{ext}");
        File.WriteAllLines(file.FilePath, file.Lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private GeneratedPair GeneratePair(string name, int recordsPerFile)
    {
        var csvFormat = DrawFrom(_delimitedPool, ref _delimitedCursor);
        var txtFormat = DrawDistinctFrom(_allPool, ref _allCursor, csvFormat);

        // Only the TXT format can be a "no year in the text" shape (see catalog comments) - if
        // it is, every record in this pair must land in the CURRENT year, because that's what
        // TimestampDetector itself fills in for a yearless format. Harmless for the CSV side,
        // which always embeds a real year regardless.
        int? forceYear = txtFormat.HasYear ? null : DateTime.UtcNow.Year;

        var yearOffset = _rng.Next(-3, 4); // years, so 2-digit-year / epoch-digit-count stay well-behaved
        var year = forceYear ?? (DateTime.UtcNow.Year + yearOffset);
        var startUtc = RandomDateInYear(year);
        var avgInterval = TimeSpan.FromSeconds(_rng.Next(5, 31));

        var records = _dataFactory.CreateRecords(recordsPerFile, startUtc, avgInterval, forceYear);

        var csvFile = BuildCsvFile(records, csvFormat);
        var txtFile = BuildTxtFile(records, txtFormat);

        return new GeneratedPair { Name = name, Records = records, CsvFile = csvFile, TxtFile = txtFile };
    }

    // ===================================================================
    // CSV rendering
    // ===================================================================

    private enum FieldKind { TimeStamp, Message, Other }

    private GeneratedFile BuildCsvFile(List<RandomLogRecord> records, SampleTimestampFormat format, TimeSpan? forcedOffsetForFirstRecord = null)
    {
        var order = RandomColumnOrder();
        var tsColumn = Array.IndexOf(order, FieldKind.TimeStamp);

        var lines = new List<string>(records.Count + 1);
        var expected = new List<DateTime?>(records.Count + 1);

        lines.Add(string.Join(",", order.Select(HeaderFor)));
        expected.Add(null); // header row has no timestamp

        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            var record = records[recordIndex];
            // Only ever non-null for boundary tests (see GenerateBoundaryCsvFile) - forces the
            // FIRST record's rendered offset instead of a random one, everything else is
            // unaffected.
            var forcedOffset = recordIndex == 0 ? forcedOffsetForFirstRecord : null;
            var rendered = TimestampRenderer.Render(format, record.TimestampUtc, _rng, forcedOffset);
            var fields = new string[3];
            foreach (var (kind, index) in order.Select((k, i) => (k, i)))
            {
                fields[index] = kind switch
                {
                    FieldKind.TimeStamp => rendered.Text,
                    FieldKind.Message => record.Message,
                    FieldKind.Other => record.OtherData,
                    _ => throw new InvalidOperationException()
                };
            }
            lines.Add(string.Join(",", fields));
            expected.Add(rendered.ExpectedUtc);
        }

        return new GeneratedFile
        {
            FileType = FileType.CSV,
            Format = format,
            TimestampColumnIndex = tsColumn,
            Lines = lines,
            ExpectedUtcPerLine = expected
        };
    }

    private static string HeaderFor(FieldKind kind) => kind switch
    {
        FieldKind.TimeStamp => "TimeStamp",
        FieldKind.Message => "Message",
        FieldKind.Other => "OtherData",
        _ => throw new InvalidOperationException()
    };

    /// <summary>[TimeStamp, Message, Other] ~70% of the time, timestamp in position 1 or 2 the
    /// rest - "predominantly on the left" per the request, since this generator is about
    /// pattern variety, not column-position variety.</summary>
    private FieldKind[] RandomColumnOrder()
    {
        var roll = _rng.NextDouble();
        if (roll < 0.70) return new[] { FieldKind.TimeStamp, FieldKind.Message, FieldKind.Other };
        if (roll < 0.85) return new[] { FieldKind.Message, FieldKind.TimeStamp, FieldKind.Other };
        return new[] { FieldKind.Message, FieldKind.Other, FieldKind.TimeStamp };
    }

    // ===================================================================
    // TXT (FlatText) rendering
    // ===================================================================

    private GeneratedFile BuildTxtFile(List<RandomLogRecord> records, SampleTimestampFormat format, TimeSpan? forcedOffsetForFirstRecord = null)
    {
        // Epoch shapes have no bracketed variant in the real detector (LeadingTimestampRegexEpoch
        // has no [ ]-wrapped form), so only offer brackets for the other shapes.
        var useBrackets = !format.IsEpoch && _rng.NextDouble() < 0.30;

        var lines = new List<string>(records.Count);
        var expected = new List<DateTime?>(records.Count);

        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            var record = records[recordIndex];
            var forcedOffset = recordIndex == 0 ? forcedOffsetForFirstRecord : null;
            var rendered = TimestampRenderer.Render(format, record.TimestampUtc, _rng, forcedOffset);
            var tsText = useBrackets ? $"[{rendered.Text}]" : rendered.Text;
            // "just a whole line of info no tabs" - message + other data folded into one
            // free-text tail, exactly like the app's real FlatText samples (see /samples).
            lines.Add($"{tsText} {record.Message} | {record.OtherData}");
            expected.Add(rendered.ExpectedUtc);
        }

        return new GeneratedFile
        {
            FileType = FileType.FlatText,
            Format = format,
            TimestampColumnIndex = -1,
            UsedBrackets = useBrackets,
            Lines = lines,
            ExpectedUtcPerLine = expected
        };
    }

    // ===================================================================
    // Single-format generation, used by TimestampFormatCatalogTests for one [Theory] case
    // per catalog entry (see LogAggregator.Tests) - independent of the group/pair cycling
    // logic above, since each test targets one specific, named format.
    // ===================================================================

    /// <summary>Generates one CSV file using exactly the given format. Throws for a
    /// FlatText-only format (SupportsDelimited = false) - use GenerateSingleTxtFile for those.</summary>
    public GeneratedFile GenerateSingleCsvFile(SampleTimestampFormat format, int recordsPerFile = 15)
    {
        if (!format.SupportsDelimited)
            throw new ArgumentException($"'{format.Name}' is FlatText-only (SupportsDelimited = false) - use GenerateSingleTxtFile instead.", nameof(format));

        return BuildCsvFile(CreateRecordsFor(format, recordsPerFile), format);
    }

    /// <summary>Generates one TXT (FlatText) file using exactly the given format. Every shape
    /// in the catalog supports FlatText.</summary>
    public GeneratedFile GenerateSingleTxtFile(SampleTimestampFormat format, int recordsPerFile = 15)
    {
        return BuildTxtFile(CreateRecordsFor(format, recordsPerFile), format);
    }

    private List<RandomLogRecord> CreateRecordsFor(SampleTimestampFormat format, int recordsPerFile)
    {
        int? forceYear = format.HasYear ? null : DateTime.UtcNow.Year;
        var year = forceYear ?? (DateTime.UtcNow.Year + _rng.Next(-3, 4));
        var startUtc = RandomDateInYear(year);
        var avgInterval = TimeSpan.FromSeconds(_rng.Next(5, 31));
        return _dataFactory.CreateRecords(recordsPerFile, startUtc, avgInterval, forceYear);
    }

    // ===================================================================
    // Boundary/extreme-value generation - deliberately separate from GeneratePair/GenerateGroup
    // above: each call pins ONE exact DateTime (and, for offset formats, one exact offset) into
    // the FIRST record of an otherwise-ordinary small file, instead of leaving everything to
    // the random draw. The rest of the file is normal random data from the same format, so
    // detection still gets a realistic multi-line sample to vote across, not a single isolated
    // line. Used by BoundaryTimestampTests for cases like year rollover, Feb 29, and the real
    // legal UTC offset extremes (+14:00, -12:00) that the random offset list might reach only
    // by chance, or never.
    // ===================================================================

    /// <summary>Builds a small TXT (FlatText) file whose first record uses exactly
    /// <paramref name="utc"/> (and, for an offset-bearing format, exactly
    /// <paramref name="forcedOffset"/>). The injected record is always Lines[0].</summary>
    public GeneratedFile GenerateBoundaryTxtFile(SampleTimestampFormat format, DateTime utc, TimeSpan? forcedOffset = null, int extraRecords = 6)
    {
        var records = InjectBoundaryRecord(format, utc, extraRecords);
        return BuildTxtFile(records, format, forcedOffset);
    }

    /// <summary>Builds a small CSV file whose first record uses exactly <paramref name="utc"/>
    /// (and, for an offset-bearing format, exactly <paramref name="forcedOffset"/>). The
    /// injected record is always Lines[1] (Lines[0] is the header row). Throws for a
    /// FlatText-only format, same as GenerateSingleCsvFile.</summary>
    public GeneratedFile GenerateBoundaryCsvFile(SampleTimestampFormat format, DateTime utc, TimeSpan? forcedOffset = null, int extraRecords = 6)
    {
        if (!format.SupportsDelimited)
            throw new ArgumentException($"'{format.Name}' is FlatText-only (SupportsDelimited = false) - use GenerateBoundaryTxtFile instead.", nameof(format));

        var records = InjectBoundaryRecord(format, utc, extraRecords);
        return BuildCsvFile(records, format, forcedOffset);
    }

    /// <summary>Generates <paramref name="extraRecords"/> ordinary records for realistic
    /// voting context, then replaces record 0's timestamp with <paramref name="utc"/> exactly
    /// (keeping its generated message/other-data text, since only the timestamp is under
    /// test).</summary>
    private List<RandomLogRecord> InjectBoundaryRecord(SampleTimestampFormat format, DateTime utc, int extraRecords)
    {
        var records = CreateRecordsFor(format, Math.Max(1, extraRecords + 1));
        records[0] = new RandomLogRecord
        {
            TimestampUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            Message = records[0].Message,
            OtherData = records[0].OtherData
        };
        return records;
    }

    // ===================================================================
    // Small helpers
    // ===================================================================

    private DateTime RandomDateInYear(int year)
    {
        var jan1 = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        var dayOffset = _rng.Next(0, daysInYear);
        var secondOffset = _rng.Next(0, 24 * 60 * 60);
        return jan1.AddDays(dayOffset).AddSeconds(secondOffset);
    }

    private SampleTimestampFormat DrawFrom(List<SampleTimestampFormat> pool, ref int cursor)
    {
        if (cursor >= pool.Count)
        {
            Shuffle(pool);
            cursor = 0;
        }
        return pool[cursor++];
    }

    /// <summary>Draws from <paramref name="pool"/>, retrying (up to a handful of times) to
    /// avoid handing back the same format as <paramref name="other"/> - keeps each group's 4
    /// files exercising 4 different shapes in the common case. Not load-bearing for
    /// correctness (a duplicate is harmless), just for better coverage per run.</summary>
    private SampleTimestampFormat DrawDistinctFrom(List<SampleTimestampFormat> pool, ref int cursor, SampleTimestampFormat other)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var candidate = DrawFrom(pool, ref cursor);
            if (candidate.Name != other.Name) return candidate;
        }
        return DrawFrom(pool, ref cursor);
    }

    private List<SampleTimestampFormat> Shuffle(List<SampleTimestampFormat> list)
    {
        // Fisher-Yates, using this generator's own seeded Random so the shuffle order is
        // reproducible for a given seed.
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
