using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using LogAggregator.Common;

namespace LogAggregator.Models;

/// <summary>
/// One LogType "loaded into" one Source card - e.g. the Kepware LogType's files on the
/// "PLANT-PC-04" source. A Source is just a named group of these (see <see cref="LogSource"/>).
/// Files, per-binding parse state, and sync status all live here now instead of on LogSource
/// directly, since a card can have several of these (one per bound LogType) syncing
/// independently of one another.
/// </summary>
public class SourceLogType : ObservableObject
{
    private int _parsedBlockCount;
    private bool _isSyncing;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private int _errorCount;

    /// <summary>References a <see cref="LogType"/> in AppConfig.LogTypes by Id, rather than
    /// embedding a copy - so editing a LogType's color/icon/timestamp pattern in the LogTypes
    /// window is instantly reflected everywhere it's bound, with no per-source copies to sync.</summary>
    public string LogTypeId { get; set; } = string.Empty;

    public List<string> FilePaths { get; set; } = new();

    public int ParsedBlockCount
    {
        get => _parsedBlockCount;
        set => SetProperty(ref _parsedBlockCount, value);
    }

    [JsonIgnore]
    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    /// <summary>True when the last sync produced a warning that needs the user's attention
    /// (e.g. a timestamp pattern that couldn't be determined) - drives the small badge shown on
    /// this binding's chip. Not persisted; recomputed from ingestion warnings each sync.</summary>
    [JsonIgnore]
    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    [JsonIgnore]
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>How many actionable warnings the last sync produced for this binding - not
    /// persisted, recomputed from ingestion warnings each sync (see SourceCardViewModel.
    /// SyncBindingAsync). Drives the chip badge's count ("3 issues") instead of it only ever
    /// being able to show the single first warning's message.</summary>
    [JsonIgnore]
    public int ErrorCount
    {
        get => _errorCount;
        set => SetProperty(ref _errorCount, value);
    }

    [JsonIgnore]
    public CancellationTokenSource? CurrentCts { get; set; }

    [JsonIgnore]
    public bool HasFiles => FilePaths.Count > 0;
}
