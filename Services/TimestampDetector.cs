using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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
        "yyyy.MM.dd HH:mm:ss.fffffff",
        "yyyy.MM.dd HH:mm:ss",
        "yyyyMMdd HHmmss.fffffff",
        "yyyyMMdd HHmmss",
        "yyyyMMddHHmmss",
        "MM-dd-yyyy HH:mm:ss.fffffff",
        "MM-dd-yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss.fffffff",
        "MM/dd/yyyy HH:mm:ss",
        // Slash-delimited date with a literal "T" separator instead of a space - not real ISO
        // 8601 (which uses hyphens), but a real format some devices emit.
        "MM/dd/yyyy'T'HH:mm:ss.fffffff",
        "MM/dd/yyyy'T'HH:mm:ss",
        "M/d/yyyy'T'H:mm:ss",
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

    // ===================================================================
    // Entity/proximity-based heuristic detection - see TryEntityHeuristic below for the full
    // explanation. These two regexes independently recognize "this chunk looks like a date" and
    // "this chunk looks like a time" ANYWHERE within the leading part of a line, rather than
    // requiring one rigid combined shape to match the whole leading timestamp in one go.
    // ===================================================================

    /// <summary>Restricts the month-name-first/day-first date entity alternatives (below) to
    /// actual month names, rather than any 3-9 letter word - without this, a word like "INFO" or
    /// an hour like "14" right after a syslog-style "Aug  8" would get greedily swallowed into a
    /// false date match (mistaking the log level or the time's own hour for part of the date).</summary>
    private const string MonthNamePattern =
        @"(?:Jan|January|Feb|February|Mar|March|Apr|April|May|Jun|June|Jul|July|Aug|August|Sep|Sept|September|Oct|October|Nov|November|Dec|December)";

    // The trailing "(?:\s+\d{2,4}(?!:))?" year group on the month-name alternatives requires
    // that the digits NOT be immediately followed by ":" - without that, "Aug  8 14:23:05"
    // would greedily swallow the time's own "14" as if it were a 2-digit year, since a bare
    // \d{2,4} can't otherwise tell a year apart from an hour that happens to be 2-4 digits.
    private static readonly Regex DateEntityRegex = new(
        $@"\d{{1,4}}[-/.]\d{{1,2}}[-/.]\d{{1,4}}|{MonthNamePattern}\.?\s+\d{{1,2}}(?:st|nd|rd|th)?,?(?:\s+\d{{2,4}}(?!:))?|\d{{1,2}}\s+{MonthNamePattern}\.?(?:\s+\d{{2,4}}(?!:))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimeEntityRegex = new(
        @"\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:\s*[AaPp][Mm])?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?",
        RegexOptions.Compiled);

    /// <summary>Only the leading part of a line is scanned for date/time entities, so a later,
    /// unrelated date mentioned in the message body (e.g. "(originally logged Jul 15
    /// 09:00:00)") is never mistaken for the line's own leading timestamp - matching what the 4
    /// fixed regexes above already assume implicitly via their ^ anchor.</summary>
    private const int LeadingWindowLength = 80;

    /// <summary>Small tolerance for leading whitespace/punctuation before the first recognized
    /// entity - a pairing that starts well into the line reflects that ONE sample line's own
    /// wording (a device name, a log level) rather than a shape every line in the file shares, so
    /// it's rejected rather than turned into an over-fit profile.</summary>
    private const int LineStartSlack = 2;

    private readonly struct TextEntity
    {
        public TextEntity(int start, int length, string text)
        {
            Start = start;
            Length = length;
            Text = text;
        }

        public int Start { get; }
        public int Length { get; }
        public int End => Start + Length;
        public string Text { get; }
    }

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
        string bestDescription = "Start of line";

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
                bestDescription = "Start of line";
            }
        }

        // Additional candidate source: independently recognize date-shaped and time-shaped
        // chunks and reason about how they relate, rather than only matching one of the 4 fixed
        // shapes above wholesale. Two different pairing strategies are tried (nearest-by-distance
        // and first-in-reading-order - see their doc comments below) since real logs aren't
        // consistent about which one is "correct"; both just add more candidates to the SAME vote
        // above, so a heuristic guess only wins if it actually fits the sample better than every
        // fixed regex already tried.
        foreach (var pairSelector in new Func<List<TextEntity>, List<TextEntity>, (TextEntity date, TextEntity time)?>[]
                 { PairByNearestDistance, PairByReadingOrder })
        {
            var heuristic = TryEntityHeuristic(nonEmpty, pairSelector);
            if (heuristic is null) continue;

            var (regex, hits, rate) = heuristic.Value;
            if (rate > bestRate)
            {
                bestRate = rate;
                bestHits = hits;
                bestRegex = regex;
                bestDescription = "Start of line (date and time recognized independently and paired by proximity)";
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
            Description = bestDescription
        };

        return new MultiLineDetectionResult
        {
            Status = DetectionStatus.Confident,
            Candidates = new List<TimestampCandidate>
            {
                new()
                {
                    Profile = profile,
                    Description = bestDescription,
                    ExampleRawText = bestHits[0].raw,
                    ExampleUtc = bestHits[0].utc,
                    MatchRate = bestRate
                }
            },
            Message = $"Matched {bestHits.Count} of {nonEmpty.Count} sample lines ({bestRate:P0})."
        };
    }

    /// <summary>
    /// Tries the entity/proximity heuristic against the sample lines using one pairing strategy,
    /// returning the resulting regex+profile candidate and its overall hit rate (or null if no
    /// sample line yields a usable pairing). The candidate SHAPE is derived from the first sample
    /// line where date+time entities are found and pair up successfully - real device names/
    /// messages differ line to line, but the regex built from that one seed line only survives
    /// as a candidate if it then ALSO matches a good fraction of the other lines, exactly like
    /// the fixed-regex candidates above.
    /// </summary>
    private static (Regex Regex, List<(string raw, DateTime utc)> Hits, double Rate)? TryEntityHeuristic(
        List<string> nonEmptyLines,
        Func<List<TextEntity>, List<TextEntity>, (TextEntity date, TextEntity time)?> pairSelector)
    {
        foreach (var seedLine in nonEmptyLines)
        {
            var window = seedLine.Length > LeadingWindowLength ? seedLine.Substring(0, LeadingWindowLength) : seedLine;
            var dates = DateEntityRegex.Matches(window).Cast<Match>().Select(m => new TextEntity(m.Index, m.Length, m.Value)).ToList();
            var times = TimeEntityRegex.Matches(window).Cast<Match>().Select(m => new TextEntity(m.Index, m.Length, m.Value)).ToList();
            if (dates.Count == 0 || times.Count == 0) continue;

            var pair = pairSelector(dates, times);
            if (pair is null) continue;

            var regex = BuildEntityRegex(seedLine, pair.Value.date, pair.Value.time);
            if (regex is null) continue;

            var hits = new List<(string raw, DateTime utc)>();
            foreach (var line in nonEmptyLines)
            {
                var match = regex.Match(line);
                if (!match.Success) continue;

                var raw = ExtractTimestampText(match);
                var probe = new TimestampProfile { Mode = TimestampLocationMode.LineStart, RegexPattern = regex.ToString() };
                if (TryParseTimestampText(raw, probe, out var utc))
                    hits.Add((raw, utc));
            }

            double rate = hits.Count / (double)nonEmptyLines.Count;
            return (regex, hits, rate);
        }

        return null;
    }

    /// <summary>Nearest-by-distance pairing: among every (date, time) combination found in the
    /// leading window, pick whichever pair has the smallest character gap between them, anchored
    /// on whichever entity (date or time) starts essentially at the line's beginning. Handles
    /// either order (a leading date followed by a time, or - less commonly - a leading time
    /// followed by a date) since real formats aren't consistent about which comes first, and
    /// prefers a genuinely adjacent date+time over a coincidental date/time-shaped run elsewhere
    /// in a longer leading window.</summary>
    private static (TextEntity date, TextEntity time)? PairByNearestDistance(List<TextEntity> dates, List<TextEntity> times)
    {
        var anchorDate = dates.Where(d => d.Start <= LineStartSlack).OrderBy(d => d.Start).Cast<TextEntity?>().FirstOrDefault();
        var anchorTime = times.Where(t => t.Start <= LineStartSlack).OrderBy(t => t.Start).Cast<TextEntity?>().FirstOrDefault();

        if (anchorDate is { } date)
        {
            var nearestTime = times.Where(t => !Overlaps(date, t)).OrderBy(t => Gap(date, t)).Cast<TextEntity?>().FirstOrDefault();
            return nearestTime is { } time ? (date, time) : null;
        }

        if (anchorTime is { } time2)
        {
            var nearestDate = dates.Where(d => !Overlaps(d, time2)).OrderBy(d => Gap(d, time2)).Cast<TextEntity?>().FirstOrDefault();
            return nearestDate is { } date2 ? (date2, time2) : null;
        }

        return null;
    }

    /// <summary>First-in-reading-order pairing: literally "read left to right - the first time
    /// found belongs with the leading date", the exact scenario the user described as one
    /// example (a leading "start" timestamp, with a second, unrelated "end" timestamp - or an
    /// embedded date in the message text - appearing only later in the line). Distinct from
    /// nearest-by-distance: with 3+ candidate entities in the window this can pick a different
    /// pair than the closest-gap one, so both are tried as independent candidates and the
    /// multi-line vote in AutoDetectLineStartMultiLine decides which one actually fits the real
    /// sample data, rather than either heuristic being trusted outright.</summary>
    private static (TextEntity date, TextEntity time)? PairByReadingOrder(List<TextEntity> dates, List<TextEntity> times)
    {
        var anchorDate = dates.Where(d => d.Start <= LineStartSlack).OrderBy(d => d.Start).Cast<TextEntity?>().FirstOrDefault();
        if (anchorDate is { } date)
        {
            var firstTimeAfter = times.Where(t => t.Start >= date.End).OrderBy(t => t.Start).Cast<TextEntity?>().FirstOrDefault();
            return firstTimeAfter is { } time ? (date, time) : null;
        }

        var anchorTime = times.Where(t => t.Start <= LineStartSlack).OrderBy(t => t.Start).Cast<TextEntity?>().FirstOrDefault();
        if (anchorTime is { } time2)
        {
            var firstDateAfter = dates.Where(d => d.Start >= time2.End).OrderBy(d => d.Start).Cast<TextEntity?>().FirstOrDefault();
            return firstDateAfter is { } date2 ? (date2, time2) : null;
        }

        return null;
    }

    private static bool Overlaps(TextEntity a, TextEntity b) => a.Start < b.End && b.Start < a.End;

    private static int Gap(TextEntity a, TextEntity b) =>
        a.Start >= b.End ? a.Start - b.End : (b.Start >= a.End ? b.Start - a.End : 0);

    /// <summary>Builds an anchored (^\s*...) regex reproducing the seed line's structure from the
    /// earlier entity through the end of the later one: literal text (escaped) for the separator
    /// between them, and a generic digit/letter-class pattern for each token within each entity
    /// (via <see cref="BuildBlendedSpanPattern"/>) so other lines' differing literal values still
    /// match. The earlier entity is wrapped in a named "date" or "time" group and the later one
    /// in the other - the same (?&lt;date&gt;...)(?&lt;time&gt;...) convention the interactive
    /// token picker uses (see TimestampPickerViewModel.BuildRegexPattern), which
    /// ExtractTimestampText already knows how to combine.</summary>
    private static Regex? BuildEntityRegex(string line, TextEntity dateEntity, TextEntity timeEntity)
    {
        var first = dateEntity.Start <= timeEntity.Start ? dateEntity : timeEntity;
        var second = dateEntity.Start <= timeEntity.Start ? timeEntity : dateEntity;
        bool firstIsDate = dateEntity.Start <= timeEntity.Start;

        // Only build a general profile from a pairing anchored near the very start of the line -
        // see LineStartSlack's doc comment for why.
        if (first.Start > LineStartSlack) return null;

        var allTokens = TimestampTokenizer.Tokenize(line);

        var firstPattern = BuildBlendedSpanPattern(line, allTokens, first.Start, first.End);
        var betweenLiteral = second.Start > first.End
            ? SeparatorPattern(line.Substring(first.End, second.Start - first.End))
            : string.Empty;
        var secondPattern = BuildBlendedSpanPattern(line, allTokens, second.Start, second.End);

        var firstGroup = firstIsDate ? "date" : "time";
        var secondGroup = firstIsDate ? "time" : "date";

        // What comes before the first entity (within the small LineStartSlack tolerance) isn't
        // always whitespace - a bracketed/parenthesized timestamp has a literal "[" or "("
        // there. Pure whitespace (or nothing) stays flexible (\s*, so lines with slightly
        // different padding still match); anything else is taken as a literal the format always
        // has in that spot, e.g. the bracket itself.
        var prefixText = line.Substring(0, first.Start);
        var prefixPattern = string.IsNullOrWhiteSpace(prefixText) ? @"\s*" : Regex.Escape(prefixText);

        var pattern = $@"^{prefixPattern}(?<{firstGroup}>{firstPattern}){betweenLiteral}(?<{secondGroup}>{secondPattern})";

        try
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            // A malformed dynamically-built pattern should never crash detection - it just means
            // this particular seed line/pairing doesn't produce a usable candidate.
            return null;
        }
    }

    /// <summary>Builds a generic character-class pattern for the [spanStart, spanEnd) slice of
    /// <paramref name="line"/>, using the already-tokenized runs in <paramref name="allTokens"/>:
    /// \d+ for a digit run, [A-Za-z]+ for a letter run, and escaped literal text for whatever
    /// separator sits between tokens (e.g. "-" or "/") - so the resulting pattern matches the
    /// same SHAPE on other lines even when the actual digits/letters differ.</summary>
    private static string BuildBlendedSpanPattern(string line, List<TimestampToken> allTokens, int spanStart, int spanEnd)
    {
        var sb = new StringBuilder();
        int cursor = spanStart;

        foreach (var token in allTokens)
        {
            if (token.StartIndex + token.Length <= spanStart) continue;
            if (token.StartIndex >= spanEnd) break;

            if (token.StartIndex > cursor)
                sb.Append(SeparatorPattern(line.Substring(cursor, token.StartIndex - cursor)));

            sb.Append(token.IsNumeric ? @"\d+" : "[A-Za-z]+");
            cursor = token.StartIndex + token.Length;
        }

        if (cursor < spanEnd)
            sb.Append(SeparatorPattern(line.Substring(cursor, spanEnd - cursor)));

        return sb.ToString();
    }

    /// <summary>A whitespace-only separator becomes a generic \s+ rather than an exact escaped
    /// literal, so a regex built from one sample line's spacing still matches another line whose
    /// spacing differs slightly - e.g. traditional syslog's single-digit-day padding, where a
    /// single-digit day gets an extra leading space to line up with double-digit days. Any other
    /// separator (a literal "-", "/", ",", a bracket, etc.) stays an exact escaped literal, since
    /// that punctuation is part of the format itself, not incidental spacing.</summary>
    private static string SeparatorPattern(string separator) =>
        separator.Length > 0 && string.IsNullOrWhiteSpace(separator) ? @"\s+" : Regex.Escape(separator);

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
            var dto = text.Length == 10
                ? DateTimeOffset.FromUnixTimeSeconds(value)
                : DateTimeOffset.FromUnixTimeMilliseconds(value);

            // Sanity-bound to roughly year 2001-2100 so a 10/13-digit non-timestamp number
            // (unlikely, but possible) doesn't get misread as a wildly implausible date.
            if (dto.Year < 2001 || dto.Year > 2100) return false;

            utcResult = dto.UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseExactNormalized(string text, string format, DateTimeStyles styles, out DateTime utcResult)
    {
        var fracLen = CountTrailingFractionalSpecifier(format);
        var candidate = fracLen > 0 ? NormalizeFractionalSeconds(text, fracLen) : text;

        // Formats with no year specifier (syslog-style "MMM d HH:mm:ss") need the *current*
        // year filled in, not year 1 - NoCurrentDateDefault would otherwise leave it at 0001.
        var hasYear = format.Contains("yyyy", StringComparison.Ordinal) || format.Contains("yy", StringComparison.Ordinal);
        var effectiveStyles = hasYear ? styles : DateTimeStyles.AllowWhiteSpaces;

        if (DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, effectiveStyles, out var parsed))
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

    // ===================================================================
    // Internal: exposed for TimestampTokenizer/TimestampPickerViewModel's "does this chunk
    // look like a date/time" hints.
    // ===================================================================

    internal static bool LooksLikeDateOnlyToken(string text) => LooksLikeDateOnly.IsMatch(text);
    internal static bool LooksLikeTimeOnlyToken(string text) => LooksLikeTimeOnly.IsMatch(text);
    internal static bool LooksLikeDateTimeToken(string text) => LooksLikeDateTime.IsMatch(text);
}
