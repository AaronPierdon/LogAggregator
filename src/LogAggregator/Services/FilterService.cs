using System;
using System.Collections.Generic;
using System.Linq;

namespace LogAggregator.Services;

/// <summary>
/// Parses the three filter boxes' comma-separated text into term lists. The actual
/// Block_Passes = (OR_match || AND_match) &amp;&amp; !Exclusion_match evaluation now happens as SQL
/// inside LogDatabase.BuildFilterSql, rather than in memory here - with tens of millions of
/// rows, filtering has to happen at the database, not by iterating a C# collection.
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
}
