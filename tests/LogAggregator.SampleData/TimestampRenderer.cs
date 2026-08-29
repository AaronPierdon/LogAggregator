using System;
using System.Globalization;

namespace LogAggregator.SampleData;

/// <summary>SIMULATION SUPPORT - the result of rendering one timestamp: the raw text to embed
/// in a generated log line/column, plus the exact UTC instant that text represents once the
/// declared precision of the format has been applied. ExpectedUtc is what SimulationRunner
/// compares the real TimestampDetector's parsed value against - it is NOT simply "the random
/// value we started from," because a format like "yyyy-MM-dd HH:mm:ss" (no fraction) only
/// keeps whole-second precision, and a correct round trip through that format necessarily
/// loses the rest. Truncating ExpectedUtc to the same precision the text actually carries is
/// what makes the comparison meaningful instead of spuriously "failing" on lost precision that
/// was never recoverable in the first place.</summary>
public readonly record struct RenderedTimestamp(string Text, DateTime ExpectedUtc);

/// <summary>
/// SIMULATION SUPPORT - turns a (DateTime, SampleTimestampFormat) pair into rendered text, the
/// reverse of what TimestampDetector does. Kept entirely separate from TimestampDetector - this
/// class only ever WRITES sample data; it never parses anything.
/// </summary>
public static class TimestampRenderer
{
    /// <summary>Renders <paramref name="baseUtc"/> using <paramref name="format"/>.
    /// <paramref name="rng"/> is used for format-specific random choices (which UTC offset to
    /// use for an offset-bearing format, whether to omit the offset's colon) UNLESS
    /// <paramref name="forcedOffset"/> is given, in which case that exact offset is used
    /// instead of a randomly-chosen one - lets boundary tests pin a specific extreme offset
    /// (e.g. +14:00, -12:00) rather than hoping the random draw lands on it.</summary>
    public static RenderedTimestamp Render(SampleTimestampFormat format, DateTime baseUtc, Random rng, TimeSpan? forcedOffset = null)
    {
        if (format.IsEpoch)
        {
            // Epoch text carries whole-second precision (seconds form) or millisecond
            // precision (millis form) - never more.
            var fracDigits = format.EpochMillis ? 3 : 0;
            var truncated = TruncateToFractionalDigits(baseUtc, fracDigits);
            var dto = new DateTimeOffset(truncated, TimeSpan.Zero);
            var digits = format.EpochMillis ? dto.ToUnixTimeMilliseconds() : dto.ToUnixTimeSeconds();
            return new RenderedTimestamp(digits.ToString(CultureInfo.InvariantCulture), truncated);
        }

        var fracLen = CountTrailingFractionalDigits(format.NetFormat);
        var expected = TruncateToFractionalDigits(baseUtc, fracLen);

        if (format.HasOffset)
        {
            TimeSpan offset;
            if (forcedOffset is { } fo)
            {
                offset = fo;
            }
            else
            {
                // Pick a random-but-plausible signed offset so this isn't tied to whatever the
                // sandbox/build machine's own local offset happens to be. Half-hour offsets are
                // included on purpose (e.g. +05:30) since real logs do show up with them, and
                // +14:00 (the real legal maximum, used by Kiribati) is in the mix too.
                var offsetMinutesChoices = new[] { -12 * 60, -8 * 60, -5 * 60, -4 * 60, 0, 60, 90, 3 * 60, 5 * 60 + 30, 8 * 60, 9 * 60 + 30, 12 * 60, 14 * 60 };
                var offsetMinutes = offsetMinutesChoices[rng.Next(offsetMinutesChoices.Length)];
                offset = TimeSpan.FromMinutes(offsetMinutes);
            }
            var local = expected + offset;
            var dto = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            var text = dto.ToString(format.NetFormat, CultureInfo.InvariantCulture);

            // ~40% of the time, strip the colon out of the offset (e.g. "-04:00" -> "-0400")
            // to exercise TimestampDetector.NormalizeOffset, the fix for the original bug
            // report ("-0400" wasn't being captured/parsed).
            if (rng.NextDouble() < 0.4)
            {
                text = RemoveOffsetColon(text);
            }

            return new RenderedTimestamp(text, expected);
        }

        // Format has no year in it (classic syslog) - the caller (SampleLogGenerator) is
        // responsible for having already forced baseUtc's year to the current year, matching
        // what TimestampDetector itself fills in for a yearless format.
        var renderedText = expected.ToString(format.NetFormat, CultureInfo.InvariantCulture);
        return new RenderedTimestamp(renderedText, expected);
    }

    /// <summary>Length of the longest contiguous run of 'f' characters anywhere in a custom
    /// format string, e.g. "HH:mm:ss.fffffff" -> 7, "HH:mm:ss.fffffff zzz" -> 7 (NOT 0 - the
    /// run isn't at the very end once there's a trailing " zzz" or " tt", which is why this
    /// scans the whole string for the longest run instead of just counting back from the
    /// end). "HH:mm:ss" -> 0. Every fractional-seconds format in the catalog uses a single
    /// contiguous run of 'f', so this is sufficient (no need to duplicate TimestampDetector's
    /// more general CountTrailingFractionalSpecifier).</summary>
    private static int CountTrailingFractionalDigits(string format)
    {
        int best = 0, current = 0;
        foreach (var c in format)
        {
            if (c == 'f')
            {
                current++;
                if (current > best) best = current;
            }
            else
            {
                current = 0;
            }
        }
        return best;
    }

    /// <summary>Zeroes out everything below <paramref name="fracDigits"/> decimal digits of a
    /// second (a .NET DateTime tick is 100ns = 1e-7s, so 7 is full native precision).</summary>
    private static DateTime TruncateToFractionalDigits(DateTime dt, int fracDigits)
    {
        if (fracDigits <= 0)
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, DateTimeKind.Utc);

        long ticksPerUnit = (long)Math.Pow(10, 7 - fracDigits);
        long truncatedTicks = (dt.Ticks / ticksPerUnit) * ticksPerUnit;
        return new DateTime(truncatedTicks, DateTimeKind.Utc);
    }

    /// <summary>"...2026-08-08 14:23:05 -04:00" -> "...2026-08-08 14:23:05 -0400". Only touches
    /// a trailing "+hh:mm"/"-hh:mm" - safe to call on any zzz-formatted string.</summary>
    private static string RemoveOffsetColon(string text)
    {
        if (text.Length < 6) return text;
        var tail = text[^6..];
        if ((tail[0] == '+' || tail[0] == '-') && tail[3] == ':')
        {
            return text[..^6] + tail[..3] + tail[4..];
        }
        return text;
    }
}
