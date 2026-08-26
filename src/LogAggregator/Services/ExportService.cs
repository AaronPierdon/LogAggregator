using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LogAggregator.Services;

/// <summary>
/// Writes the current (post-filter) view to a pipe-delimited flat text file, reading straight
/// from SQLite via an unbuffered query. Runs on a background thread and never materializes the
/// full result set in memory - correct even for tens of millions of exported rows.
/// </summary>
public static class ExportService
{
    public static Task ExportAsync(
        string path, LogDatabase database,
        IReadOnlyCollection<string> activeSourceIds,
        IReadOnlyList<string> orTerms, IReadOnlyList<string> andTerms, IReadOnlyList<string> exclusionTerms,
        SortColumn sortColumn, bool descending,
        IProgress<long>? progress = null)
    {
        return Task.Run(() =>
        {
            using var writer = new StreamWriter(path, append: false);
            writer.WriteLine("Universal Timestamp | Original Timestamp | Source | Log Message");

            long count = 0;
            foreach (var block in database.QueryAllMatchingUnbuffered(activeSourceIds, orTerms, andTerms, exclusionTerms, sortColumn, descending))
            {
                var tsText = block.TimestampParseFailed
                    ? "(unparsed)"
                    : block.UniversalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

                var lines = block.SplitLines();
                var firstLine = lines.Length > 0 ? lines[0] : block.FullText;
                writer.WriteLine($"{tsText} | {block.OriginalTimestamp} | {block.SourceName} | {firstLine}");

                for (int i = 1; i < lines.Length; i++)
                    writer.WriteLine($" | | |     {lines[i]}");

                writer.WriteLine();

                count++;
                if (count % 5000 == 0) progress?.Report(count);
            }

            progress?.Report(count);
        });
    }
}
