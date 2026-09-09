using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Generates synthetic log files for demoing and stress-testing the app itself - the Settings
/// window's Developer tab, "Generate Mock Log Files". Every invented server name is prefixed
/// "MOCK-", and every invented IP address is drawn from the three IPv4 ranges RFC 5737 reserves
/// specifically for documentation and example use (192.0.2.0/24, 198.51.100.0/24,
/// 203.0.113.0/24) - real, valid-looking dotted-quad addresses guaranteed to never be routable
/// or assigned to anyone, so a screenshot or screen recording built from this data can never
/// leak a real hostname or IP even by accident.
///
/// Deliberately independent of LogAggregator.SampleData (the tests/ simulation project): that
/// project only exists for the opt-in build-time simulation harness (see App.xaml.cs /
/// SIMULATION_MODE in LogAggregator.csproj) and is not referenced by a normal build, so this
/// class is fully self-contained and ships in every build, unconditionally.
///
/// A generated file is just a normal file afterward - there's no special integration with the
/// app's ingestion pipeline. Drag the output folder's files onto the main window's drop zone (or
/// use "+ Add Source" / "Manage Log Types") exactly as you would with any real log file.
/// </summary>
public static class MockLogGeneratorService
{
    // ===================================================================
    // Timestamp shape catalog
    // ===================================================================

    private sealed class MockTimestampFormat
    {
        public string Label { get; init; } = string.Empty;
        public string NetFormat { get; init; } = string.Empty;
        public MockWildness Tier { get; init; }
        public bool HasYear { get; init; } = true;
        public bool IsEpoch { get; init; }
        public bool EpochMillis { get; init; }
        public bool Bracketed { get; init; }
        public bool HasOffset { get; init; }

        /// <summary>Whether TimestampDetector's leading-timestamp regexes can find this shape on
        /// their own in a FlatText file. False entries are included in the Wild tier on purpose -
        /// dropping one of these onto the app should trigger the "ask, don't guess" manual
        /// pattern step in the LogType editor instead of a confident auto-detect, which is
        /// exactly what a break/fix pass should exercise. Also used to keep CSV/Tab output
        /// restricted to shapes the column-based detector can actually find.</summary>
        public bool AutoDetectable { get; init; } = true;
    }

