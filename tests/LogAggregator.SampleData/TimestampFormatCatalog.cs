using System;
using System.Collections.Generic;

namespace LogAggregator.SampleData;

/// <summary>
/// SIMULATION SUPPORT - describes one timestamp "shape" the generator can render and the real
/// TimestampDetector can (in theory) parse back. This is deliberately a data-only catalog: the
/// generator renders text from it, the real app's TimestampDetector.AutoDetectFromLines /
/// LineStartsWithTimestamp / TryParseColumnTimestamp parse that text back, and SimulationRunner
/// compares the two. No parsing logic is duplicated here.
/// </summary>
public sealed class SampleTimestampFormat
{
    /// <summary>Short identifier used in file names and report output, e.g. "iso8601-frac".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Human-readable description for report output.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// The .NET custom DateTime format string used to render this shape (also matches one of
    /// the entries in TimestampDetector.CandidateFormats, so it's exercising a format the real
    /// parser already knows about - this project intentionally does not invent formats the app
    /// can't already handle).  Ignored when <see cref="IsEpoch"/> is true.
    /// </summary>
    public string NetFormat { get; init; } = string.Empty;

    /// <summary>
    /// True if this shape is safe to put in a CSV/TabDelimited column. Some shapes are
    /// FlatText-only by design (see the "known gap" note in SupportsDelimited = false entries
    /// below) - putting them in a delimited column would produce a simulation "failure" that's
    /// actually a pre-existing, separate, already-understood limitation of
    /// AutoDetectDelimitedMultiLine (it only looks at columns that pass the LooksLikeDateTime
    /// regex, which requires a date-shaped separator + a colon-separated time; it has no
    /// "best effort" fallback pass the way the single-line AutoDetectDelimited does). Keeping
    /// those shapes out of CSV generation keeps the simulation report meaningful: every
    /// reported failure is a real regression, not a rediscovery of a known gap.
    /// </summary>
    public bool SupportsDelimited { get; init; } = true;

    /// <summary>
    /// False for "no year in the text" shapes (classic syslog: "Jan  2 15:04:05"). When false,
    /// SampleLogGenerator forces that record's year to the current year, because that's what
    /// TimestampDetector itself does for a format with no "yyyy"/"yy" (see the
    /// DateTimeStyles.NoCurrentDateDefault comment in TryParseExactNormalized) - DateTime's
    /// own TryParseExact fills a missing year from today's date, not year 1.
    /// </summary>
    public bool HasYear { get; init; } = true;

    /// <summary>True for the two Unix-epoch shapes, which don't use NetFormat at all.</summary>
    public bool IsEpoch { get; init; }

    /// <summary>Epoch shape is milliseconds-since-1970 (13 digits) instead of seconds (10).</summary>
    public bool EpochMillis { get; init; }

    /// <summary>True if NetFormat contains a "zzz" (signed UTC offset) specifier - the
    /// generator renders these via DateTimeOffset with a randomly-chosen offset instead of the
    /// sandbox machine's own local offset, and randomly omits the colon (e.g. "-0400" instead
    /// of "-04:00") to exercise TimestampDetector.NormalizeOffset.</summary>
    public bool HasOffset => NetFormat.Contains("zzz", StringComparison.Ordinal);

    /// <summary>
    /// False for a shape that TimestampDetector's LOCATION step (the leading-timestamp regexes
    /// for FlatText, LooksLikeDateTime for CSV/Tab columns) can never find in the first place -
    /// as opposed to a parsing bug. Every one of those regexes requires either a date separator
    /// (-/.) or a time colon (:) to recognize something as timestamp-shaped; a fully compact
    /// format like "yyyyMMdd HHmmss" has neither, so it's never even offered to the parser -
    /// confirmed by the simulation harness itself (see TimestampFormatCatalogTests, "compact").
    /// TimestampDetector.CandidateFormats still lists it (so a user who manually types this
    /// exact pattern into the wizard's override still gets it parsed correctly - the PARSE step
    /// has no problem with it), it just can't be auto-detected. Left true for everything else.
    /// </summary>
    public bool SupportsAutoDetection { get; init; } = true;

    public override string ToString() => Name;
}

