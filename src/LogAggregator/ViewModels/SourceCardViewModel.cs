using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;

namespace LogAggregator.ViewModels;

/// <summary>
/// View model for one card in the left panel. A card is a Source plus a row of LogType chips
/// (see LogTypeChipViewModel/SourceLogType); each chip owns its own ingestion lifecycle
/// (drop-to-sync, cancel), independent of the card's other chips. Actual row data lives in
/// LogDatabase, not here - this class only carries counts, status, and chip-level UI state.
/// </summary>
public class SourceCardViewModel : ObservableObject
{
    /// <summary>Beyond this many bound LogTypes, the card shows a "+N more" toggle instead of
    /// growing indefinitely (per the spec: "if there are more than 3 or 4 log types... show a
    /// button to expand").</summary>
    private const int MaxVisibleChips = 4;

    private readonly LogDatabase _database;
    private readonly IngestionService _ingestionService;
    private readonly ObservableCollection<LogType> _allLogTypes;

    private bool _isEditingName;
    private string _editingName = string.Empty;
    private bool _isLogTypePickerOpen;
    private bool _isOverflowExpanded;
    private List<string> _pendingDropPaths = new();

    public LogSource Source { get; }

    public ObservableCollection<LogTypeChipViewModel> Chips { get; } = new();

    /// <summary>Read-only convenience list for the LogType-picker popup shown on a card-level
    /// drop (as opposed to a drop directly on one chip).</summary>
    public ObservableCollection<LogType> AllLogTypes => _allLogTypes;

    /// <summary>Raised when a binding's sync completes successfully - MainViewModel refreshes
    /// the currently-displayed page/count, since new rows may now be visible.</summary>
    public event Action<SourceCardViewModel, SourceLogType, SourceIngestionResult>? SyncCompleted;

    /// <summary>Raised when the user cancels an in-flight sync, or it errors out, for one
    /// binding. Any partial rows already written for that binding are deleted first.</summary>
    public event Action<SourceCardViewModel, SourceLogType>? SyncCancelled;

    /// <summary>Raised whenever anything about this card changes in a way that should persist
    /// to sources.config.json.</summary>
    public event Action? ConfigChanged;

    /// <summary>Raised to ask the owner to remove this card entirely.</summary>
    public event Action<SourceCardViewModel>? RemoveRequested;

    /// <summary>Raised to show a transient toast message (drop errors, quick status).</summary>
    public event Action<string>? ToastRequested;

    /// <summary>Raised when the user picks "+ New Log Type..." from the card-level drop popup -
    /// the owner opens the LogType editor pre-seeded with the dropped sample file(s), then calls
    /// <see cref="CompleteNewLogTypeDrop"/> with the result.</summary>
    public event Action<SourceCardViewModel, List<string>>? NewLogTypeRequestedForDrop;

    /// <summary>Raised from a chip's right-click menu ("Log Type Settings..." or "Set Timestamp
    /// Pattern...") - the owner opens the LogType editor for this LogType, pre-seeded with this
    /// binding's real files. The bool is true for "Set Timestamp Pattern...", which additionally
    /// forces the editor to land on the timestamp step with the manual picker open.</summary>
    public event Action<SourceLogType, LogType, bool>? LogTypeSettingsRequested;

    /// <summary>Raised when the user clicks a chip's error badge - "let the user see the files
    /// that didn't match" for this specific binding. Bubbled up through MainViewModel to
    /// MainWindow, which opens the Parse Warnings window filtered to just this LogType.</summary>
    public event Action<SourceLogType, LogType>? ViewWarningsRequested;