    private static readonly MockTimestampFormat[] TimestampCatalog =
    {
        // ---- Conservative: the handful of shapes real-world logs use constantly ----
        new() { Label = "ISO 8601 (space-separated)", NetFormat = "yyyy-MM-dd HH:mm:ss", Tier = MockWildness.Conservative },
        new() { Label = "ISO 8601 ('T', UTC 'Z')", NetFormat = "yyyy-MM-ddTHH:mm:ssK", Tier = MockWildness.Conservative },
        new() { Label = "US date (MM/dd/yyyy)", NetFormat = "MM/dd/yyyy HH:mm:ss", Tier = MockWildness.Conservative },
        new() { Label = "Classic syslog (no year)", NetFormat = "MMM d HH:mm:ss", Tier = MockWildness.Conservative, HasYear = false },

        // ---- Standard: the rest of TimestampDetector.CandidateFormats' breadth ----
        new() { Label = "ISO 8601 with milliseconds", NetFormat = "yyyy-MM-dd HH:mm:ss.fff", Tier = MockWildness.Standard },
        new() { Label = "ISO 8601 ('T', 7-digit fraction, UTC)", NetFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK", Tier = MockWildness.Standard },
        new() { Label = "US date with UTC offset", NetFormat = "MM/dd/yyyy HH:mm:ss zzz", Tier = MockWildness.Standard, HasOffset = true },
        new() { Label = "European date (day first)", NetFormat = "dd/MM/yyyy HH:mm:ss", Tier = MockWildness.Standard },
        new() { Label = "Dot-separated date", NetFormat = "yyyy.MM.dd HH:mm:ss.fff", Tier = MockWildness.Standard },
        new() { Label = "SQL-style month abbreviation", NetFormat = "dd-MMM-yyyy HH:mm:ss.fff", Tier = MockWildness.Standard },
        new() { Label = "12-hour clock (AM/PM)", NetFormat = "M/d/yyyy h:mm:ss tt", Tier = MockWildness.Standard },
        new() { Label = "2-digit year", NetFormat = "MM-dd-yy HH:mm:ss", Tier = MockWildness.Standard },
        new() { Label = "Unix epoch (seconds)", NetFormat = "", Tier = MockWildness.Standard, IsEpoch = true, AutoDetectable = true },
        new() { Label = "Unix epoch (milliseconds)", NetFormat = "", Tier = MockWildness.Standard, IsEpoch = true, EpochMillis = true, AutoDetectable = true },

        // ---- Wild: genuinely unusual shapes; several are deliberately outside what the
        // leading-timestamp regexes recognize, so they surface the manual pattern step instead
        // of a confident auto-detect. ----
        new() { Label = "Bracketed timestamp", NetFormat = "yyyy-MM-dd HH:mm:ss.fff", Tier = MockWildness.Wild, Bracketed = true },
        new() { Label = "Compact, no separators (known gap)", NetFormat = "yyyyMMdd HHmmss", Tier = MockWildness.Wild, AutoDetectable = false },
        new() { Label = "Full weekday + month name", NetFormat = "dddd, MMMM d, yyyy h:mm:ss tt", Tier = MockWildness.Wild, AutoDetectable = false },
        new() { Label = "Time before date (reversed)", NetFormat = "HH:mm:ss dd-MM-yyyy", Tier = MockWildness.Wild, AutoDetectable = false },
    };

    // ===================================================================
    // Mock identity: server names (always "MOCK-" prefixed) and IPs (always RFC 5737
    // documentation ranges - see the class doc comment above).
    // ===================================================================

    private static readonly string[] SiteTags =
    {
        "EAST", "WEST", "NORTH", "SOUTH", "PLANT01", "PLANT02", "LINE-A", "LINE-B",
        "RACK04", "CELL07", "BLDG3", "FLOOR2"
    };

    private static readonly string[] RoleTags =
    {
        "SCADA", "HISTORIAN", "PLC-GW", "OPCUA", "KEPWARE", "PI-COLLECTOR", "EVTLOG",
        "MODBUS-BR", "ALARM-SVC", "DB01", "GATEWAY", "EDGE-NODE"
    };

    private static readonly (byte A, byte B, byte C)[] DocumentationRanges =
    {
        (192, 0, 2),    // TEST-NET-1, RFC 5737
        (198, 51, 100), // TEST-NET-2, RFC 5737
        (203, 0, 113)   // TEST-NET-3, RFC 5737
    };

    private static readonly string[] Severities = { "INFO", "WARN", "ERROR", "DEBUG" };

    private static readonly string[] Subsystems =
    {
        "Kepware.Gateway", "PI.Collector", "EventViewer.Security", "SCADA.PLC01",
        "Modbus.Bridge", "OPCUA.Client", "Historian.Sync", "Alarm.Manager", "Edge.Agent"
    };

    private static readonly string[] Verbs =
    {
        "Connection established", "Connection lost", "Tag value updated",
        "Threshold exceeded", "Heartbeat received", "Configuration reloaded",
        "Buffer flushed", "Retry attempt failed", "Session authenticated",
        "Watchdog reset", "Scan cycle completed", "Deadband suppressed an update"
    };

    private static readonly string[] Units = { "psi", "degC", "rpm", "pct", "V", "A", "Hz" };

    // ===================================================================
    // Public entry point
    // ===================================================================

    public static Task<MockGenerationResult> GenerateAsync(MockDataOptions options) =>
        Task.Run(() => Generate(options));

    private static MockGenerationResult Generate(MockDataOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputFolder))
            throw new InvalidOperationException("Choose an output folder first.");

        Directory.CreateDirectory(options.OutputFolder);

        var rng = new Random(options.Seed ?? Environment.TickCount);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pool = BuildFormatPool(options.OutputFormat, options.Wildness);

        var result = new MockGenerationResult { OutputFolder = options.OutputFolder };
        var runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        int fileCount = Math.Clamp(options.FileCount, 1, 50);
        int recordsPerFile = Math.Clamp(options.RecordsPerFile, 5, 200_000);

        for (int i = 0; i < fileCount; i++)
        {
            var serverName = NextServerName(rng, usedNames);
            var extension = options.OutputFormat switch
            {
                FileType.CSV => ".csv",
                FileType.TabDelimited => ".tsv",
                _ => ".log"
            };
            var fileName = $"{serverName}_{runStamp}{extension}";
            var path = Path.Combine(options.OutputFolder, fileName);

            var defectsInThisFile = WriteFile(path, serverName, recordsPerFile, options, pool, rng);

            result.Files.Add(new MockGeneratedFile { Path = path, ServerName = serverName, RecordCount = recordsPerFile });
            result.TotalRecords += recordsPerFile;
            result.InjectedDefectCount += defectsInThisFile;
        }

