using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LogAggregator.Services;

/// <summary>
/// Shared logic for turning a raw drag-and-drop/browse file list into a flat list of real log
/// files: a dropped folder is recursed into (every file underneath it is included - the user
/// drops the containing folder rather than hand-picking each file inside it), and any .zip
/// (whether dropped directly or found inside a dropped folder) is extracted to a temp directory
/// with its contents added instead. Used by the LogType editor's file-picker step, Source card
/// drops (both chip-level and card-level), and the main window's quick-add drop zone.
/// </summary>
public static class FileDropHelper
{
    public static List<string> ExpandDroppedPaths(IEnumerable<string> paths, Action<string>? onError = null)
    {
        var result = new List<string>();

        foreach (var path in paths)
        {
            ExpandOnePath(path, result, onError);
        }

        return result;
    }

    private static void ExpandOnePath(string path, List<string> result, Action<string>? onError)
    {
        if (Directory.Exists(path))
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Couldn't read folder {Path.GetFileName(path)}: {ex.Message}");
                return;
            }

            foreach (var child in children)
                ExpandOnePath(child, result, onError);

            return;
        }

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
            return;
        }

        result.Add(path);
    }
}
