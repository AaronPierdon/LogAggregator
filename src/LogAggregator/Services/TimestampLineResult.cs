using System;
namespace LogAggregator.Services;

/// <summary>
/// Outcome of evaluating a single physical line against a FlatText timestamp profile.
/// Distinguishes "no leading timestamp at all" (continuation line) from "looked like a
/// timestamp but failed to parse" (new block, sentinel timestamp, warning logged) - the two
/// must be handled differently so unrelated content never gets silently merged together.
/// </summary>
public readonly struct TimestampLineResult
{
    public bool Matched { get; private init; }
    public bool Parsed { get; private init; }
    public DateTime Utc { get; private init; }
    public string RawText { get; private init; }

    public static TimestampLineResult NoMatch() => new() { Matched = false, RawText = string.Empty };

    public static TimestampLineResult Ok(DateTime utc, string raw) =>
        new() { Matched = true, Parsed = true, Utc = utc, RawText = raw };

    public static TimestampLineResult Unparseable(string raw) =>
        new() { Matched = true, Parsed = false, Utc = DateTime.MinValue, RawText = raw };
}
