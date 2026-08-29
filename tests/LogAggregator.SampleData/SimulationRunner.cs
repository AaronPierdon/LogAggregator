using System;
using System.IO;
using System.Linq;
using LogAggregator.Models;
using LogAggregator.Services;

namespace LogAggregator.SampleData;

/// <summary>SIMULATION SUPPORT - knobs for one SimulationRunner.Run() call. Every field has a
/// sane default so `SimulationRunner.Run(new SimulationOptions())` just works.</summary>
public sealed class SimulationOptions
{
    /// <summary>Same seed -> same generated files -> same report. Change this to get a
    /// different (but still reproducible) batch.</summary>
    public int Seed { get; init; } = 42;

    /// <summary>Each group is 4 files (2 CSV + 2 TXT, as two same-content pairs) covering 4
    /// different timestamp shapes, so GroupCount=6 exercises 24 files.</summary>
    public int GroupCount { get; init; } = 6;

    public int RecordsPerFile { get; init; } = 30;

    /// <summary>Where generated files are written. Defaults to a fresh folder under the OS
    /// temp directory (never inside the repo) so nothing here needs to be gitignored unless
    /// you deliberately point this somewhere else.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>If false (default) and OutputDirectory was left null, the temp folder used for
    /// this run is deleted afterward. Set true (or pass an explicit OutputDirectory) to keep
    /// the generated files around for manual inspection.</summary>
    public bool KeepGeneratedFiles { get; init; }
}

/// <summary>
/// SIMULATION SUPPORT - generates a batch of synthetic log files and runs them through the
/// REAL detection pipeline (FileTypeDetector + TimestampDetector - the exact same calls
/// LogTypeEditorViewModel.RunDetection() and MainViewModel make), then compares every parsed
/// timestamp against the value the generator recorded as ground truth. Produces a
/// SimulationReport that's meaningful on its own (ToReportText()) and is also what the
/// SIMULATION_MODE app startup path and the xUnit test project both drive.
///
/// This class never reimplements any parsing/detection logic - it only calls into
/// LogAggregator.Services, so a "failure" reported here always reflects something the real app
/// would also get wrong.
/// </summary>
public static class SimulationRunner
{
    public static SimulationReport Run(SimulationOptions options)
    {
        var usingTempDir = options.OutputDirectory is null;
        var outputDir = options.OutputDirectory
            ?? Path.Combine(Path.GetTempPath(), "LogAggregator.Simulation", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));

        var generator = new SampleLogGenerator(options.Seed);
        var groups = generator.GenerateGroups(options.GroupCount, options.RecordsPerFile);

        var report = new SimulationReport { RunAtUtc = DateTime.UtcNow, Seed = options.Seed };

        try
        {
            foreach (var group in groups)
            {
                generator.WriteToDisk(group, outputDir);
                foreach (var file in group.AllFiles())
                {
                    report.Files.Add(EvaluateFile(file));
                }
            }
        }
        finally
        {
            if (usingTempDir && !options.KeepGeneratedFiles)
            {
                try { Directory.Delete(outputDir, recursive: true); }
                catch { /* best-effort cleanup only - a leftover temp folder isn't worth failing the run over */ }
            }
        }

        return report;
    }

    /// <summary>Runs one already-written GeneratedFile through the real detection pipeline
    /// and compares every data line against its known-correct value. Public so targeted
    /// per-format tests (see LogAggregator.Tests/TimestampFormatCatalogTests.cs) can reuse the
    /// exact same evaluation logic Run() uses for a full batch, instead of a second
    /// implementation that could quietly drift out of sync.</summary>
    public static FileResult EvaluateFile(GeneratedFile file)
    {
        // Mirrors LogTypeEditorViewModel.RunDetection() exactly: same real methods, same
        // 30-line sample size - this is testing precisely what the wizard's auto-detect step
        // would do if you dropped this file onto a source card.
        var sampleLines = FileTypeDetector.ReadSampleLines(file.FilePath, 30);
        var detectedType = FileTypeDetector.DetectFromLines(sampleLines);
        var delimiter = detectedType == FileType.TabDelimited ? '\t' : ',';
        var detection = TimestampDetector.AutoDetectFromLines(sampleLines, detectedType, delimiter);

        var result = new FileResult
        {
            FilePath = file.FilePath,
            FormatName = file.Format.Name,
            FileType = file.FileType,
            DetectionStatus = detection.Status,
            DetectionMessage = detection.Message,
            CandidateCount = detection.Candidates.Count
        };

        if (detectedType != file.FileType)
        {
            result.Failures.Add(new LineFailure
            {
                Note = $"File type misdetected as {detectedType} (expected {file.FileType})."
            });
        }

        if (detection.Status == DetectionStatus.Failed || detection.Candidates.Count == 0)
        {
            // Detection failed outright - there's no candidate profile to verify line-by-line
            // against, so the file-level status above is the whole story.
            return result;
        }

        // Same choice a user would most likely make: the highest-match-rate candidate. For a
        // Confident result there's only one candidate anyway; for Ambiguous this is "the top
        // suggestion" - if it's still correct, that's tracked as FilesAmbiguousButCorrect
        // rather than counted as a plain pass, so the report distinguishes "worked" from
        // "worked, but the user would have had to pick from a list."
        var chosen = detection.Candidates.OrderByDescending(c => c.MatchRate).First().Profile;

        for (int i = 0; i < file.Lines.Count; i++)
        {
            var expected = file.ExpectedUtcPerLine[i];
            if (expected is null) continue; // header row or other non-data line

            result.TotalDataLines++;

            bool ok;
            DateTime actual;
            if (file.FileType == FileType.FlatText)
            {
                ok = TimestampDetector.LineStartsWithTimestamp(file.Lines[i], chosen, out actual, out _);
            }
            else
            {
                var fields = file.FileType == FileType.TabDelimited
                    ? DelimitedLineParser.SplitTab(file.Lines[i])
                    : DelimitedLineParser.SplitLine(file.Lines[i], delimiter);
                ok = TimestampDetector.TryParseColumnTimestamp(fields, chosen, out actual, out _);
            }

            var withinTolerance = ok && Math.Abs((actual - expected.Value).TotalMilliseconds) < 5.0;
            if (withinTolerance)
            {
                result.PassedLines++;
            }
            else if (result.Failures.Count < 5) // a handful of concrete examples is enough to debug from
            {
                result.Failures.Add(new LineFailure
                {
                    LineNumber = i + 1,
                    LineText = file.Lines[i],
                    ExpectedUtc = expected.Value,
                    ActualUtc = ok ? actual : null
                });
            }
        }

        return result;
    }
}