    public SourceCardViewModel(LogSource source, LogDatabase database, ObservableCollection<LogType> allLogTypes)
    {
        Source = source;
        _database = database;
        _allLogTypes = allLogTypes;
        _ingestionService = new IngestionService(database);

        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogSource.IsActive))
                ConfigChanged?.Invoke();
        };

        RebuildChips();

        BeginRenameCommand = new RelayCommand(BeginRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsEditingName = false);
        RemoveSourceCommand = new RelayCommand(RemoveSource);
        AddFilesCommand = new RelayCommand(AddFilesViaDialog);
        PickExistingLogTypeCommand = new RelayCommand(param =>
        {
            if (param is LogType logType) PickLogTypeForPendingDrop(logType);
        });
        RequestNewLogTypeCommand = new RelayCommand(() =>
        {
            IsLogTypePickerOpen = false;
            NewLogTypeRequestedForDrop?.Invoke(this, _pendingDropPaths);
        });
        ToggleOverflowCommand = new RelayCommand(() => IsOverflowExpanded = !IsOverflowExpanded);
        CardDropCommand = new RelayCommand(param =>
        {
            if (param is string[] paths) HandleDropOnCard(paths);
        });

        SetColorCommand = new RelayCommand(param =>
        {
            if (param is string hex)
            {
                Source.DisplayColor = hex;
                OnPropertyChanged(nameof(WedgeColors)); // only visually matters when Chips is empty (the fallback case)
                ConfigChanged?.Invoke();
                // Fire-and-forget: for a source with millions of already-ingested rows this
                // UPDATE can take a moment, so it shouldn't block the UI thread.
                _ = Task.Run(() => _database.UpdateSourceColor(Source.Id, hex));
            }
        });
    }

    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand RemoveSourceCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand PickExistingLogTypeCommand { get; }
    public ICommand RequestNewLogTypeCommand { get; }
    public ICommand ToggleOverflowCommand { get; }
    public ICommand SetColorCommand { get; }
    public ICommand CardDropCommand { get; }

    public static IReadOnlyList<string> PresetColors => ColorPresets.Colors;

    // ===================================================================
    // Inline name editing
    // ===================================================================

    public bool IsEditingName
    {
        get => _isEditingName;
        set => SetProperty(ref _isEditingName, value);
    }

    public string EditingName
    {
        get => _editingName;
        set => SetProperty(ref _editingName, value);
    }

    private void BeginRename()
    {
        EditingName = Source.Name;
        IsEditingName = true;
    }

    private void CommitRename()
    {
        var trimmed = EditingName?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && trimmed != Source.Name)
        {
            Source.Name = trimmed;
            ConfigChanged?.Invoke();
            _ = Task.Run(() => _database.UpdateSourceName(Source.Id, trimmed));
        }
        IsEditingName = false;
    }

    private void RemoveSource()
    {
        var confirm = MessageBox.Show(
            $"Remove source \"{Source.Name}\" and all of its loaded rows? This cannot be undone.",
            "Remove Source", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
            RemoveRequested?.Invoke(this);
    }

    /// <summary>Right-click "Remove From This Source" on a chip - removes just this LogType's
    /// binding (and its already-loaded rows) from this card. The LogType definition itself, and
    /// its bindings on any other card, are untouched. Mirrors the per-card cascade
    /// LogTypesViewModel.Delete does when a LogType is deleted globally, scoped to one card.</summary>
    private void RemoveChip(SourceLogType binding, LogType logType)
    {
        var confirm = MessageBox.Show(
            $"Remove \"{logType.Name}\" from \"{Source.Name}\"? Its loaded rows for this source will be deleted. This cannot be undone.",
            "Remove Log Type From Source", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        binding.CurrentCts?.Cancel();
        _database.DeleteBlocksForBinding(Source.Id, binding.LogTypeId);
        Source.LogTypes.Remove(binding);
        RebuildChips();
        ConfigChanged?.Invoke();
    }

    // ===================================================================
    // Chips
    // ===================================================================

    /// <summary>Rebuilds the Chips collection from Source.LogTypes + the shared LogTypes list.
    /// Called on construction, after a binding is added/removed, and by the LogTypes window
    /// after a LogType's name/color/icon changes (its properties aren't independently
    /// observable, so a full rebuild is the simplest reliable refresh for a handful of chips).</summary>
    public void RefreshChips() => RebuildChips();

    private void RebuildChips()
    {
        Chips.Clear();
        foreach (var binding in Source.LogTypes)
        {
            var logType = _allLogTypes.FirstOrDefault(lt => lt.Id == binding.LogTypeId);
            if (logType is null) continue; // defensive: LogType was deleted without cascading here

            var dropCommand = new RelayCommand(param =>
            {
                if (param is string[] paths) _ = HandleDropOnChipAsync(binding, logType, paths);
            });
            var cancelCommand = new RelayCommand(() => binding.CurrentCts?.Cancel());
            var openSettingsCommand = new RelayCommand(() => LogTypeSettingsRequested?.Invoke(binding, logType, false));
            var setTimestampPatternCommand = new RelayCommand(() => LogTypeSettingsRequested?.Invoke(binding, logType, true));
            var removeFromSourceCommand = new RelayCommand(() => RemoveChip(binding, logType));
            var viewWarningsCommand = new RelayCommand(() => ViewWarningsRequested?.Invoke(binding, logType));

            Chips.Add(new LogTypeChipViewModel(binding, logType, dropCommand, cancelCommand,
                openSettingsCommand, setTimestampPatternCommand, removeFromSourceCommand, viewWarningsCommand));
        }

        OnPropertyChanged(nameof(VisibleChips));
        OnPropertyChanged(nameof(OverflowChips));
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(WedgeColors));
        Source.RefreshComputedState();
    }

    public IEnumerable<LogTypeChipViewModel> VisibleChips =>
        IsOverflowExpanded ? Chips : Chips.Take(MaxVisibleChips);

    /// <summary>Up to 4 hex colors used to paint the collapsed left-panel's icon rail as pie
    /// wedges (one per bound LogType) instead of a single flat circle - falls back to the
    /// source's own DisplayColor for a card with no LogTypes bound yet, so the rail never shows
    /// an empty/invisible swatch.</summary>
    public IReadOnlyList<string> WedgeColors =>
        Chips.Count > 0
            ? Chips.Take(MaxVisibleChips).Select(c => c.ColorHex).ToList()
            : new List<string> { Source.DisplayColor };

    public IEnumerable<LogTypeChipViewModel> OverflowChips =>
        IsOverflowExpanded ? Enumerable.Empty<LogTypeChipViewModel>() : Chips.Skip(MaxVisibleChips);

    public bool HasOverflow => Chips.Count > MaxVisibleChips;

    public int OverflowCount => Math.Max(0, Chips.Count - MaxVisibleChips);

    public bool IsOverflowExpanded
    {
        get => _isOverflowExpanded;
        set
        {
            if (SetProperty(ref _isOverflowExpanded, value))
            {
                OnPropertyChanged(nameof(VisibleChips));
                OnPropertyChanged(nameof(OverflowChips));
            }
        }
    }

    /// <summary>Called once at startup so each chip's count reflects rows already sitting in
    /// SQLite from a previous session, without re-parsing anything.</summary>
    public void RefreshCountsFromDatabase()
    {
        foreach (var binding in Source.LogTypes)
            binding.ParsedBlockCount = (int)Math.Min(_database.CountForBinding(Source.Id, binding.LogTypeId), int.MaxValue);

        // Source.ParsedBlockCount (and friends) are computed aggregates over the bindings above -
        // they don't raise their own PropertyChanged when a binding's count changes underneath
        // them, so the card subtext (bound straight to Source.ParsedBlockCount) needs an explicit
        // nudge here or it would stay stuck at 0 until some other sync event happened to refresh it.
        Source.RefreshComputedState();
    }

    // ===================================================================
    // Drag & drop
    // ===================================================================

    public bool IsLogTypePickerOpen
    {
        get => _isLogTypePickerOpen;
        set => SetProperty(ref _isLogTypePickerOpen, value);
    }

    /// <summary>A drop landed directly on one LogType chip's icon - load the files straight
    /// into that binding, no prompt needed since the user already aimed at the LogType.</summary>
    public async Task HandleDropOnChipAsync(SourceLogType binding, LogType logType, string[] rawPaths)
    {
        var expanded = FileDropHelper.ExpandDroppedPaths(rawPaths, msg => ToastRequested?.Invoke(msg));
        if (expanded.Count == 0)
        {
            ToastRequested?.Invoke("No usable files found in what you dropped.");
            return;
        }

        MergePaths(binding.FilePaths, expanded);
        ConfigChanged?.Invoke();
        await SyncBindingAsync(binding, logType).ConfigureAwait(true);
    }

    /// <summary>A drop landed on the card but not on any specific chip - ask which LogType it
    /// belongs to (or offer to create a new one) before loading anything.</summary>
    public void HandleDropOnCard(string[] rawPaths)
    {
        var expanded = FileDropHelper.ExpandDroppedPaths(rawPaths, msg => ToastRequested?.Invoke(msg));
        if (expanded.Count == 0)
        {
            ToastRequested?.Invoke("No usable files found in what you dropped.");
            return;
        }

        _pendingDropPaths = expanded;
        IsLogTypePickerOpen = true;
    }

    private void PickLogTypeForPendingDrop(LogType logType)
    {
        IsLogTypePickerOpen = false;

        var binding = Source.FindBinding(logType.Id);
        if (binding is null)
        {
            binding = new SourceLogType { LogTypeId = logType.Id };
            Source.LogTypes.Add(binding);
            RebuildChips();
        }

        MergePaths(binding.FilePaths, _pendingDropPaths);
        ConfigChanged?.Invoke();
        _ = SyncBindingAsync(binding, logType);
    }

    /// <summary>Called by the owner (MainWindow) once a brand-new LogType created from the
    /// "+ New Log Type..." quick-create flow has been added to AllLogTypes - binds it to this
    /// source with the files that were pending from the original drop.</summary>
    public void CompleteNewLogTypeDrop(LogType newLogType)
    {
        var binding = new SourceLogType { LogTypeId = newLogType.Id };
        MergePaths(binding.FilePaths, _pendingDropPaths);
        Source.LogTypes.Add(binding);
        RebuildChips();
        ConfigChanged?.Invoke();
        _ = SyncBindingAsync(binding, newLogType);
    }

    private static void MergePaths(List<string> target, IEnumerable<string> newPaths)
    {
        foreach (var path in newPaths)
        {
            if (!target.Contains(path, StringComparer.OrdinalIgnoreCase))
                target.Add(path);
        }
    }

    private void AddFilesViaDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Add files to \"{Source.Name}\"",
            Multiselect = true,
            Filter = "All supported files (*.csv;*.txt;*.log;*.tsv;*.zip)|*.csv;*.txt;*.log;*.tsv;*.zip|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            HandleDropOnCard(dialog.FileNames);
    }

    // ===================================================================
    // Per-binding ingestion
    // ===================================================================

    public async Task SyncBindingAsync(SourceLogType binding, LogType logType)
    {
        if (binding.IsSyncing || !binding.HasFiles) return;

        var cts = new CancellationTokenSource();
        binding.CurrentCts = cts;
        binding.IsSyncing = true;
        Source.RefreshComputedState();

        try
        {
            var result = await _ingestionService.IngestBindingAsync(Source, logType, binding, cts.Token).ConfigureAwait(true);
            binding.ParsedBlockCount = (int)Math.Min(result.TotalCount, int.MaxValue);

            var actionable = result.Warnings.Where(w => w.RequiresUserAction).ToList();
            binding.HasError = actionable.Count > 0;
            binding.ErrorCount = actionable.Count;
            // First warning's text stays as the quick tooltip/summary line; the full list is
            // reachable via ViewWarningsCommand ("let the user see the files that didn't
            // match") so this doesn't need to enumerate everything.
            binding.ErrorMessage = actionable.Count switch
            {
                0 => string.Empty,
                1 => actionable[0].Reason,
                _ => $"{actionable[0].Reason} (+{actionable.Count - 1} more - click to view all)"
            };

            SyncCompleted?.Invoke(this, binding, result);
        }
        catch (OperationCanceledException)
        {
            await Task.Run(() => _database.DeleteBlocksForBinding(Source.Id, logType.Id)).ConfigureAwait(true);
            binding.ParsedBlockCount = 0;
            SyncCancelled?.Invoke(this, binding);
        }
        catch (Exception ex)
        {
            await Task.Run(() => _database.DeleteBlocksForBinding(Source.Id, logType.Id)).ConfigureAwait(true);
            binding.ParsedBlockCount = 0;
            MessageBox.Show(
                $"Failed to sync \"{logType.Name}\" on source \"{Source.Name}\":\n\n{ex.Message}",
                "Log Aggregator - Sync Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SyncCancelled?.Invoke(this, binding);
        }
        finally
        {
            binding.IsSyncing = false;
            binding.CurrentCts = null;
            Source.RefreshComputedState();
        }
    }

    /// <summary>Re-syncs every bound LogType that already has files - used after startup isn't
    /// needed (SQLite already has the rows), but is exposed for a manual "resync all" if ever
    /// wired up from the UI.</summary>
    public async Task SyncAllAsync()
    {
        foreach (var binding in Source.LogTypes.Where(b => b.HasFiles).ToList())
        {
            var logType = _allLogTypes.FirstOrDefault(lt => lt.Id == binding.LogTypeId);
            if (logType is not null) await SyncBindingAsync(binding, logType).ConfigureAwait(true);
        }
    }
}
