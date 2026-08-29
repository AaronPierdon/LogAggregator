using System;
using System.Collections.Generic;
using System.Linq;
using LogAggregator.Models;
using LogAggregator.SampleData;
using LogAggregator.Services;
using Xunit;

namespace LogAggregator.Tests;

/// <summary>
/// Tests TimestampDetector.AutoDetectFromLines's confidence/voting behavior at its actual
/// boundaries, rather than just "does a well-formed file detect correctly" (that's what
/// TimestampFormatCatalogTests already covers). These are hand-built inputs, not drawn from
/// the format catalog/pool-cycling machinery in SampleLogGenerator - each one is testing a
/// specific structural situation:
///   - two columns that are BOTH genuinely valid, complete timestamps (real ambiguity)
///   - the exact 0.7 match-rate threshold used by both the CSV and FlatText detection paths
///   - files too small to "vote" across in the usual sense (1-2 lines)
///   - a majority pattern with a minority of a different (but still valid) pattern mixed in
/// This directly informs what the app's "ambiguous, please pick" wizard step and the
/// per-binding error chip (see IngestionWarning/SourceLogType.HasError) actually need to
/// handle - both exist specifically because auto-detection is not always unambiguous.
/// </summary>
public class AmbiguityDetectionTests
{
    private static readonly SampleTimestampFormat SpaceSep = Find("space-sep");
    private static readonly SampleTimestampFormat UsSlash = Find("us-slash");

    private static SampleTimestampFormat Find(string name) =>
        TimestampFormatCatalog.All.First(f => f.Name == name);

    [Fact]
    public void TwoGenuinelyValidTimestampColumns_IsReportedAmbiguous_NotSilentlyGuessed()
    {
        // A CSV with two independent, fully-valid timestamp columns (e.g. "CreatedUtc" and
        // "ModifiedUtc") - a real-world shape, not a contrived one. The app must never silently
        // pick one; it has to come back Ambiguous with both as candidates so the wizard can ask.
        var rng = new Random(12345);
        var factory = new RandomDataFactory(seed: 12345);
        var created = factory.CreateRecords(20, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(5));
        var modified = factory.CreateRecords(20, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(7));

        var lines = new List<string> { "CreatedUtc,ModifiedUtc,Message" };
        for (int i = 0; i < created.Count; i++)
        {
            var createdText = TimestampRenderer.Render(SpaceSep, created[i].TimestampUtc, rng).Text;
            var modifiedText = TimestampRenderer.Render(SpaceSep, modified[i].TimestampUtc, rng).Text;
            lines.Add($"{createdText},{modifiedText},{created[i].Message}");
        }

        var fileType = FileTypeDetector.DetectFromLines(lines);
        Assert.Equal(FileType.CSV, fileType);

        var result = TimestampDetector.AutoDetectFromLines(lines, fileType, ',');

        Assert.Equal(DetectionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData(70, 100, DetectionStatus.Confident)]  // exactly the 0.7 threshold - must pass (>=, not >)
    [InlineData(69, 100, DetectionStatus.Failed)]     // one row short of it - must NOT become a candidate at all
    public void CsvColumn_MatchRateThreshold_IsAppliedCorrectly(int validRows, int totalRows, DetectionStatus expectedStatus)
    {
        var rng = new Random(999);
        var factory = new RandomDataFactory(seed: 999);
        var records = factory.CreateRecords(totalRows, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(10));

        // No header row here deliberately: AutoDetectDelimitedMultiLine's match-rate
        // denominator (sampleCount) is every non-blank line it's handed, header included - a
        // header line doesn't look like a date+time so it never counts as a hit, but it WOULD
        // still count in the denominator and quietly shift the threshold (a first version of
        // this test included one and got 70/101 = 0.693, not 70/100 = 0.7, and failed at
        // exactly the boundary it was meant to prove). This call doesn't need a header anyway -
        // FileType.CSV/delimiter are passed explicitly, not inferred from one.
        var lines = new List<string>();
        for (int i = 0; i < totalRows; i++)
        {
            var value = i < validRows
                ? TimestampRenderer.Render(SpaceSep, records[i].TimestampUtc, rng).Text
                : "N/A"; // deliberately not timestamp-shaped at all - must contribute zero hits
            lines.Add($"{value},{records[i].Message}");
        }

        var result = TimestampDetector.AutoDetectFromLines(lines, FileType.CSV, ',');

        Assert.Equal(expectedStatus, result.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TinyFile_DetectsWithoutCrashing(int lineCount)
    {
        var rng = new Random(42);
        var factory = new RandomDataFactory(seed: 42);
        var records = factory.CreateRecords(lineCount, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(1));

        var lines = records
            .Select(r => $"{TimestampRenderer.Render(SpaceSep, r.TimestampUtc, rng).Text} {r.Message}")
            .ToList();

        // The point is "doesn't throw and doesn't confidently claim a pattern with essentially
        // no evidence" - not a specific status. A single matching line is still 100% of a
        // 1-line sample, so Confident is an acceptable outcome here; what would NOT be
        // acceptable is an exception.
        var result = TimestampDetector.AutoDetectFromLines(lines, FileType.FlatText, ',');
        Assert.NotEqual(DetectionStatus.Pending, result.Status);
    }

    [Fact]
    public void EmptyOrWhitespaceOnlyInput_FailsGracefully_DoesNotThrow()
    {
        Assert.Equal(DetectionStatus.Failed, TimestampDetector.AutoDetectFromLines(new List<string>(), FileType.FlatText, ',').Status);
        Assert.Equal(DetectionStatus.Failed, TimestampDetector.AutoDetectFromLines(new List<string> { "", "   ", "\t" }, FileType.FlatText, ',').Status);
        Assert.Equal(DetectionStatus.Failed, TimestampDetector.AutoDetectFromLines(new List<string>(), FileType.CSV, ',').Status);
    }

    [Fact]
    public void MajorityPatternWithMinorityDifferentPattern_StillDetects()
    {
        // 12 lines in one shape, 3 in a different (but still valid) shape mixed in - simulates
        // a log whose format changed mid-file (e.g. after an app update). Both "space-sep" and
        // "us-slash" match the SAME leading-timestamp regex family (it's separator/order
        // agnostic), and TryParseTimestampText tries every candidate format per-line rather
        // than committing the whole file to one - so the prediction here is that this still
        // detects with a high (not necessarily 100%) match rate rather than failing outright.
        // That's a real resilience property worth pinning down with a test either way.
        var rng = new Random(2026);
        var factory = new RandomDataFactory(seed: 2026);
        var majority = factory.CreateRecords(12, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(3));
        var minority = factory.CreateRecords(3, new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(3));

        var lines = new List<string>();
        lines.AddRange(majority.Select(r => $"{TimestampRenderer.Render(SpaceSep, r.TimestampUtc, rng).Text} {r.Message}"));
        lines.AddRange(minority.Select(r => $"{TimestampRenderer.Render(UsSlash, r.TimestampUtc, rng).Text} {r.Message}"));

        var result = TimestampDetector.AutoDetectFromLines(lines, FileType.FlatText, ',');

        Assert.NotEqual(DetectionStatus.Failed, result.Status);
        Assert.NotEmpty(result.Candidates);
        Assert.True(result.Candidates[0].MatchRate >= 0.7,
            $"Expected a reasonably high match rate across the mixed-shape file, got {result.Candidates[0].MatchRate:P0} ({result.Message}).");
    }
}
