using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;

namespace LogAggregator.ViewModels;

/// <summary>
/// Backs the Parse Warnings window - "let the user see the files that didn't match, in the
/// worst case or if file corruption" (the error-chip ask). Reads from MainViewModel.Warnings,
/// which already accumulates every IngestionWarning raised this session (see
/// MainViewModel.OnSyncCompleted); this view model just resolves the Source/LogType names those
/// warnings only store as IDs, and optionally narrows to one chip.
///
/// Deliberately a snapshot, not a live view: Warnings is a live ObservableCollection that a
/// background sync can still be appending to, and re-sorting/re-filtering rows out from under
/// someone mid-read would be more confusing than a Refresh button that re-snapshots on demand.
/// </summary>
public class WarningsViewModel : ObservableObject
{
    private readonly IReadOnlyList<IngestionWarning> _allWarnings;
    private readonly Dictionary<string, string> _sourceNames;
    private readonly Dictionary<string, LogType> _logTypesById;

    private readonly string? _filterSourceId;
    private readonly string? _filterLogTypeId;

    public ObservableCollection<WarningRowViewModel> Rows { get; } = new();

    public string Title { get; }

    /// <summary>True when this window was opened from a specific chip's badge rather than the
    /// status bar counter - drives whether a "showing N of M total" hint is worth showing.</summary>
    public bool IsFiltered => _filterSourceId is not null || _filterLogTypeId is not null;

    public int TotalWarningCount => _allWarnings.Count;

    public ICommand RefreshCommand { get; }

    public WarningsViewModel(
        IEnumerable<IngestionWarning> allWarnings,
        IEnumerable<LogSource> sources,
        IEnumerable<LogType> logTypes,
        SourceLogType? filterBinding,
        LogType? filterLogType)
    {
        _allWarnings = allWarnings.ToList();
        _sourceNames = sources.ToDictionary(s => s.Id, s => s.Name);
        _logTypesById = logTypes.ToDictionary(lt => lt.Id, lt => lt);

        // A SourceLogType instance is only ever a member of exactly one LogSource.LogTypes list
        // (it's never copied), so finding the owning source by reference is reliable - the
        // binding itself carries no SourceId of its own.
        _filterSourceId = filterBinding is null
            ? null
            : sources.FirstOrDefault(s => s.LogTypes.Contains(filterBinding))?.Id;
        _filterLogTypeId = filterLogType?.Id;

        Title = (_filterSourceId, filterLogType) switch
        {
            (not null, not null) => $"Parse Warnings - {ResolveSourceName(_filterSourceId)} / {filterLogType.Name}",
            _ => "Parse Warnings - All Sources"
        };

        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Rows.Clear();

        IEnumerable<IngestionWarning> filtered = _allWarnings.Where(w => w.RequiresUserAction);
        if (_filterSourceId is not null) filtered = filtered.Where(w => w.SourceId == _filterSourceId);
        if (_filterLogTypeId is not null) filtered = filtered.Where(w => w.LogTypeId == _filterLogTypeId);

        foreach (var warning in filtered.OrderByDescending(w => w.OccurredAt))
            Rows.Add(BuildRow(warning));

        OnPropertyChanged(nameof(Rows));
    }

    private WarningRowViewModel BuildRow(IngestionWarning warning)
    {
        _logTypesById.TryGetValue(warning.LogTypeId, out var logType);

        return new WarningRowViewModel
        {
            SourceName = ResolveSourceName(warning.SourceId),
            LogTypeName = logType?.Name ?? "(deleted LogType)",
            LogTypeColorHex = logType?.ColorHex ?? "#808080",
            FileName = string.IsNullOrEmpty(warning.FilePath) ? "(unknown file)" : Path.GetFileName(warning.FilePath),
            FilePath = warning.FilePath,
            LineNumber = warning.LineNumber,
            HasLineNumber = warning.LineNumber > 0,
            Reason = warning.Reason,
            RawText = warning.RawText,
            HasRawText = !string.IsNullOrEmpty(warning.RawText),
            OccurredAtText = warning.OccurredAt.ToString("g")
        };
    }

    private string ResolveSourceName(string? sourceId) =>
        sourceId is not null && _sourceNames.TryGetValue(sourceId, out var name) ? name : "(deleted source)";
}

/// <summary>One row in the Parse Warnings list - a display-ready flattening of one
/// IngestionWarning, with Source/LogType names resolved from their IDs.</summary>
public class WarningRowViewModel
{
    public string SourceName { get; set; } = string.Empty;
    public string LogTypeName { get; set; } = string.Empty;
    public string LogTypeColorHex { get; set; } = "#808080";
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public bool HasLineNumber { get; set; }

    /// <summary>"file.txt" or "file.txt - line 42" - built here rather than in XAML because
    /// WPF's inline text elements (Run/Span) derive from FrameworkContentElement, not
    /// FrameworkElement, so they have no Visibility property to toggle the "- line N" part
    /// on/off for whole-file warnings (which carry no line number).</summary>
    public string LocationText => HasLineNumber ? $"{FileName} - line {LineNumber}" : FileName;

    public string Reason { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public bool HasRawText { get; set; }
    public string OccurredAtText { get; set; } = string.Empty;
}
