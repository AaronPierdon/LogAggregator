using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Determines a file's delimiter shape (CSV / TabDelimited / FlatText) directly from its
/// content, so the user is never asked to pick it manually. Only the leading portion of each
/// line is inspected for commas, because free-text message fields (e.g. Kepware's Event
/// column, which routinely contains "Vendor ID = 1, Product type = 14, ...") can contain
/// plenty of commas without the file actually being comma-delimited - what matters is whether
/// a delimiter shows up consistently near the start of the line, where structured columns live.
/// </summary>
public static class FileTypeDetector
{
    private const int LeadingWindow = 80;
    private const double ConsistencyThreshold = 0.7;

    public static FileType DetectFromFile(string path)
    {
        return DetectFromLines(ReadSampleLines(path, 25));
    }

    public static FileType DetectFromLines(IReadOnlyList<string> lines)
    {
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (nonEmpty.Count == 0) return FileType.FlatText;

        double tabRate = nonEmpty.Count(l => l.Contains('\t')) / (double)nonEmpty.Count;
        if (tabRate >= ConsistencyThreshold) return FileType.TabDelimited;

        double csvRate = nonEmpty.Count(l => CountLeadingUnquotedCommas(l) >= 1) / (double)nonEmpty.Count;
        if (csvRate >= ConsistencyThreshold) return FileType.CSV;

        return FileType.FlatText;
    }

    private static int CountLeadingUnquotedCommas(string line)
    {
        int end = Math.Min(line.Length, LeadingWindow);
        int count = 0;
        bool inQuotes = false;
        for (int i = 0; i < end; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) count++;
        }
        return count;
    }

    public static List<string> ReadSampleLines(string path, int maxLines)
    {
        var result = new List<string>();
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            while (result.Count < maxLines && (line = reader.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) result.Add(line);
            }
        }
        catch
        {
            // Best effort - an unreadable file just yields an empty sample, caller handles that.
        }
        return result;
    }
}
