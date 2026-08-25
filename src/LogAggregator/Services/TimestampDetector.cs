using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Result of the legacy single-line TimestampDetector.AutoDetect() overload. The wizard now
/// uses AutoDetectFromLines() (see MultiLineDetectionResult) against real file content instead
/// of a pasted sample line; this type is kept for the lower-level single-line helper methods.
/// </summary>
public class TimestampDetectionResult
{
    public bool Success { get; set; }
    public TimestampProfile? Profile { get; set; }
    public DateTime? PreviewParsedUtc { get; set; }
    public string? PreviewRawText { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Locates and parses timestamps across a wide range of real-world log shapes without the
/// user needing to hand-write a regex or format string. Built from direct inspection of five
/// very differently-shaped sample logs (Windows Event Viewer CSV/TXT exports with 12-hour and
/// 24-hour timestamps, a space-padded flat-text SCADA log with the timestamp split across two
/// leading columns, and two comma-delimited PI System logs with variable-precision fractional
/// seconds). The same detection logic is reused at ingestion time, so a profile built from one
/// sample line generalizes to the rest of the file.
/// </summary>
public static class TimestampDetector
{
    // Ordered most-specific-first so a more precise match wins when several formats could apply.
    public static readonly string[] CandidateFormats =
    {
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss.fffffff",
        "yyyy/MM/dd HH:mm:ss",
        "MM-dd-yyyy HH:mm:ss.fffffff",
        "MM-dd-yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss.fffffff",
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy h:mm:ss.fffffff tt",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy H:mm:ss",
        "dd-MMM-yyyy HH:mm:ss.fffffff",
        "dd-MMM-yyyy HH:mm:ss",
        "dd-MMM-yy HH:mm:ss.fffffff",
        "dd-MMM-yy HH:mm:ss",
        "yyyy-MM-dd",
        "MM-dd-yyyy",
        "MM/dd/yyyy",
    };

    private static readonly Regex LeadingTimestampRegex = new(
        @"^\s*(?<ts>\d{1,4}[-/][A-Za-z]{0,3}\d{0,2}[-/]?\d{0,4}\s+\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s*[AaPp][Mm])?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)",
        RegexOptions.Compiled);

    // Looser check: "does this string contain something date-shaped AND time-shaped anywhere".
    private static readonly Regex LooksLikeDateTime = new(
        @"\d{1,4}[-/][A-Za-z]{0,3}[-/]?\d{0,4}[-/]?\d{0,4}.{0,3}\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?",
        RegexOptions.Compiled);

    private static readonly Regex LooksLikeDateOnly = new(
        @"^\d{1,4}[-/][A-Za-z0-9]{1,4}[-/]\d{1,4}$", RegexOptions.Compiled);

    private static readonly Regex LooksLikeTimeOnly = new(
        @"^\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?\s*([AaPp][Mm])?$", RegexOptions.Compiled);

    // Matches the fractional-seconds portion right after "hh:mm:ss" so it can be normalized
    // to a fixed width regardless of how many digits the source log actually wrote.
    private static readonly Regex FractionalSecondsRegex = new(@"(?<=:\d{2})[.,](?<frac>\d+)", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, Regex> UserRegexCache = new();

    // ===================================================================
    // Public: auto-detect from a single pasted sample line
    // ===================================================================

    public static TimestampDetectionResult AutoDetect(string sampleLine, FileType fileType, char delimiter = ',')
    {
        if (string.IsNullOrWhiteSpace(sampleLine))
            return Fail("Paste a sample line first.");

        return fileType == FileType.FlatText
            ? AutoDetectLineStart(sampleLine)
            : AutoDetectDelimited(sampleLine, delimiter);
    }

    private static TimestampDetectionResult AutoDetectLineStart(string sampleLine)
    {
        var match = LeadingTimestampRegex.Match(sampleLine);
        if (!match.Success)
        {
            return Fail("No timestamp-shaped text found at the start of the line. " +
                         "If the timestamp isn't at the very start, pick CSV or Tab-delimited instead.");
        }

        var raw = match.Groups["ts"].Value;
        var profile = new TimestampProfile
        {
            Mode = TimestampLocationMode.LineStart,
            RegexPattern = LeadingTimestampRegex.ToString(),
        };

        if (TryParseTimestampText(raw, profile, out var utc))
        {
            profile.Description = "Leading timestamp (start of line), format auto-detected";
            return new TimestampDetectionResult
            {
                Success = true,
                Profile = profile,
                PreviewParsedUtc = utc,
                PreviewRawText = raw,
                Message = $"Matched leading timestamp \"{raw}\" -> {utc:yyyy-MM-dd HH:mm:ss.fffffff} UTC"
            };
        }

        return Fail($"Found what looks like a timestamp (\"{raw}\") but couldn't parse it. " +
                     "Try entering a format string manually.");
    }

    private static TimestampDetectionResult AutoDetectDelimited(string sampleLine, char delimiter)
    {
        var fields = DelimitedLineParser.SplitLine(sampleLine, delimiter);
        if (fields.Length < 1)
            return Fail("Couldn't split the sample line into columns - check the delimiter.");

        // Pass 1: a single column containing a full date+time.
        for (int i = 0; i < fields.Length; i++)
        {
            var v = fields[i].Trim().Trim('"');
            if (v.Length == 0) continue;
            if (!LooksLikeDateTime.IsMatch(v)) continue;

            var profile = new TimestampProfile { Mode = TimestampLocationMode.DelimitedColumn, ColumnIndex = i };
            if (TryParseTimestampText(v, profile, out var utc))
            {
                profile.Description = $"Column {i} (\"{v}\"), format auto-detected";
                return new TimestampDetectionResult
                {
                    Success = true,
                    Profile = profile,
                    PreviewParsedUtc = utc,
                    PreviewRawText = v,
                    Message = $"Column {i} looks like the timestamp: \"{v}\" -> {utc:yyyy-MM-dd HH:mm:ss.fffffff} UTC"
                };
            }
        }

        // Pass 2: an adjacent Date column + Time column pair (e.g. Kepware-style logs).
        for (int i = 0; i < fields.Length - 1; i++)
        {
            var dateVal = fields[i].Trim().Trim('"');
            var timeVal = fields[i + 1].Trim().Trim('"');
            if (!LooksLikeDateOnly.IsMatch(dateVal) || !LooksLikeTimeOnly.IsMatch(timeVal)) continue;

            var combined = $"{dateVal} {timeVal}";
            var profile = new TimestampProfile
            {
                Mode = TimestampLocationMode.DelimitedTwoColumn,
                ColumnIndex = i,
                SecondColumnIndex = i + 1
            };
            if (TryParseTimestampText(combined, profile, out var utc))
            {
                profile.Description = $"Columns {i} + {i + 1} combined (\"{combined}\"), format auto-detected";
                return new TimestampDetectionResult
                {
                    Success = true,
                    Profile = profile,
                    PreviewParsedUtc = utc,
                    PreviewRawText = combined,
                    Message = $"Columns {i} and {i + 1} together look like a date + time: \"{combined}\" -> {utc:yyyy-MM-dd HH:mm:ss.fffffff} UTC"
                };
            }
        }

        // Pass 3: fall back to "any column .NET itself can parse", even if it's date-only.
        for (int i = 0; i < fields.Length; i++)
        {
            var v = fields[i].Trim().Trim('"');
            if (v.Length == 0) continue;
            var profile = new TimestampProfile { Mode = TimestampLocationMode.DelimitedColumn, ColumnIndex = i };
            if (TryParseTimestampText(v, profile, out var utc))
            {
                profile.Description = $"Column {i} (\"{v}\"), best-effort match";
                return new TimestampDetectionResult
                {
                    Success = true,
                    Profile = profile,
                    PreviewParsedUtc = utc,
                    PreviewRawText = v,
                    Message = $"Best-effort match on column {i}: \"{v}\" -> {utc:yyyy-MM-dd HH:mm:ss} UTC. " +
                               "Double check this is really the timestamp column."
                };
            }
        }

        return Fail("Couldn't find a column that looks like a timestamp. Check the delimiter, " +
                     "or set the column index manually.");
    }

    private static TimestampDetectionResult Fail(string message) => new() { Success = false, Message = message };

    // ===================================================================
    // Public: auto-detect from real sample lines read from the actual file (no pasting
    // required). Votes across multiple lines instead of trusting a single one, and reports
    // ambiguity (multiple equally-plausible timestamp columns) instead of silently guessing.
    // ===================================================================

    public static MultiLineDetectionResult AutoDetectFromLines(List<string> lines, FileType fileType, char delimiter = ',')
    {
        if (lines is null || lines.Count == 0)
            return new MultiLineDetectionResult { Status = DetectionStatus.Failed, Message = "No sample lines available to analyze." };

        return fileType == FileType.FlatText
            ? AutoDetectLineStartMultiLine(lines)
            : AutoDetectDelimitedMultiLine(lines, fileType, delimiter);
    }

    private static MultiLineDetectionResult AutoDetectLineStartMultiLine(List<string> lines)
    {
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0)
            return new MultiLineDetectionResult { Status = DetectionStatus.Failed, Message = "No content found to analyze." };

        var hits = new List<(string raw, DateTime utc)>();
        foreach (var line in nonEmpty)
        {
            var match = LeadingTimestampRegex.Match(line);
            if (!match.Success) continue;

            var raw = match.Groups["ts"].Value;
            var probeProfile = new TimestampProfile { Mode = TimestampLocationMode.LineStart, RegexPattern = LeadingTimestampRegex.ToString() };
            if (TryParseTimestampText(raw, probeProfile, out var utc))
                hits.Add((raw, utc));
        }

        double rate = hits.Count / (double)nonEmpty.Count;
        if (hits.Count == 0 || rate < 0.3)
        {
            return new MultiLineDetectionResult
            {
                Status = DetectionStatus.Failed,
                Message = "Couldn't find a consistent timestamp at the start of the sample lines."
            };
        }

        var profile = new TimestampProfile
        {
            Mode = TimestampLocationMode.LineStart,
            RegexPattern = LeadingTimestampRegex.ToString(),
            Description = "Start of line"
        };

        return new MultiLineDetectionResult
        {
            Status = DetectionStatus.Confident,
            Candidates = new List<TimestampCandidate>
            {
                new()
                {
                    Profile = profile,
                    Description = "Start of line",
                    ExampleRawText = hits[0].raw,
                    ExampleUtc = hits[0].utc,
                    MatchRate = rate
                }
            },
            Message = $"Matched {hits.Count} of {nonEmpty.Count} sample lines ({rate:P0})."
        };
    }

    private static MultiLineDetectionResult AutoDetectDelimitedMultiLine(List<string> lines, FileType fileType, char delimiter)
    {
        var rows = lines.Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => fileType == FileType.TabDelimited ? DelimitedLineParser.SplitTab(l) : DelimitedLineParser.SplitLine(l, delimiter))
            .Where(f => f.Length > 0)
            .ToList();

        if (rows.Count == 0)
        {
            return new MultiLineDetectionResult
            {
                Status = DetectionStatus.Failed,
                Message = "Couldn't split any sample lines into columns."
            };
        }

        int maxCols = rows.Max(r => r.Length);
        int sampleCount = rows.Count;

        // Pass 1: single columns that consistently contain a full date+time.
        var singleHits = new int[maxCols];
        var singleExample = new (string raw, DateTime utc)?[maxCols];

        for (int col = 0; col < maxCols; col++)
        {
            foreach (var row in rows)
            {
                if (col >= row.Length) continue;
                var v = row[col].Trim().Trim('"');
                if (v.Length == 0 || !LooksLikeDateTime.IsMatch(v)) continue;

                var probeProfile = new TimestampProfile { Mode = TimestampLocationMode.DelimitedColumn, ColumnIndex = col };
                if (TryParseTimestampText(v, probeProfile, out var utc))
                {
                    singleHits[col]++;
                    singleExample[col] ??= (v, utc);
                }
            }
        }

        var candidates = new List<TimestampCandidate>();
        var claimedColumns = new HashSet<int>();

        for (int col = 0; col < maxCols; col++)
        {
            double rate = singleHits[col] / (double)sampleCount;
            if (rate >= 0.7 && singleExample[col] is { } example)
            {
                claimedColumns.Add(col);
                candidates.Add(new TimestampCandidate
                {
                    Profile = new TimestampProfile
                    {
                        Mode = TimestampLocationMode.DelimitedColumn,
                        ColumnIndex = col,
                        Description = $"Column {col}"
                    },
                    Description = $"Column {col} (single column, e.g. \"{example.raw}\")",
                    ExampleRawText = example.raw,
                    ExampleUtc = example.utc,
                    MatchRate = rate
                });
            }
        }

        // Pass 2: adjacent Date + Time column pairs (e.g. Kepware-style logs), skipping any
        // column already claimed by a strong single-column candidate.
        for (int col = 0; col < maxCols - 1; col++)
        {
            if (claimedColumns.Contains(col) || claimedColumns.Contains(col + 1)) continue;

            int pairHits = 0;
            (string raw, DateTime utc)? pairExample = null;

            foreach (var row in rows)
            {
                if (col + 1 >= row.Length) continue;
                var dateVal = row[col].Trim().Trim('"');
                var timeVal = row[col + 1].Trim().Trim('"');
                if (!LooksLikeDateOnly.IsMatch(dateVal) || !LooksLikeTimeOnly.IsMatch(timeVal)) continue;

                var combined = $"{dateVal} {timeVal}";
                var probeProfile = new TimestampProfile { Mode = TimestampLocationMode.DelimitedTwoColumn, ColumnIndex = col, SecondColumnIndex = col + 1 };
                if (TryParseTimestampText(combined, probeProfile, out var utc))
                {
                    pairHits++;
                    pairExample ??= (combined, utc);
                }
            }

            double rate = pairHits / (double)sampleCount;
            if (rate >= 0.7 && pairExample is { } example)
            {
                candidates.Add(new TimestampCandidate
                {
                    Profile = new TimestampProfile
                    {
                        Mode = TimestampLocationMode.DelimitedTwoColumn,
                        ColumnIndex = col,
                        SecondColumnIndex = col + 1,
                        Description = $"Columns {col} + {col + 1}"
                    },
                    Description = $"Column {col} (date) + Column {col + 1} (time), e.g. \"{example.raw}\"",
                    ExampleRawText = example.raw,
                    ExampleUtc = example.utc,
                    MatchRate = rate
                });
            }
        }

        candidates = candidates.OrderByDescending(c => c.MatchRate).ToList();

        if (candidates.Count == 0)
        {
            return new MultiLineDetectionResult
            {
                Status = DetectionStatus.Failed,
                Message = "Couldn't find a column that consistently looks like a timestamp across the sample lines."
            };
        }

        if (candidates.Count == 1)
        {
            return new MultiLineDetectionResult
            {
                Status = DetectionStatus.Confident,
                Candidates = candidates,
                Message = $"{candidates[0].Description} -> matched {candidates[0].MatchRate:P0} of sample lines."
            };
        }

        return new MultiLineDetectionResult
        {
            Status = DetectionStatus.Ambiguous,
            Candidates = candidates,
            Message = $"Found {candidates.Count} places that could be the timestamp - pick the correct one below."
        };
    }

    /// <summary>Tests a manually-entered pattern against the same real sample lines used for
    /// auto-detection (no pasted sample line needed), returning an aggregate pass rate and an
    /// example match so the wizard can show meaningful feedback.</summary>
    public static (bool Success, string Message) TestManualPattern(List<string> lines, FileType fileType, TimestampProfile profile, char delimiter)
    {
        int total = 0, hits = 0;
        string? exampleRaw = null;
        DateTime? exampleUtc = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            total++;

            bool ok;
            DateTime utc;
            string raw;

            if (fileType == FileType.FlatText)
            {
                ok = LineStartsWithTimestamp(line, profile, out utc, out raw);
            }
            else
            {
                var fields = fileType == FileType.TabDelimited ? DelimitedLineParser.SplitTab(line) : DelimitedLineParser.SplitLine(line, delimiter);
                ok = TryParseColumnTimestamp(fields, profile, out utc, out raw);
            }

            if (ok)
            {
                hits++;
                exampleRaw ??= raw;
                exampleUtc ??= utc;
            }
        }

        if (total == 0) return (false, "No sample lines available to test against.");
        if (hits == 0) return (false, "That pattern didn't match any of the sample lines from the file(s) you selected.");

        double rate = hits / (double)total;
        return (true, $"Matched {hits} of {total} sample lines ({rate:P0}). Example: \"{exampleRaw}\" -> {exampleUtc:yyyy-MM-dd HH:mm:ss} UTC.");
    }

