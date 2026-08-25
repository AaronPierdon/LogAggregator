using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LogAggregator.Services;

/// <summary>
/// Delimited-text helpers. CSV needs real RFC4180 parsing because several real-world exports
/// (e.g. Windows Event Viewer CSV) embed commas and even newlines inside quoted fields - a
/// naive line.Split(',') would silently corrupt those records. Tab-delimited files in practice
/// don't quote fields, so a straightforward split is sufficient there.
/// </summary>
public static class DelimitedLineParser
{
    /// <summary>
    /// Reads one full CSV record from <paramref name="reader"/>, honoring quotes - a record may
    /// span multiple physical lines if a quoted field contains embedded newlines. Returns null
    /// at end of stream. The reader's position is advanced past the record.
    /// </summary>
    public static string[]? ReadCsvRecord(TextReader reader, char delimiter = ',')
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool anyContent = false;
        int ch;

        while ((ch = reader.Read()) != -1)
        {
            anyContent = true;
            char c = (char)ch;

            if (inQuotes)
            {
                if (c == '"')
                {
                    int next = reader.Peek();
                    if (next == '"')
                    {
                        reader.Read();
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c == '\r')
            {
                // Peek for \n to consume the pair as one line ending.
                if (reader.Peek() == '\n') reader.Read();
                fields.Add(current.ToString());
                return fields.ToArray();
            }
            else if (c == '\n')
            {
                fields.Add(current.ToString());
                return fields.ToArray();
            }
            else
            {
                current.Append(c);
            }
        }

        if (!anyContent) return null;

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    /// <summary>Splits a single line by a delimiter, honoring simple double-quote wrapping
    /// (no embedded-newline support - intended for a single already-isolated line, e.g. a
    /// pasted sample line in the wizard's test panel).</summary>
    public static string[] SplitLine(string line, char delimiter = ',')
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (var c in line)
        {
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                else current.Append(c);
                continue;
            }

            if (c == '"') inQuotes = true;
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    /// <summary>Simple tab split - no quote handling, matches how tab-delimited exports behave
    /// in practice.</summary>
    public static string[] SplitTab(string line) => line.Split('\t');
}
