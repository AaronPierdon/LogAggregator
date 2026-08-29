using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using LogAggregator.Common;

namespace LogAggregator.Models;

/// <summary>
/// One source card in the left panel. Persisted to sources.config.json. A Source is now just
/// identity (name, its own display color, active flag) plus a group of <see cref="LogTypes"/>
/// bindings - e.g. a "PLANT-PC-04" source might have a Kepware LogType binding and an
/// Event Viewer LogType binding, each with its own files and parse state. File format and
/// timestamp parsing live on the LogType, not here (see LogType.cs).
/// </summary>
public class LogSource : ObservableObject
{
    private string _name = string.Empty;
    private string _displayColor = "#4C9BFF";
    private bool _isActive = true;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>Hex color string used when the "Log line coloring" setting is set to Source
    /// color, and as the fallback wedge color in the collapsed rail for a source with no
    /// LogTypes bound yet.</summary>
    public string DisplayColor
    {
        get => _displayColor;
        set => SetProperty(ref _displayColor, value);
    }

    /// <summary>Whether this source's rows appear in the main view (card checkbox).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>One entry per LogType bound to this source. Order is preserved so chips don't
    /// reshuffle every time the app restarts.</summary>
    public List<SourceLogType> LogTypes { get; set; } = new();

    [JsonIgnore]
    public bool HasFiles => LogTypes.Any(b => b.HasFiles);

    [JsonIgnore]
    public bool IsSyncing => LogTypes.Any(b => b.IsSyncing);

    [JsonIgnore]
    public bool HasAnyError => LogTypes.Any(b => b.HasError);

    [JsonIgnore]
    public int ParsedBlockCount => LogTypes.Sum(b => b.ParsedBlockCount);

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

    /// <summary>Finds (or null if not bound) this source's binding for a given LogType.</summary>
    public SourceLogType? FindBinding(string logTypeId) =>
        LogTypes.FirstOrDefault(b => b.LogTypeId == logTypeId);

    public void RefreshComputedState()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(HasAnyError));
        OnPropertyChanged(nameof(ParsedBlockCount));
        OnPropertyChanged(nameof(CardState));
    }
}

public enum SourceCardState
{
    NoFiles,
    Ready,
    Syncing
}
