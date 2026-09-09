using System;
using System.Collections.Generic;

namespace LogAggregator.Models;

/// <summary>
/// How unusual the timestamp *shapes* the mock generator draws from should be - independent of
/// <see cref="MockUsagePreset"/>, which controls whether the file content is otherwise clean or
/// deliberately broken. Conservative sticks to the handful of shapes real-world logs use
/// constantly; Standard adds the rest of the shapes TimestampDetector.CandidateFormats already
/// knows; Wild adds a few genuinely unusual shapes, some of which TimestampDetector's
/// leading-timestamp regexes can't locate on their own on purpose - a good stress test of the
/// "ambiguous/failed detection asks instead of guessing" behavior and the manual pattern
/// override step in the LogType editor.
/// </summary>
public enum MockWildness
{
    Conservative,
    Standard,
    Wild
}

/// <summary>
/// SimpleExample = one consistent timestamp shape, clean data, confident auto-detection - meant
/// to demo the app's happy path in a single drag-and-drop. BreakFix = deliberately mixes
/// timestamp shapes within a file and injects a handful of edge cases (unparseable "timestamps",
/// blank lines, stack-trace-style continuation lines) so the ingestion pipeline's "never
/// silently drop a row, just warn" guarantee has something real to show.
/// </summary>
public enum MockUsagePreset
{
    SimpleExample,
    BreakFix
}

/// <summary>
/// Parameters for MockLogGeneratorService.GenerateAsync - everything the Settings window's
/// Developer tab needs to hand the generator.
/// </summary>
public class MockDataOptions
{
    public FileType OutputFormat { get; set; } = FileType.FlatText;

    public MockWildness Wildness { get; set; } = MockWildness.Standard;

    public MockUsagePreset UsagePreset { get; set; } = MockUsagePreset.SimpleExample;

    /// <summary>How many separate mock "servers" (each its own file, each its own invented
    /// name) to generate in this run.</summary>
    public int FileCount { get; set; } = 3;

    public int RecordsPerFile { get; set; } = 250;

    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>Null = a fresh random seed every run, so repeated clicks produce visibly
    /// different mock servers/data - the point of a "generate on the fly while showcasing"
    /// feature. Set to reproduce an exact previous run if that's ever useful.</summary>
    public int? Seed { get; set; }
}

/// <summary>One generated file - handed back so the UI can show what was written without
/// re-scanning the output folder.</summary>
public class MockGeneratedFile
{
    public string Path { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public int RecordCount { get; set; }
}

/// <summary>Result of one MockLogGeneratorService.GenerateAsync call.</summary>
public class MockGenerationResult
{
    public List<MockGeneratedFile> Files { get; set; } = new();
    public string OutputFolder { get; set; } = string.Empty;
    public int TotalRecords { get; set; }

    /// <summary>How many intentionally-broken records were mixed in - always 0 for
    /// MockUsagePreset.SimpleExample.</summary>
    public int InjectedDefectCount { get; set; }
}
