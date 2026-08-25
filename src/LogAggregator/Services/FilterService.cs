using System;
using System.Collections.Generic;
using System.Linq;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Evaluates the three-box filter (OR / AND / Exclusion) against a LogBlock's pre-joined
/// FullText. Block_Passes = (OR_match || AND_match) && !Exclusion_match, where an exclusion
/// term is ignored (doesn't veto) if that same term also appears in the OR or AND box.
/// </summary>
public static class FilterService
{
    public static List<string> ParseTerms(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new List<string>();
        return input.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static bool BlockPasses(LogBlock block, IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms)
    {
        var text = block.FullText;

        // OR: at least one OR term found. Vacuously false with zero terms (relies on AND instead).
        bool orMatch = orTerms.Count > 0 && orTerms.Any(t => Contains(text, t));

        // AND: every AND term found (not necessarily on the same line). Vacuously true with
        // zero terms, so "no filters at all" correctly shows everything.
        bool andMatch = andTerms.Count == 0 || andTerms.All(t => Contains(text, t));

        // Exclusion veto: a found exclusion term hides the block, unless that exact term also
        // appears in the OR or AND box (override rule).
        bool exclusionVeto = exclusionTerms.Any(t =>
            Contains(text, t) &&
            !orTerms.Contains(t, StringComparer.OrdinalIgnoreCase) &&
            !andTerms.Contains(t, StringComparer.OrdinalIgnoreCase));

        return (orMatch || andMatch) && !exclusionVeto;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
