using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;
using LogAggregator.Common;

namespace LogAggregator.Models;

/// <summary>
/// One data source card in the left panel. Persisted to sources.config.json.
/// </summary>
public class LogSource : ObservableObject
{
    private string _name = string.Empty;
    private FileType _type = FileType.FlatText;
    private string _displayColor = "#4C9BFF";
    private bool _isActive = true;
    private int _parsedBlockCount;
    private bool _isSyncing;
    private bool _isLocked;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public FileType Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public TimestampProfile TimestampProfile { get; set; } = new();

    /// <summary>Hex color string, e.g. "#4C9BFF", used for row tinting and the card swatch.</summary>
    public string DisplayColor
    {
        get => _displayColor;
        set => SetProperty(ref _displayColor, value);
    }

    public List<string> FilePaths { get; set; } = new();

    /// <summary>Whether this source's rows appear in the main view (card checkbox).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public int ParsedBlockCount
    {
        get => _parsedBlockCount;
        set => SetProperty(ref _parsedBlockCount, value);
    }

    /// <summary>True while a sync/import task is running for this source. Not persisted.</summary>
    [JsonIgnore]
    public bool IsSyncing
    {
        get => _isSyncing;
        set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                OnPropertyChanged(nameof(CardState));
                OnPropertyChanged(nameof(IsLocked));
            }
        }
    }

    /// <summary>Wizard is locked while syncing (per spec) or explicitly locked by the caller.</summary>
    [JsonIgnore]
    public bool IsLocked
    {
        get => _isLocked || _isSyncing;
        set => SetProperty(ref _isLocked, value);
    }

    [JsonIgnore]
    public bool HasFiles => FilePaths.Count > 0;

    /// <summary>Card visual state: NoFiles (white/grey), Ready (green accent), Syncing.</summary>
    [JsonIgnore]
    public SourceCardState CardState
    {
        get
        {
            if (IsSyncing) return SourceCardState.Syncing;
            return HasFiles ? SourceCardState.Ready : SourceCardState.NoFiles;
        }
    }

    /// <summary>Live cancellation token source for an in-flight sync. Not persisted, not bound.</summary>
    [JsonIgnore]
    public CancellationTokenSource? CurrentCts { get; set; }

    public void RefreshComputedState()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CardState));
    }
}

public enum SourceCardState
{
    NoFiles,
    Ready,
    Syncing
}