    // ===================================================================
    // Public: used at ingestion time with an already-built profile
    // ===================================================================

    /// <summary>FlatText mode: does this physical line start a new block? If so, out-parameters
    /// carry the parsed timestamp and the raw matched text.</summary>
    public static bool LineStartsWithTimestamp(string line, TimestampProfile profile, out DateTime utc, out string originalText)
    {
        var regex = GetRegex(profile.RegexPattern);
        var match = regex.Match(line);
        if (!match.Success)
        {
            utc = DateTime.MinValue;
            originalText = string.Empty;
            return false;
        }

        originalText = match.Groups["ts"].Success ? match.Groups["ts"].Value : match.Groups[1].Value;
        return TryParseTimestampText(originalText, profile, out utc);
    }

    /// <summary>FlatText mode: evaluate a physical line, distinguishing "no timestamp here at
    /// all" (continuation line) from "matched the timestamp shape but couldn't be parsed"
    /// (new block, sentinel timestamp, warning). Used by IngestionService.</summary>
    public static TimestampLineResult EvaluateLine(string line, TimestampProfile profile)
    {
        var regex = GetRegex(profile.RegexPattern);
        var match = regex.Match(line);
        if (!match.Success) return TimestampLineResult.NoMatch();

        var raw = match.Groups["ts"].Success ? match.Groups["ts"].Value : match.Groups[1].Value;
        return TryParseTimestampText(raw, profile, out var utc)
            ? TimestampLineResult.Ok(utc, raw)
            : TimestampLineResult.Unparseable(raw);
    }

