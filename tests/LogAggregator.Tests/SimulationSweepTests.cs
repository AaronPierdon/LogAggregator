using System;
using System.IO;
using LogAggregator.SampleData;
using Xunit;
using Xunit.Abstractions;

namespace LogAggregator.Tests;

/// <summary>
/// The "run a big batch and write a report" half of the request, complementing
/// TimestampFormatCatalogTests' one-case-per-format theories. xUnit's own output tells you
/// PASS/FAIL for this one test method; the report this writes tells you exactly which
/// generated files/lines failed and why, which is the level of detail needed to actually go
/// fix something.
/// </summary>
public class SimulationSweepTests
{
    private readonly ITestOutputHelper _output;

    public SimulationSweepTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FullSweep_AllGeneratedFilesRoundTrip()
    {
        // Seed is fixed (not random) so a failure here is reproducible: rerun this exact test
        // and you get the exact same generated files and the exact same failure again.
        var options = new SimulationOptions { Seed = 42, GroupCount = 8, RecordsPerFile = 30 };
        var report = SimulationRunner.Run(options);

        var reportPath = WriteReportNextToTestOutput(report);
        _output.WriteLine($"Simulation report written to: {reportPath}");
        _output.WriteLine(report.ToReportText());

        Assert.True(report.FilesFailed == 0,
            $"{report.FilesFailed}/{report.TotalFiles} generated file(s) failed " +
            $"({report.TotalLinesPassed}/{report.TotalDataLines} lines matched overall). " +
            $"Full report: {reportPath}");
    }

    /// <summary>Writes the report into the test assembly's own output folder
    /// (bin/.../SimulationReports/) so it's easy to find and attach/paste afterward, without
    /// needing to know any particular machine's TEMP path.</summary>
    private static string WriteReportNextToTestOutput(SimulationReport report)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "SimulationReports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"simulation-report-{report.RunAtUtc:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, report.ToReportText());
        return path;
    }
}
