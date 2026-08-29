using System;
using System.IO;
using LogAggregator.SampleData;
using Xunit;

namespace LogAggregator.Tests;

/// <summary>
/// A small, hand-picked set of "interesting" timestamp values applied to a handful of
/// representative formats - deliberately NOT crossed against the whole catalog (that would be
/// a large combinatorial sweep for very little extra signal). Each scenario targets one
/// specific class of edge case:
///   - year rollover (both the last possible instant of a year and the first)
///   - Feb 29 on a real leap year
///   - the real legal UTC offset extremes, +14:00 (Kiribati) and -12:00 (Baker Island) - the
///     normal random offset pool might land on these by chance, or might not; this guarantees
///     both get exercised every run
///   - the two-digit-year format's actual usable window edges (SampleLogGenerator constrains
///     two-digit-year generation to +/-3 years of "now" specifically to dodge .NET's/NodaTime's
///     century-inference pivot - this pins the exact edges of that window instead of trusting
///     the random middle of it)
/// </summary>
public class BoundaryTimestampTests : IDisposable
{
    private readonly string _outputDir;

    public BoundaryTimestampTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "LogAggregator.Tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string, string, DateTime, int?> Scenarios()
    {
        var data = new TheoryData<string, string, DateTime, int?>();

        // The very last representable instant of a year at 7-digit-fraction precision, and the
        // very first instant of the next - a truncation/rounding bug near a boundary would most
        // likely show up as an off-by-one into the wrong second, minute, or day here.
        data.Add("year-end", "space-sep-frac", new DateTime(2025, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999), null);
        data.Add("year-start", "space-sep-frac", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

        // A real leap day, on a format whose parser fills in the year from the text (not a
        // "no year in the text" shape, which would need special handling of its own).
        data.Add("leap-day", "us-slash", new DateTime(2028, 2, 29, 12, 0, 0, DateTimeKind.Utc), null);

        // The real legal UTC offset extremes.
        data.Add("offset-plus14", "space-sep-offset", new DateTime(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc), 14 * 60);
        data.Add("offset-minus12", "space-sep-offset", new DateTime(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc), -12 * 60);
        data.Add("offset-plus14-frac", "space-sep-offset-frac", new DateTime(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567), 14 * 60);

        // ISO-8601 "Z" at exact midnight - the exact shape the RoundtripKind fix targeted.
        data.Add("iso-midnight-z", "iso8601-z", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

        // The two-digit-year format's actual usable window edges (see class doc comment).
        data.Add("two-digit-year-min", "two-digit-year", new DateTime(DateTime.UtcNow.Year - 3, 3, 15, 9, 30, 0, DateTimeKind.Utc), null);
        data.Add("two-digit-year-max", "two-digit-year", new DateTime(DateTime.UtcNow.Year + 3, 3, 15, 9, 30, 0, DateTimeKind.Utc), null);

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void BoundaryValue_RoundTripsThroughRealDetector_Txt(string scenarioName, string formatName, DateTime utc, int? forcedOffsetMinutes)
    {
        var format = FindFormat(formatName);
        var forcedOffset = forcedOffsetMinutes is int m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;

        var generator = new SampleLogGenerator(StableSeed(scenarioName));
        var file = generator.GenerateBoundaryTxtFile(format, utc, forcedOffset);
        SampleLogGenerator.WriteFile(file, _outputDir, $"boundary_txt_{scenarioName}");

        var result = SimulationRunner.EvaluateFile(file);

        Assert.True(result.Success,
            $"{scenarioName} (TXT, {formatName}): detection={result.DetectionStatus} '{result.DetectionMessage}', " +
            $"{result.PassedLines}/{result.TotalDataLines} lines matched. {FailureDetail(result)}");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void BoundaryValue_RoundTripsThroughRealDetector_Csv(string scenarioName, string formatName, DateTime utc, int? forcedOffsetMinutes)
    {
        var format = FindFormat(formatName);
        if (!format.SupportsDelimited)
        {
            return; // see TimestampFormatCatalogTests for the same, documented skip
        }

        var forcedOffset = forcedOffsetMinutes is int m ? TimeSpan.FromMinutes(m) : (TimeSpan?)null;

        var generator = new SampleLogGenerator(StableSeed(scenarioName));
        var file = generator.GenerateBoundaryCsvFile(format, utc, forcedOffset);
        SampleLogGenerator.WriteFile(file, _outputDir, $"boundary_csv_{scenarioName}");

        var result = SimulationRunner.EvaluateFile(file);

        Assert.True(result.Success,
            $"{scenarioName} (CSV, {formatName}): detection={result.DetectionStatus} '{result.DetectionMessage}', " +
            $"{result.PassedLines}/{result.TotalDataLines} lines matched. {FailureDetail(result)}");
    }

    private static SampleTimestampFormat FindFormat(string name)
    {
        foreach (var format in TimestampFormatCatalog.All)
            if (format.Name == name) return format;
        throw new InvalidOperationException($"No catalog format named '{name}'.");
    }

    // Same deliberately-not-string.GetHashCode() approach as TimestampFormatCatalogTests - see
    // that class for why.
    private static int StableSeed(string name)
    {
        unchecked
        {
            int hash = 17;
            foreach (char c in name) hash = hash * 31 + c;
            return hash;
        }
    }

    private static string FailureDetail(FileResult result)
    {
        if (result.Failures.Count == 0) return string.Empty;
        var first = result.Failures[0];
        return first.Note is not null
            ? $"First issue: {first.Note}"
            : $"First mismatch at line {first.LineNumber}: expected {first.ExpectedUtc:O}, got {(first.ActualUtc?.ToString("O") ?? "(no parse)")}, text=\"{first.LineText}\"";
    }
}