        return result;
    }

    private static List<MockTimestampFormat> BuildFormatPool(FileType outputFormat, MockWildness wildness)
    {
        bool delimited = outputFormat != FileType.FlatText;

        // Epoch and bracketed shapes are FlatText concepts (a raw epoch number or a
        // bracket-wrapped value doesn't make sense as "the" content of one CSV/Tab column), and
        // the three Wild shapes marked AutoDetectable=false are excluded from delimited output
        // for the same reason FlatText excludes them from the "Simple" pool below - the
        // column-based detector can't find them either, so they'd just be silent dead weight in
        // a CSV/Tab file rather than demonstrating anything.
        var pool = TimestampCatalog
            .Where(f => f.Tier <= wildness)
            .Where(f => !delimited || (!f.IsEpoch && !f.Bracketed && f.AutoDetectable))
            .ToList();

        // Should never happen given the catalog's shape, but never hand back an empty pool.
        if (pool.Count == 0) pool.Add(TimestampCatalog[0]);
        return pool;
    }

    // ===================================================================
    // Per-file generation
    // ===================================================================

    private static int WriteFile(
        string path, string serverName, int recordCount, MockDataOptions options,
        List<MockTimestampFormat> pool, Random rng)
    {
        bool breakFix = options.UsagePreset == MockUsagePreset.BreakFix;

        // Simple: one format for the whole file, drawn preferentially from shapes the app can
        // actually auto-detect, so a plain drag-and-drop shows a confident happy path. BreakFix:
        // redraw the format per record (when more than one is available), so the file mixes
        // shapes - exactly the ambiguous case the "show every candidate, ask, never guess"
        // wizard behavior exists for.
        var simplePool = pool.Where(f => f.AutoDetectable).ToList();
        if (simplePool.Count == 0) simplePool = pool;
        var primaryFormat = simplePool[rng.Next(simplePool.Count)];

        var startUtc = DateTime.UtcNow.AddDays(-rng.Next(1, 14)).AddHours(-rng.Next(0, 24));
        var avgIntervalMs = rng.Next(1000, 45_000);
        var current = startUtc;
        int defects = 0;

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (options.OutputFormat != FileType.FlatText)
        {
            var sep = options.OutputFormat == FileType.CSV ? "," : "\t";
            writer.WriteLine(string.Join(sep, "Timestamp", "Severity", "Subsystem", "Message", "Host", "SourceIP"));
        }

        for (int i = 0; i < recordCount; i++)
        {
            // BreakFix injects one blank/malformed/continuation-only record roughly every 15
            // lines - frequent enough to show up in a modestly-sized demo file without
            // drowning it in noise.
            if (breakFix && rng.Next(15) == 0)
            {
                defects += WriteDefectRecord(writer, options.OutputFormat, rng);
                continue;
            }

            var jitterMs = rng.Next(-(int)(avgIntervalMs * 0.4), (int)(avgIntervalMs * 0.4) + 1);
            current = current.AddMilliseconds(avgIntervalMs + jitterMs);

            var format = breakFix ? pool[rng.Next(pool.Count)] : primaryFormat;
            var tsText = RenderTimestamp(format, current, rng);

            var severity = Severities[rng.Next(Severities.Length)];
            var subsystem = Subsystems[rng.Next(Subsystems.Length)];
            var verb = Verbs[rng.Next(Verbs.Length)];
            var mockIp = NextMockIp(rng);
            bool includeNetworkDetail = rng.Next(4) == 0;
            var message = BuildMessage(rng, severity, subsystem, verb, serverName, mockIp, includeNetworkDetail);

            WriteRecord(writer, options.OutputFormat, tsText, severity, subsystem, message, serverName, mockIp);
        }

        return defects;
    }

    private static void WriteRecord(
        TextWriter writer, FileType outputFormat, string tsText, string severity, string subsystem,
        string message, string serverName, string mockIp)
    {
        switch (outputFormat)
        {
            case FileType.CSV:
                writer.WriteLine(string.Join(",",
                    CsvField(tsText), CsvField(severity), CsvField(subsystem), CsvField(message), CsvField(serverName), CsvField(mockIp)));
                break;
            case FileType.TabDelimited:
                writer.WriteLine(string.Join("\t",
                    tsText, severity, subsystem, message.Replace('\t', ' '), serverName, mockIp));
                break;
            default:
                writer.WriteLine($"{tsText} {message}");
                break;
        }
    }

    /// <summary>Always quotes every field - a legal, standards-compliant RFC4180 writer strategy
    /// that guarantees an embedded comma or quote in the message round-trips correctly through
    /// DelimitedLineParser.ReadCsvRecord (the same real-world shape the README calls out for the
    /// app's actual sample logs).</summary>
    private static string CsvField(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static int WriteDefectRecord(TextWriter writer, FileType outputFormat, Random rng)
    {
        const string badTimestamp = "9999-99-99 99:99:99";
        const string severity = "CRITICAL";
        const string subsystem = "Watchdog";

        switch (rng.Next(3))
        {
            case 0:
                // A blank line - FlatText/TabDelimited both treat this as harmless filler, not
                // the start of a new record.
                writer.WriteLine();
                return 0;

            case 1:
                // Timestamp-shaped text that fails to actually parse (invalid month/day/hour) -
                // still recognized as the start of a new block, but IngestionService keeps it
                // with a sentinel timestamp and a warning instead of silently dropping it. This
                // is the single most useful defect to demo: it's the app's own documented
                // "never drop a row" guarantee, made visible.
                var message = "Malformed timestamp - sensor watchdog produced an invalid frame";
                switch (outputFormat)
                {
                    case FileType.CSV:
                        writer.WriteLine(string.Join(",",
                            CsvField(badTimestamp), CsvField(severity), CsvField(subsystem), CsvField(message), CsvField("(unknown)"), CsvField("(unknown)")));
                        break;
                    case FileType.TabDelimited:
                        writer.WriteLine(string.Join("\t", badTimestamp, severity, subsystem, message, "(unknown)", "(unknown)"));
                        break;
                    default:
                        writer.WriteLine($"{badTimestamp} {severity} {subsystem}: {message}");
                        break;
                }
                return 1;

            default:
                // A continuation-only line with no leading timestamp at all, e.g. a pasted
                // stack trace - exercises multi-line block handling. A CSV record can't take
                // this shape at all (every record needs a timestamp column), so CSV falls back
                // to the same malformed-timestamp record as case 1 instead.
                if (outputFormat == FileType.CSV)
                {
                    writer.WriteLine(string.Join(",",
                        CsvField(badTimestamp), CsvField(severity), CsvField(subsystem),
                        CsvField("Malformed timestamp - sensor watchdog produced an invalid frame"),
                        CsvField("(unknown)"), CsvField("(unknown)")));
                    return 1;
                }
                writer.WriteLine("    at Kepware.Gateway.TagWriter.Flush() -- unhandled exception, see previous log entry");
                return 0;
        }
    }

    // ===================================================================
    // Rendering helpers
    // ===================================================================

    private static string RenderTimestamp(MockTimestampFormat format, DateTime baseUtc, Random rng)
    {
        // AddYears (not manual month/day/year reconstruction) so a generated value that happens
        // to land on Feb 29 can't throw when forced into a non-leap current year - AddYears
        // rolls Feb 29 -> Feb 28 in that case, per documented DateTime behavior, rather than
        // crashing the generator on an otherwise-ordinary run with a large RecordsPerFile.
        var effective = format.HasYear
            ? baseUtc
            : baseUtc.AddYears(DateTime.UtcNow.Year - baseUtc.Year);

        if (format.IsEpoch)
        {
            var dto = new DateTimeOffset(effective, TimeSpan.Zero);
            var digits = format.EpochMillis ? dto.ToUnixTimeMilliseconds() : dto.ToUnixTimeSeconds();
            return digits.ToString(CultureInfo.InvariantCulture);
        }

        string text;
        if (format.HasOffset)
        {
            var offsetMinutesChoices = new[] { -8 * 60, -5 * 60, -4 * 60, 0, 60, 3 * 60, 5 * 60 + 30, 9 * 60 + 30 };
            var offset = TimeSpan.FromMinutes(offsetMinutesChoices[rng.Next(offsetMinutesChoices.Length)]);
            var local = effective + offset;
            var dto = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            text = dto.ToString(format.NetFormat, CultureInfo.InvariantCulture);
        }
        else
        {
            text = effective.ToString(format.NetFormat, CultureInfo.InvariantCulture);
        }

        return format.Bracketed ? "[" + text + "]" : text;
    }

    private static string NextServerName(Random rng, HashSet<string> used)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var role = RoleTags[rng.Next(RoleTags.Length)];
            var site = SiteTags[rng.Next(SiteTags.Length)];
            var n = rng.Next(1, 100);
            var name = $"MOCK-{role}-{site}-{n:D2}";
            if (used.Add(name)) return name;
        }

        // Astronomically unlikely given the pool size above, but guarantees uniqueness even in
        // a pathological run (e.g. a very large FileCount) rather than silently colliding.
        var fallback = $"MOCK-SERVER-{used.Count + 1:D4}";
        used.Add(fallback);
        return fallback;
    }

    private static string NextMockIp(Random rng)
    {
        var range = DocumentationRanges[rng.Next(DocumentationRanges.Length)];
        var host = rng.Next(1, 255);
        return $"{range.A}.{range.B}.{range.C}.{host}";
    }

    private static string BuildMessage(
        Random rng, string severity, string subsystem, string verb, string serverName, string mockIp, bool includeNetworkDetail)
    {
        if (includeNetworkDetail)
            return $"{severity} {subsystem}: {verb} (host={serverName}, ip={mockIp})";

        var tag = $"tag{rng.Next(1, 64):D2}";
        var value = (rng.NextDouble() * 1000.0).ToString("F2", CultureInfo.InvariantCulture);
        var unit = Units[rng.Next(Units.Length)];
        return $"{severity} {subsystem}: {verb} tag={tag} value={value} unit={unit}";
    }
}
