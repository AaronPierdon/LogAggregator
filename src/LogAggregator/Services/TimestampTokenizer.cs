using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LogAggregator.Services;

/// <summary>Soft, non-binding guess about what a token might be - drives which buttons the
/// TimestampPickerControl pre-highlights, but every token stays individually selectable
/// regardless of its hint (the user makes the final call, not the tokenizer).</summary>
public enum TimestampTokenHint
{
    None,
    LikelyDatePart,
    LikelyTimePart
}

/// <summary>One contiguous run of letters or digits in a sample line - the atomic unit the
/// interactive timestamp region picker shows as a toggle button. Punctuation/whitespace between
/// tokens is not itself selectable; it's carried along implicitly via StartIndex/Length so the
/// picker can reconstruct the exact original separators when it builds a regex.</summary>
public class TimestampToken
{
    public string Text { get; set; } = string.Empty;
    public int StartIndex { get; set; }
    public int Length { get; set; }
    public TimestampTokenHint Hint { get; set; } = TimestampTokenHint.None;

    /// <summary>True if this run is entirely digits (as opposed to letters, e.g. a month name).</summary>
    public bool IsNumeric { get; set; }

    /// <summary>True only for the single synthetic "whole detected timestamp" token
    /// TimestampPickerViewModel adds when TimestampDetector.FindLikelyTimestampSpan finds a
    /// recognizable shape - unlike every other token (one atomic digit/letter run), this one
    /// spans punctuation and multiple runs (e.g. "2026-08-08 14:23:05 -0400" as a single unit),
    /// so BuildRegexPattern has to build its interior specially rather than treating it as one
    /// \d+/[A-Za-z]+ character class.</summary>
    public bool IsComposite { get; set; }
}

/// <summary>
/// Breaks a sample line into selectable date/time candidate chunks for the interactive
/// timestamp region picker (used when auto-detection is Ambiguous/Failed - see
/// TimestampPickerViewModel). Every digit-run and letter-run in the line becomes one token;
/// nothing is ever free-text/drag-selected, matching the "each chunk is a button" spec.
/// </summary>
public static class TimestampTokenizer
{
    private static readonly Regex TokenRegex = new(@"\d+|[A-Za-z]+", RegexOptions.Compiled);

    private static readonly HashSet<string> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jan", "January", "Feb", "February", "Mar", "March", "Apr", "April", "May",
        "Jun", "June", "Jul", "July", "Aug", "August", "Sep", "Sept", "September",
        "Oct", "October", "Nov", "November", "Dec", "December"
    };

    public static List<TimestampToken> Tokenize(string line)
    {
        var tokens = new List<TimestampToken>();
        if (string.IsNullOrEmpty(line)) return tokens;

        foreach (Match m in TokenRegex.Matches(line))
        {
            bool isNumeric = char.IsDigit(m.Value[0]);
            char prevChar = m.Index > 0 ? line[m.Index - 1] : '\0';
            int afterIndex = m.Index + m.Length;
            char nextChar = afterIndex < line.Length ? line[afterIndex] : '\0';

            var hint = ClassifyHint(m.Value, isNumeric, prevChar, nextChar);

            tokens.Add(new TimestampToken
            {
                Text = m.Value,
                StartIndex = m.Index,
                Length = m.Length,
                IsNumeric = isNumeric,
                Hint = hint
            });
        }

        return tokens;
    }

    private static TimestampTokenHint ClassifyHint(string text, bool isNumeric, char prevChar, char nextChar)
    {
        if (isNumeric)
        {
            // A 4-digit run in a plausible year range is very likely a year.
            if (text.Length == 4 && int.TryParse(text, out var asInt) && asInt is >= 1900 and <= 2100)
                return TimestampTokenHint.LikelyDatePart;

            // Adjacent to a colon almost always means hour/minute/second.
            if (prevChar == ':' || nextChar == ':')
                return TimestampTokenHint.LikelyTimePart;

            // Adjacent to a date-style separator suggests day/month.
            if (prevChar is '-' or '/' or '.' || nextChar is '-' or '/' or '.')
                return TimestampTokenHint.LikelyDatePart;

            return TimestampTokenHint.None;
        }

        if (MonthNames.Contains(text)) return TimestampTokenHint.LikelyDatePart;
        if (string.Equals(text, "AM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "PM", StringComparison.OrdinalIgnoreCase))
            return TimestampTokenHint.LikelyTimePart;

        return TimestampTokenHint.None;
    }
}
