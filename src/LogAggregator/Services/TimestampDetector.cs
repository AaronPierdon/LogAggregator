using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using LogAggregator.Models;
using NodaTime;
using NodaTime.Text;

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
/// Locates and parses timestamps across a wide range of real-world log shapes without the user
/// needing to hand-write a regex or format string. Originally built from direct inspection of
/// five differently-shaped sample logs (Windows Event Viewer CSV/TXT exports with 12-hour and
/// 24-hour timestamps, a space-padded flat-text SCADA log with the timestamp split across two
/// leading columns, and two comma-delimited PI System logs with variable-precision fractional
/// seconds); hardened further to cover more permutations - syslog-style month-first timestamps,
/// bracketed timestamps, compact/no-separator forms, 2-digit years, dot-separated dates, and
/// Unix epoch seconds/milliseconds - since real deployments show up with all of these. The same
/// detection logic is reused at ingestion time, so a profile built from sample lines generalizes
/// to the rest of the file.
///
/// The "where in the line/record is the timestamp" logic below is all hand-rolled (regexes
/// tuned against real sample logs) - that part is inherently specific to this app and no
/// library replaces it well. The "turn the located text into a DateTime" half now goes through
/// NodaTime first (see TryParseWithNodaTime) - it's explicit about anything a format string
/// doesn't specify (a missing year, a 2-digit year's century) via a template value, instead of
/// DateTime.TryParseExact's ambient OS-locale-dependent defaults. DateTime.TryParseExact is
/// kept as an automatic fallback for every format string in TryParseExactNormalized, so the
/// NodaTime path can only add parsing coverage, never remove it.
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
        // Space-separated date/time followed by a signed UTC offset - e.g. "2026-08-08
        // 14:23:05 -0400" (a very common export/log shape, distinct from the "T"-separated
        // ISO-8601 "K" formats above). "zzz" always expects a colon in the offset
        // ("-04:00") - NormalizeOffset (see TryParseExactNormalized) inserts one before
        // parsing if the source log wrote it without one ("-0400"), the same normalize-
        // before-parse trick NormalizeFractionalSeconds already uses.
        "yyyy-MM-dd HH:mm:ss.fffffff zzz",
        "yyyy-MM-dd HH:mm:ss zzz",
        "MM/dd/yyyy HH:mm:ss.fffffff zzz",
        "MM/dd/yyyy HH:mm:ss zzz",
        "yyyy/MM/dd HH:mm:ss.fffffff",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy.MM.dd HH:mm:ss.fffffff",
        "yyyy.MM.dd HH:mm:ss",
        "yyyyMMdd HHmmss.fffffff",
        "yyyyMMdd HHmmss",
        "yyyyMMddHHmmss",
        "MM-dd-yyyy HH:mm:ss.fffffff",
        "MM-dd-yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss.fffffff",
        "MM/dd/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss.fffffff",
        "dd/MM/yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm:ss.fffffff",
        "dd.MM.yyyy HH:mm:ss",
        "MM-dd-yy HH:mm:ss",
        "MM/dd/yy HH:mm:ss",
        "yy-MM-dd HH:mm:ss",
        "yy/MM/dd HH:mm:ss",
        "M/d/yyyy h:mm:ss.fffffff tt",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy H:mm:ss",
        "M/d/yy H:mm:ss",
        "dd-MMM-yyyy HH:mm:ss.fffffff",
        "dd-MMM-yyyy HH:mm:ss",
        "dd-MMM-yy HH:mm:ss.fffffff",
        "dd-MMM-yy HH:mm:ss",
        // Syslog-style: month name leads, no year (year filled from the current date - see
        // TryParseExactNormalized, which drops NoCurrentDateDefault for these formats).
        "MMM d HH:mm:ss.fffffff",
        "MMM d HH:mm:ss",
        "MMM dd HH:mm:ss",
        "MMM d yyyy HH:mm:ss",
        "MMM dd yyyy HH:mm:ss",
        "MMMM d, yyyy HH:mm:ss",
        "MMMM d yyyy h:mm:ss tt",
        "yyyy-MM-dd",
        "MM-dd-yyyy",
        "MM/dd/yyyy",
    };

    /// <summary>Numeric-date-first leading timestamp: "2024-01-02 15:04:05", "01/02/2024
    /// 3:04:05 PM", "2024-01-02T15:04:05.123Z", etc. Tried first since it's the shape the app's
    /// original sample logs use.</summary>
    private static readonly Regex LeadingTimestampRegexNumeric = new(
        @"^\s*(?<ts>\d{1,4}[-/.][A-Za-z]{0,3}\d{0,2}[-/.]?\d{0,4}[T\s]+\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s*[AaPp][Mm])?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)",
        RegexOptions.Compiled);

    /// <summary>Syslog-style month-name-first leading timestamp: "Jan  2 15:04:05", "Jan 02
    /// 2024 15:04:05.123456". Tried when the numeric-first shape doesn't match.</summary>
    private static readonly Regex LeadingTimestampRegexMonthFirst = new(
        @"^\s*(?<ts>[A-Za-z]{3,9}\.?\s+\d{1,2},?(?:\s+\d{2,4})?\s+\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s*[AaPp][Mm])?)",
        RegexOptions.Compiled);

    /// <summary>A leading timestamp wrapped in brackets or parentheses: "[2024-01-02
    /// 15:04:05]", "(01/02/2024 15:04:05)" - common in wrapped/bracketed log formats. The
    /// brackets themselves are outside the "ts" capture.</summary>
    private static readonly Regex LeadingTimestampRegexBracketed = new(
        @"^\s*[\[\(](?<ts>\d{1,4}[-/.][A-Za-z]{0,3}\d{0,2}[-/.]?\d{0,4}[T\s]+\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?(?:\s*[AaPp][Mm])?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)[\]\)]",
        RegexOptions.Compiled);

    /// <summary>A leading all-digit Unix epoch (seconds or milliseconds since 1970), e.g. a raw
    /// "1700000000" or "1700000000123" at the start of the line, followed by whitespace.</summary>
    private static readonly Regex LeadingTimestampRegexEpoch = new(
        @"^\s*(?<ts>\d{10}(?:\d{3})?)\s+",
        RegexOptions.Compiled);

    /// <summary>Tried in order - most specific/least likely to false-positive first.</summary>
    private static readonly Regex[] LeadingTimestampRegexCandidates =
    {
        LeadingTimestampRegexBracketed,
        LeadingTimestampRegexNumeric,
        LeadingTimestampRegexMonthFirst,
        LeadingTimestampRegexEpoch
    };

    // Default/legacy single regex, kept for any external code (and the "ts"/group-1 fallback
    // path) that still references "the" leading timestamp regex.
    private static readonly Regex LeadingTimestampRegex = LeadingTimestampRegexNumeric;

    // Looser check: "does this string contain something date-shaped AND time-shaped anywhere".
    private static readonly Regex LooksLikeDateTime = new(
        @"(\d{1,4}[-/.][A-Za-z]{0,3}[-/.]?\d{0,4}[-/.]?\d{0,4}|[A-Za-z]{3,9}\.?\s+\d{1,2},?(?:\s+\d{2,4})?).{0,3}\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?",
        RegexOptions.Compiled);

    private static readonly Regex LooksLikeDateOnly = new(
        @"^\d{1,4}[-/.][A-Za-z0-9]{1,4}[-/.]\d{1,4}$|^[A-Za-z]{3,9}\.?\s+\d{1,2},?(?:\s+\d{2,4})?$", RegexOptions.Compiled);

    private static readonly Regex LooksLikeTimeOnly = new(
        @"^\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?\s*([AaPp][Mm])?$", RegexOptions.Compiled);

    // Matches the fractional-seconds portion right after "hh:mm:ss" so it can be normalized
    // to a fixed width regardless of how many digits the source log actually wrote.
    private static readonly Regex FractionalSecondsRegex = new(@"(?<=:\d{2})[.,](?<frac>\d+)", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<string, Regex> UserRegexCache = new();

    /// <summary>Compiled NodaTime patterns, keyed by the same .NET-style format string used for
    /// DateTime.TryParseExact - CreateWithInvariantCulture does real parsing work on the pattern
    /// text itself, so this avoids redoing that on every line during a multi-million-row
    /// ingestion. Cached without a template value; TryParseWithNodaTime supplies one per call via
    /// WithTemplateValue, which is a cheap field swap, not a re-parse.</summary>
    private static readonly ConcurrentDictionary<string, LocalDateTimePattern> NodaPatternCache = new();

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
        var (regex, match) = MatchLeadingTimestamp(sampleLine);
        if (regex is null || match is null)
        {
            return Fail("No timestamp-shaped text found at the start of the line. " +
                         "If the timestamp isn't at the very start, pick CSV or Tab-delimited instead.");
        }

        var raw = ExtractTimestampText(match);
        var profile = new TimestampProfile
        {
            Mode = TimestampLocationMode.LineStart,
            RegexPattern = regex.ToString(),
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

    /// <summary>Tries each candidate leading-timestamp regex in turn, returning the first one
    /// that matches (and its match), or (null, null) if none do.</summary>
    private static (Regex? Regex, Match? Match) MatchLeadingTimestamp(string line)
    {
        foreach (var regex in LeadingTimestampRegexCandidates)
        {
            var match = regex.Match(line);
            if (match.Success) return (regex, match);
        }
        return (null, null);
    }

    /// <summary>Finds the span of whatever leading-timestamp shape (bracketed, numeric,
    /// month-first, or epoch) this line starts with, if any - reuses the exact same regex
    /// candidates AutoDetectLineStartMultiLine votes across, so "does this line look like it has
    /// a timestamp" is answered identically everywhere in the app. Used by
    /// TimestampPickerViewModel both to rank sample lines (prefer showing the user a line that
    /// actually has a recognizable timestamp over one that doesn't, e.g. a CSV/export header row
    /// mixed into the sample) and to offer a single one-click "use this" suggestion instead of
    /// making the user select+tag every digit run by hand.</summary>
    public static (int Start, int Length)? FindLikelyTimestampSpan(string line)
    {
        var (regex, match) = MatchLeadingTimestamp(line);
        if (regex is null || match is null) return null;

        var tsGroup = match.Groups["ts"];
        return tsGroup.Success ? (tsGroup.Index, tsGroup.Length) : (match.Index, match.Length);
    }

    /// <summary>Extracts the matched timestamp text from a leading-timestamp match, combining
    /// separate "date"+"time" named groups (produced by the interactive region picker - see
    /// TimestampPickerViewModel) if present, else falling back to "ts", else group 1.</summary>
    private static string ExtractTimestampText(Match match)
    {
        var dateGroup = match.Groups["date"];
        var timeGroup = match.Groups["time"];
        if (dateGroup.Success && timeGroup.Success)
            return $"{dateGroup.Value.Trim()} {timeGroup.Value.Trim()}";

        if (match.Groups["ts"].Success) return match.Groups["ts"].Value;
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
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

        // Try each candidate regex shape in turn; use whichever one gets the best match rate
        // across the sample, so a file that's mostly syslog-style but has a few odd lines
        // still gets voted on consistently rather than the first shape "winning" outright.
        Regex? bestRegex = null;
        var bestHits = new List<(string raw, DateTime utc)>();
        double bestRate = 0;

        foreach (var regex in LeadingTimestampRegexCandidates)
        {
            var hits = new List<(string raw, DateTime utc)>();
            foreach (var line in nonEmpty)
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                var raw = ExtractTimestampText(match);
                var probeProfile = new TimestampProfile { Mode = TimestampLocationMode.LineStart, RegexPattern = regex.ToString() };
                if (TryParseTimestampText(raw, probeProfile, out var utc))
                    hits.Add((raw, utc));
            }

            double rate = hits.Count / (double)nonEmpty.Count;
            if (rate > bestRate)
            {
                bestRate = rate;
                bestHits = hits;
                bestRegex = regex;
            }
        }

        if (bestRegex is null || bestHits.Count == 0 || bestRate < 0.3)
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
            RegexPattern = bestRegex.ToString(),
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
                    ExampleRawText = bestHits[0].raw,
                    ExampleUtc = bestHits[0].utc,
                    MatchRate = bestRate
                }
            },
            Message = $"Matched {bestHits.Count} of {nonEmpty.Count} sample lines ({bestRate:P0})."
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

        originalText = ExtractTimestampText(match);
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

        var raw = ExtractTimestampText(match);
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
    /// FormatString (if set), a Unix epoch seconds/milliseconds check (for all-digit text of the
    /// right length), the built-in candidate library, then a culture-invariant free-form
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

        if (TryParseEpoch(text, out utcResult))
            return true;

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

    /// <summary>Recognizes a bare Unix epoch value (seconds or milliseconds since 1970) as a
    /// timestamp: exactly 10 digits (seconds, valid roughly 2001-2286) or exactly 13 digits
    /// (milliseconds). Deliberately strict about digit count so it doesn't misfire on ordinary
    /// numeric IDs/sequence numbers that happen to appear where a timestamp is expected.</summary>
    private static bool TryParseEpoch(string text, out DateTime utcResult)
    {
        utcResult = DateTime.MinValue;
        if (text.Length != 10 && text.Length != 13) return false;
        if (!text.All(char.IsDigit)) return false;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;

        try
        {
            // NodaTime's Instant for the epoch math (its representable range is vastly larger
            // than DateTimeOffset's, so this itself essentially never throws) - the bounds check
            // below is what actually guards against a 10/13-digit non-timestamp number being
            // misread as a wildly implausible date, same as before.
            var instant = text.Length == 10
                ? Instant.FromUnixTimeSeconds(value)
                : Instant.FromUnixTimeMilliseconds(value);

            var utc = instant.ToDateTimeUtc();
            if (utc.Year < 2001 || utc.Year > 2100) return false;

            utcResult = utc;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryParseExactNormalized(string text, string format, DateTimeStyles styles, out DateTime utcResult)
    {
        var fracLen = CountTrailingFractionalSpecifier(format);
        var candidate = fracLen > 0 ? NormalizeFractionalSeconds(text, fracLen) : text;

        // A "zzz"-bearing format expects an explicit UTC offset in the text (e.g. "-04:00") -
        // normalize a colon-less one ("-0400", what this app's own leading-timestamp regexes
        // actually capture) before either parse path sees it.
        var hasOffset = format.Contains("zzz", StringComparison.Ordinal);
        if (hasOffset) candidate = NormalizeOffset(candidate);

        // NodaTime first: the same pattern-letter dialect as a .NET custom DateTime format
        // string for the parts that matter here (y/M/d/H/h/m/s/f/tt), but explicit rather than
        // implicit about anything the pattern doesn't specify - see TryParseWithNodaTime. The
        // two ISO-8601 "K" (offset-or-Z) formats and the "zzz" (explicit-offset) formats above
        // skip this path: LocalDateTimePattern parses an offset-*less* LocalDateTime, so neither
        // has a direct equivalent - those go straight to DateTime.TryParseExact below (with
        // AdjustToUniversal - see below) instead, same as before NodaTime was introduced.
        if (!format.Contains('K') && !hasOffset && TryParseWithNodaTime(candidate, format, out utcResult))
        {
            return true;
        }

        // Fallback - and the only path for "K"/"zzz" formats. Left exactly as it worked before
        // NodaTime was introduced (aside from the AdjustToUniversal addition just below), so it
        // can only ever catch what the NodaTime path above missed, never do worse.
        //
        // Formats with no year specifier (syslog-style "MMM d HH:mm:ss") need the *current*
        // year filled in, not year 1 - NoCurrentDateDefault would otherwise leave it at 0001.
        var hasYear = format.Contains("yyyy", StringComparison.Ordinal) || format.Contains("yy", StringComparison.Ordinal);
        var effectiveStyles = hasYear ? styles : DateTimeStyles.AllowWhiteSpaces;

        // An explicit offset in the text should actually shift the parsed value to true UTC
        // (e.g. "14:23:05 -04:00" -> 18:23:05 UTC), not just be matched-and-ignored the way it
        // would be without this flag.
        if (hasOffset) effectiveStyles |= DateTimeStyles.AdjustToUniversal;

        // BUG FIX (found via the generated-data simulation harness - tests/LogAggregator.
        // SampleData/LogAggregator.Tests - "iso8601-z"/"iso8601-z-frac" cases): a "K"-bearing
        // format with no explicit "zzz" (i.e. an ISO-8601 "...THH:mm:ssK" style format, whose
        // offset is either "Z" or absent) needs DateTimeStyles.RoundtripKind here. Without it,
        // .NET's documented behavior for "K" + a "Z"-suffixed string is to convert the parsed
        // value into the LOCAL MACHINE'S time zone and report Kind=Local - and the
        // DateTime.SpecifyKind(..., Utc) call below only relabels the result, it doesn't
        // convert it back. On a UTC-5 machine, "2026-02-23T10:43:50Z" was silently coming out
        // as 2026-02-23T05:43:50 stamped "Utc" - a 5-hour corruption that would only ever show
        // up on a machine whose local offset isn't zero, which is exactly why nothing caught
        // it until synthetic data with known-correct expected values was run through this. With
        // RoundtripKind, .NET instead keeps "Z" as true UTC (Kind=Utc, no shift) and would keep
        // a genuinely offset-less value as Unspecified (harmless - still relabeled Utc below).
        if (format.Contains('K', StringComparison.Ordinal)) effectiveStyles |= DateTimeStyles.RoundtripKind;

        if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, effectiveStyles, out var parsed))
        {
            utcResult = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        utcResult = DateTime.MinValue;
        return false;
    }

    /// <summary>
    /// Parses <paramref name="text"/> against a .NET-style custom format string using NodaTime's
    /// LocalDateTimePattern. NodaTime's custom pattern letters (y/M/d/H/h/m/s/f/tt) match the
    /// same dialect CandidateFormats already uses, so the great majority of those format strings
    /// work here completely unmodified - no per-format translation needed.
    ///
    /// The real difference from DateTime.TryParseExact is TemplateValue: NodaTime refuses to
    /// guess anything a pattern doesn't explicitly specify, so a template supplies "today" (UTC)
    /// for whatever's missing - the current year for year-less syslog-style formats, and the
    /// century for 2-digit-year formats - explicitly, rather than DateTime.TryParseExact's
    /// ambient OS-locale-dependent year windowing (a real, if obscure, source of "silently used
    /// the wrong century" bugs in the original approach).
    /// </summary>
    private static bool TryParseWithNodaTime(string text, string format, out DateTime utcResult)
    {
        utcResult = DateTime.MinValue;

        if (!NodaPatternCache.TryGetValue(format, out var basePattern))
        {
            try
            {
                basePattern = LocalDateTimePattern.CreateWithInvariantCulture(format);
            }
            catch (InvalidPatternException)
            {
                // A format string NodaTime's (slightly stricter) pattern parser rejects outright
                // - shouldn't happen for anything in CandidateFormats, but a hand-typed manual
                // format string in the LogType editor could contain something it's pickier
                // about. Don't cache the failure (format strings here come from a small fixed
                // list plus rare manual entries, so the miss is bounded) - just fall through to
                // the DateTime.TryParseExact fallback in the caller.
                return false;
            }
            NodaPatternCache[format] = basePattern;
        }

        var now = DateTime.UtcNow;
        var template = new LocalDateTime(now.Year, now.Month, now.Day, 0, 0, 0);
        var result = basePattern.WithTemplateValue(template).Parse(text);

        if (!result.Success) return false;

        utcResult = DateTime.SpecifyKind(result.Value.ToDateTimeUnspecified(), DateTimeKind.Utc);
        return true;
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

    // Matches a trailing signed 4-digit UTC offset with no colon - e.g. the "-0400" in
    // "2026-08-08 14:23:05 -0400". Anchored to the end of the (already-extracted, already-
    // trimmed) timestamp text, since the offset is always the last thing in it - see the
    // LeadingTimestampRegex* candidates, which all capture an optional trailing offset as the
    // last part of their "ts" group.
    private static readonly Regex ColonlessTrailingOffsetRegex = new(@"(?<sign>[+-])(?<oh>\d{2})(?<om>\d{2})$", RegexOptions.Compiled);

    /// <summary>Inserts a colon into a trailing signed 4-digit UTC offset with none ("-0400" -&gt;
    /// "-04:00") - needed because .NET's "zzz" custom format specifier only ever matches a
    /// colon-separated offset, but plenty of real logs (and this app's own leading-timestamp
    /// regexes) write it without one. Same normalize-before-parse approach as
    /// NormalizeFractionalSeconds, just for the offset instead of the fractional digits.
    /// Text with no trailing offset, or one that already has a colon, passes through
    /// unchanged.</summary>
    public static string NormalizeOffset(string text) =>
        ColonlessTrailingOffsetRegex.Replace(text, m => $"{m.Groups["sign"].Value}{m.Groups["oh"].Value}:{m.Groups["om"].Value}");

    private static Regex GetRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return LeadingTimestampRegex;
        return UserRegexCache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled));
    }

    // ===================================================================
    // Internal: exposed for TimestampTokenizer/TimestampPickerViewModel's "does this chunk
    // look like a date/time" hints.
    // ===================================================================

    internal static bool LooksLikeDateOnlyToken(string text) => LooksLikeDateOnly.IsMatch(text);
    internal static bool LooksLikeTimeOnlyToken(string text) => LooksLikeTimeOnly.IsMatch(text);
    internal static bool LooksLikeDateTimeToken(string text) => LooksLikeDateTime.IsMatch(text);
}
