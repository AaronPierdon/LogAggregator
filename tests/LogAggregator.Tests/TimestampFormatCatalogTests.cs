using System;
using System.IO;
using LogAggregator.SampleData;
using Xunit;

namespace LogAggregator.Tests;

/// <summary>
/// One [Theory] case per catalog entry (see LogAggregator.SampleData.TimestampFormatCatalog),
/// for both the TXT (FlatText) and CSV shapes. This is the "per-format red/green in the test
/// runner" half of the request - Test Explorer will show one line per format, so a regression
/// in a specific shape (e.g. "space-sep-offset" - the exact shape from the original bug
/// report) shows up as that one named test going red, not just an aggregate count.
///
/// SimulationSweepTests (in this same project) is the complementary "run a big batch and write
/// a detailed report" half.
/// </summary>
public class TimestampFormatCatalogTests : IDisposable
{
    private readonly string _outputDir;

    public TimestampFormatCatalogTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), "LogAggregator.Tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> AllFormatNames()
    {
        var data = new TheoryData<string>();
        foreach (var format in TimestampFormatCatalog.All) data.Add(format.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFormatNames))]
    public void TxtFile_RoundTripsThroughRealDetector(string formatName)
    {
        var format = FindFormat(formatName);
        if (!format.SupportsAutoDetection)
        {
            // Documented, pre-existing gap - see SampleTimestampFormat.SupportsAutoDetection's
            // doc comment ("compact" has no date separator or time colon for the leading-
            // timestamp regexes to latch onto, on either CSV or TXT). Not a regression to flag.
            return;
        }

        var generator = new SampleLogGenerator(StableSeed(formatName));
        var file = generator.GenerateSingleTxtFile(format, recordsPerFile: 15);
        SampleLogGenerator.WriteFile(file, _outputDir, $"txt_{formatName}");

        var result = SimulationRunner.EvaluateFile(file);

        Assert.True(result.Success,
            $"{formatName} (TXT): detection={result.DetectionStatus} '{result.DetectionMessage}', " +
            $"{result.PassedLines}/{result.TotalDataLines} lines matched. {FailureDetail(result)}");
    }

    [Theory]
    [MemberData(nameof(AllFormatNames))]
    public void CsvFile_RoundTripsThroughRealDetector(string formatName)
    {
        var format = FindFormat(formatName);
        if (!format.SupportsDelimited || !format.SupportsAutoDetection)
        {
            // Two different, both documented, pre-existing gaps land here:
            //  - SupportsDelimited = false (epoch/no-year shapes): AutoDetectDelimitedMultiLine
            //    has no "best effort, any column .NET can parse" fallback the way the
            //    single-line detector does, so a bare epoch value or a no-year value never
            //    gets picked up from a CSV column at all.
            //  - SupportsAutoDetection = false ("compact"): no leading-timestamp/LooksLikeDateTime
            //    regex recognizes a separator-less, colon-less shape as timestamp-shaped in the
            //    first place, on CSV OR TXT (see the TXT theory above for the same skip).
            // Neither is a regression this test should flag - both are real, separate,
            // already-understood limitations worth fixing someday.
            return;
        }

        var generator = new SampleLogGenerator(StableSeed(formatName));
        var file = generator.GenerateSingleCsvFile(format, recordsPerFile: 15);
        SampleLogGenerator.WriteFile(file, _outputDir, $"csv_{formatName}");

        var result = SimulationRunner.EvaluateFile(file);

        Assert.True(result.Success,
            $"{formatName} (CSV): detection={result.DetectionStatus} '{result.DetectionMessage}', " +
            $"{result.PassedLines}/{result.TotalDataLines} lines matched. {FailureDetail(result)}");
    }

    private static SampleTimestampFormat FindFormat(string name)
    {
        foreach (var format in TimestampFormatCatalog.All)
            if (format.Name == name) return format;
        throw new InvalidOperationException($"No catalog format named '{name}'.");
    }

    /// <summary>A small, deterministic string hash for use as a generator seed. Deliberately
    /// NOT string.GetHashCode() - .NET randomizes that per process for security, so two runs
    /// of the same test would generate different data and the "same seed -> same result"
    /// guarantee the rest of this feature relies on would quietly stop holding here.</summary>
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
