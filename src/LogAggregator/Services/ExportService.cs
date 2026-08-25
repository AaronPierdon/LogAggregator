using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using LogAggregator.Models;

namespace LogAggregator.Services;

/// <summary>
/// Writes the current (post-filter, if active) view to a pipe-delimited flat text file.
/// Multi-line blocks get one row per line: the first row carries all columns, continuation
/// rows leave timestamp/source blank and indent the message. A blank line separates blocks.
/// </summary>
public static class ExportService
{
    public static async Task ExportAsync(string path, IEnumerable<LogBlock> blocks)
    {
        using var writer = new StreamWriter(path, append: false);

        await writer.WriteLineAsync("Universal Timestamp | Original Timestamp | Source | Log Message").ConfigureAwait(false);

        foreach (var block in blocks)
        {
            var tsText = block.TimestampParseFailed
                ? "(unparsed)"
                : block.UniversalTimestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

            var firstLine = block.Lines.Count > 0 ? block.Lines[0] : block.FullText;
            await writer.WriteLineAsync($"{tsText} | {block.OriginalTimestamp} | {block.SourceName} | {firstLine}").ConfigureAwait(false);

            for (int i = 1; i < block.Lines.Count; i++)
            {
                await writer.WriteLineAsync($" | | |     {block.Lines[i]}").ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
        }
    }
}