/// <summary>
/// SIMULATION SUPPORT - the full set of timestamp shapes the generator draws from. Each entry
/// mirrors a real entry in TimestampDetector.CandidateFormats (or, for the epoch entries, the
/// dedicated LeadingTimestampRegexEpoch/TryParseEpoch path) so a "failure" here means the real
/// app regressed on a shape it's documented to support - not that the catalog invented
/// something new.
/// </summary>
public static class TimestampFormatCatalog
{
    public static readonly IReadOnlyList<SampleTimestampFormat> All = new[]
    {
        // ---- Delimited-eligible shapes (used for CSV columns and/or flat-text lines) ----
        new SampleTimestampFormat
        {
            Name = "iso8601-z-frac",
            Description = "ISO-8601, 'T' separator, 7-digit fraction, 'Z' (yyyy-MM-ddTHH:mm:ss.fffffffK)",
            NetFormat = "yyyy-MM-ddTHH:mm:ss.fffffffK",
        },
        new SampleTimestampFormat
        {
            Name = "iso8601-z",
            Description = "ISO-8601, 'T' separator, whole seconds, 'Z' (yyyy-MM-ddTHH:mm:ssK)",
            NetFormat = "yyyy-MM-ddTHH:mm:ssK",
        },
        new SampleTimestampFormat
        {
            Name = "space-sep-frac",
            Description = "Space-separated, 7-digit fraction (yyyy-MM-dd HH:mm:ss.fffffff)",
            NetFormat = "yyyy-MM-dd HH:mm:ss.fffffff",
        },
        new SampleTimestampFormat
        {
            Name = "space-sep",
            Description = "Space-separated, whole seconds (yyyy-MM-dd HH:mm:ss)",
            NetFormat = "yyyy-MM-dd HH:mm:ss",
        },
        new SampleTimestampFormat
        {
            Name = "space-sep-offset-frac",
            Description = "Space-separated with a signed UTC offset and fraction (yyyy-MM-dd HH:mm:ss.fffffff zzz)",
            NetFormat = "yyyy-MM-dd HH:mm:ss.fffffff zzz",
        },
        new SampleTimestampFormat
        {
            Name = "space-sep-offset",
            Description = "Space-separated with a signed UTC offset (yyyy-MM-dd HH:mm:ss zzz) - this is the exact shape from the bug report (the '-0400' that wasn't being captured)",
            NetFormat = "yyyy-MM-dd HH:mm:ss zzz",
        },
        new SampleTimestampFormat
        {
            Name = "us-slash-offset",
            Description = "US-style slash date with a signed UTC offset (MM/dd/yyyy HH:mm:ss zzz)",
            NetFormat = "MM/dd/yyyy HH:mm:ss zzz",
        },
        new SampleTimestampFormat
        {
            Name = "us-slash",
            Description = "US-style slash date (MM/dd/yyyy HH:mm:ss)",
            NetFormat = "MM/dd/yyyy HH:mm:ss",
        },
        new SampleTimestampFormat
        {
            Name = "euro-slash",
            Description = "European-style slash date, day first (dd/MM/yyyy HH:mm:ss)",
            NetFormat = "dd/MM/yyyy HH:mm:ss",
        },
        new SampleTimestampFormat
        {
            Name = "dot-sep-frac",
            Description = "Dot-separated date, 7-digit fraction (yyyy.MM.dd HH:mm:ss.fffffff)",
            NetFormat = "yyyy.MM.dd HH:mm:ss.fffffff",
        },
        new SampleTimestampFormat
        {
            Name = "compact",
            Description = "Compact, no separators (yyyyMMdd HHmmss) - KNOWN GAP: not auto-detectable, see SupportsAutoDetection doc comment",
            NetFormat = "yyyyMMdd HHmmss",
            SupportsAutoDetection = false,
        },
        new SampleTimestampFormat
        {
            Name = "twelve-hour-frac",
            Description = "12-hour clock with AM/PM and fraction (M/d/yyyy h:mm:ss.fffffff tt)",
            NetFormat = "M/d/yyyy h:mm:ss.fffffff tt",
        },
        new SampleTimestampFormat
        {
            Name = "sql-style-frac",
            Description = "SQL-Server-style month abbreviation, 7-digit fraction (dd-MMM-yyyy HH:mm:ss.fffffff)",
            NetFormat = "dd-MMM-yyyy HH:mm:ss.fffffff",
        },
        new SampleTimestampFormat
        {
            Name = "two-digit-year",
            Description = "2-digit year (MM-dd-yy HH:mm:ss) - SampleLogGenerator keeps generated years within a few years of today so century inference can't push this outside the expected range",
            NetFormat = "MM-dd-yy HH:mm:ss",
        },

        // ---- FlatText-only shapes (see SupportsDelimited doc comment above for why) ----
        new SampleTimestampFormat
        {
            Name = "syslog-no-year",
            Description = "Classic syslog, no year, whole seconds (MMM d HH:mm:ss)",
            NetFormat = "MMM d HH:mm:ss",
            SupportsDelimited = false,
            HasYear = false,
        },
        new SampleTimestampFormat
        {
            Name = "syslog-no-year-frac",
            Description = "Classic syslog, no year, with fraction (MMM d HH:mm:ss.fffffff)",
            NetFormat = "MMM d HH:mm:ss.fffffff",
            SupportsDelimited = false,
            HasYear = false,
        },
        new SampleTimestampFormat
        {
            Name = "epoch-seconds",
            Description = "Unix epoch, whole seconds (10 digits)",
            SupportsDelimited = false,
            IsEpoch = true,
            EpochMillis = false,
        },
        new SampleTimestampFormat
        {
            Name = "epoch-millis",
            Description = "Unix epoch, milliseconds (13 digits)",
            SupportsDelimited = false,
            IsEpoch = true,
            EpochMillis = true,
        },
    };

    /// <summary>Subset that's safe to use for a CSV/TabDelimited timestamp column AND that
    /// TimestampDetector can actually auto-detect (see SupportsAutoDetection).</summary>
    public static IEnumerable<SampleTimestampFormat> DelimitedEligible()
    {
        foreach (var f in All)
            if (f.SupportsDelimited && f.SupportsAutoDetection)
                yield return f;
    }

    /// <summary>Every shape TimestampDetector can actually auto-detect, CSV-eligible or not -
    /// used for the TXT/FlatText draw pool, which (unlike CSV) accepts every shape except the
    /// handful marked SupportsAutoDetection = false.</summary>
    public static IEnumerable<SampleTimestampFormat> AutoDetectionEligible()
    {
        foreach (var f in All)
            if (f.SupportsAutoDetection)
                yield return f;
    }
}
