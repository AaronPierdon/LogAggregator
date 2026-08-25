using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LogAggregator.Services;

/// <summary>
/// Shared logic for turning a raw drag-and-drop/browse file list into a flat list of real log
/// files: folders are skipped (not recursed into - drop individual files), and any .zip is
/// extracted to a temp directory with its contents added instead. Used by both the wizard's
/// file-picker step and the main window's quick-add drop zone.
/// </summary>
public static class FileDropHelper
{
    public static List<string> ExpandDroppedPaths(IEnumerable<string> paths, Action<string>? onError = null)
    {
        var result = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path)) continue;

            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var extractDir = Path.Combine(Path.GetTempPath(), "LogAggregator_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(extractDir);
                    ZipFile.ExtractToDirectory(path, extractDir);
                    result.AddRange(Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Couldn't extract {Path.GetFileName(path)}: {ex.Message}");
                }
                continue;
            }

            result.Add(path);
        }

        return result;
    }
}