    /// <summary>CSV/TabDelimited mode: extract + parse the timestamp from an already-split record.</summary>
    public static bool TryParseColumnTimestamp(string[] fields, TimestampProfile profile, out DateTime utc, out string originalText)
    {
        if (profile.Mode == TimestampLocationMode.DelimitedTwoColumn)
        {
            if (profile.ColumnIndex < 0 || profile.SecondColumnIndex < 0 ||
                profile.ColumnIndex >= fields.Length || profile.SecondColumnIndex >= fields.Length)
            {
                utc = DateTime.MinValue;
                originalText = string.Empty;
                return false;
            }
            originalText = $"{fields[profile.ColumnIndex].Trim().Trim('"')} {fields[profile.SecondColumnIndex].Trim().Trim('"')}";
        }
        else
        {
            if (profile.ColumnIndex < 0 || profile.ColumnIndex >= fields.Length)
            {
                utc = DateTime.MinValue;
                originalText = string.Empty;
                return false;
            }
            originalText = fields[profile.ColumnIndex].Trim().Trim('"');
        }

        return TryParseTimestampText(originalText, profile, out utc);
    }

    /// <summary>
    /// Core parse routine shared by detection and ingestion. Tries, in order: the profile's own
    /// FormatString (if set), the built-in candidate library, then a culture-invariant free-form
    /// DateTime.TryParse as a last resort. Fractional seconds of any length are normalized to
    /// match whatever precision the target format expects.
    /// </summary>
    public static bool TryParseTimestampText(string text, TimestampProfile profile, out DateTime utcResult)
    {
        text = text.Trim();
        utcResult = DateTime.MinValue;
        if (text.Length == 0) return false;

        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault;

        if (!string.IsNullOrWhiteSpace(profile.FormatString) &&
            TryParseExactNormalized(text, profile.FormatString, styles, out utcResult))
        {
            return true;
        }

        foreach (var fmt in CandidateFormats)
        {
            if (TryParseExactNormalized(text, fmt, styles, out utcResult))
                return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            utcResult = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        return false;
    }

    private static bool TryParseExactNormalized(string text, string format, DateTimeStyles styles, out DateTime utcResult)
    {
        var fracLen = CountTrailingFractionalSpecifier(format);
        var candidate = fracLen > 0 ? NormalizeFractionalSeconds(text, fracLen) : text;

        if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, styles, out var parsed))
        {
            utcResult = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        utcResult = DateTime.MinValue;
        return false;
    }

    private static int CountTrailingFractionalSpecifier(string format)
    {
        var run = 0;
        foreach (var c in format)
        {
            if (c == 'f' || c == 'F') run++;
        }
        return run;
    }

    /// <summary>Pads or truncates the fractional-seconds digits in <paramref name="text"/> to
    /// exactly <paramref name="digits"/> characters so a format string like "fffffff" matches
    /// regardless of whether the source log wrote 1, 3, 5, or 7 digits.</summary>
    public static string NormalizeFractionalSeconds(string text, int digits)
    {
        return FractionalSecondsRegex.Replace(text, m =>
        {
            var frac = m.Groups["frac"].Value;
            frac = frac.Length >= digits ? frac.Substring(0, digits) : frac.PadRight(digits, '0');
            return "." + frac;
        });
    }

    private static Regex GetRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return LeadingTimestampRegex;
        return UserRegexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));
    }
}
