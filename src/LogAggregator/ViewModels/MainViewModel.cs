using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;
using Microsoft.Win32;

namespace LogAggregator.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService = new();
    private readonly Dictionary<string, SourceCardViewModel> _sourceLookup = new();
    private readonly CollectionViewSource _viewSource = new();

    private AppSettings _settings = new();
    private DispatcherTimer? _toastTimer;

    private List<string> _appliedOr = new();
    private List<string> _appliedAnd = new();
    private List<string> _appliedExclusion = new();

    private string _orFilterText = string.Empty;
    private string _andFilterText = string.Empty;
    private string _exclusionFilterText = string.Empty;
    private bool _isPanelCollapsed;
    private bool? _allActive = true;
    private bool _filtersActive;
    private string _toastMessage = string.Empty;
    private bool _toastVisible;

    public ObservableCollection<SourceCardViewModel> Sources { get; } = new();
    public ObservableCollection<LogBlock> AllBlocks { get; } = new();
    public ObservableCollection<IngestionWarning> Warnings { get; } = new();

    public ICollectionView BlocksView => _viewSource.View;

    public string OrFilterText { get => _orFilterText; set => SetProperty(ref _orFilterText, value); }
    public string AndFilterText { get => _andFilterText; set => SetProperty(ref _andFilterText, value); }
    public string ExclusionFilterText { get => _exclusionFilterText; set => SetProperty(ref _exclusionFilterText, value); }

    public bool IsPanelCollapsed
    {
        get => _isPanelCollapsed;
        set => SetProperty(ref _isPanelCollapsed, value);
    }

    public bool FiltersActive
    {
        get => _filtersActive;
        private set => SetProperty(ref _filtersActive, value);
    }

    /// <summary>Whether the quick-add drag &amp; drop banner is shown. Persisted; toggled from
    /// the Settings menu or via the banner's own minimize button.</summary>
    public bool ShowDropZone
    {
        get => _settings.ShowDropZone;
        set
        {
            if (_settings.ShowDropZone == value) return;
            _settings.ShowDropZone = value;
            OnPropertyChanged();
            _ = SaveConfigAsync();
        }
    }

    public string ToastMessage { get => _toastMessage; private set => SetProperty(ref _toastMessage, value); }
    public bool ToastVisible { get => _toastVisible; private set => SetProperty(ref _toastVisible, value); }

    /// <summary>Master header checkbox: true=all active, false=none active, null=mixed.</summary>
    public bool? AllActive
    {
        get => _allActive;
        set
        {
            if (!SetProperty(ref _allActive, value)) return;
            bool newState = value ?? true;
            foreach (var card in Sources) card.Source.IsActive = newState;
            RefreshView();
        }
    }

    public bool AnySourceSyncing => Sources.Any(s => s.Source.IsSyncing);

    public ICommand AddSourceCommand { get; }
    public ICommand TogglePanelCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public ICommand HideDropZoneCommand { get; }

    /// <summary>Requests the wizard be opened. Null card = create new source; non-null = edit.</summary>
    public event Action<SourceCardViewModel?>? WizardRequested;

    public MainViewModel()
    {
        _viewSource.Source = AllBlocks;
        _viewSource.Filter += (_, e) => e.Accepted = PassesFilter(e.Item as LogBlock);
        _viewSource.View.SortDescriptions.Add(new SortDescription(nameof(LogBlock.UniversalTimestamp), ListSortDirection.Ascending));

        AddSourceCommand = new RelayCommand(() => WizardRequested?.Invoke(null));
        TogglePanelCommand = new RelayCommand(() => IsPanelCollapsed = !IsPanelCollapsed);
        ApplyFiltersCommand = new RelayCommand(ApplyFilters, () => !AnySourceSyncing);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => AllBlocks.Count > 0);
        HideDropZoneCommand = new RelayCommand(HideDropZone);
    }

    public async Task InitializeAsync()
    {
        var config = await _configService.LoadAsync().ConfigureAwait(true);
        _settings = config.Settings ?? new AppSettings();
        OnPropertyChanged(nameof(ShowDropZone));

        foreach (var source in config.Sources)
            AddSourceCardInternal(source);

        UpdateAllActiveState();

        // Re-populate the grid from disk on startup for any source that already had files.
        foreach (var card in Sources.Where(c => c.Source.HasFiles).ToList())
            _ = card.SyncAsync();
    }

    private void AddSourceCardInternal(LogSource source)
    {
        var card = new SourceCardViewModel(source);
        card.SyncCompleted += OnSyncCompleted;
        card.SyncCancelled += OnSyncCancelled;
        card.ConfigChanged += () => _ = SaveConfigAsync();
        card.EditRequested += c => WizardRequested?.Invoke(c);

        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogSource.IsActive))
            {
                RefreshView();
                UpdateAllActiveState();
            }
            if (e.PropertyName == nameof(LogSource.IsSyncing))
            {
                OnPropertyChanged(nameof(AnySourceSyncing));
                CommandManager.InvalidateRequerySuggested();
            }
        };

        Sources.Add(card);
        _sourceLookup[source.Id] = card;
    }

    /// <summary>Called by the wizard flow (via MainWindow) when the user finishes creating or
    /// editing a source. For a new source, <paramref name="existingCard"/> is null.</summary>
    public async Task ApplyWizardResultAsync(SourceCardViewModel? existingCard, LogSource resultSource)
    {
        SourceCardViewModel card;

        if (existingCard is null)
        {
            AddSourceCardInternal(resultSource);
            card = Sources.Last();
        }
        else
        {
            existingCard.Source.Name = resultSource.Name;
            existingCard.Source.Type = resultSource.Type;
            existingCard.Source.TimestampProfile = resultSource.TimestampProfile;
            existingCard.Source.DisplayColor = resultSource.DisplayColor;
            existingCard.Source.FilePaths = resultSource.FilePaths;
            existingCard.Source.RefreshComputedState();
            card = existingCard;
        }

        await SaveConfigAsync().ConfigureAwait(true);

        if (card.Source.HasFiles)
            await card.SyncAsync().ConfigureAwait(true);
    }

    /// <summary>Called when files (or a zip) are dropped on the main window's quick-add drop
    /// zone. Detects everything automatically and adds+syncs a new source with zero prompts -
    /// if detection isn't confident, it declines and points the user at "+ Add Source" instead,
    /// so a low-confidence guess is never silently applied.</summary>
    public async Task QuickAddSourceFromDropAsync(IEnumerable<string> rawPaths)
    {
        var expanded = FileDropHelper.ExpandDroppedPaths(rawPaths, msg => ShowToast(msg));
        if (expanded.Count == 0)
        {
            ShowToast("No usable files found in what you dropped.");
            return;
        }

        var fileType = FileTypeDetector.DetectFromFile(expanded[0]);
        var sampleLines = FileTypeDetector.ReadSampleLines(expanded[0], 30);
        var delimiter = fileType == FileType.TabDelimited ? '\t' : ',';
        var detection = TimestampDetector.AutoDetectFromLines(sampleLines, fileType, delimiter);

        if (detection.Status != DetectionStatus.Confident)
        {
            ShowToast("Couldn't auto-detect this confidently - use \"+ Add Source\" instead so you can review it.", TimeSpan.FromSeconds(6));
            return;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(expanded[0]).Replace('_', ' ').Replace('-', ' ');
        var name = baseName;
        int suffix = 2;
        while (Sources.Any(s => string.Equals(s.Source.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";

        var source = new LogSource
        {
            Name = name,
            Type = fileType,
            TimestampProfile = detection.Candidates[0].Profile,
            DisplayColor = WizardViewModel.PresetColors[Sources.Count % WizardViewModel.PresetColors.Length],
            FilePaths = expanded,
            IsActive = true
        };

        AddSourceCardInternal(source);
        await SaveConfigAsync().ConfigureAwait(true);
        ShowToast($"Added \"{name}\" from {expanded.Count} file(s) and started syncing.");

        await Sources.Last().SyncAsync().ConfigureAwait(true);
    }

    public async Task RemoveSourceAsync(SourceCardViewModel card)
    {
        card.Source.CurrentCts?.Cancel();
        RemoveBlocksForSource(card.Source.Id);
        Sources.Remove(card);
        _sourceLookup.Remove(card.Source.Id);
        RefreshView();
        UpdateAllActiveState();
        await SaveConfigAsync().ConfigureAwait(true);
    }

    private void OnSyncCompleted(SourceCardViewModel card, SourceIngestionResult result)
    {
        RemoveBlocksForSource(card.Source.Id);
        foreach (var block in result.Blocks) AllBlocks.Add(block);
        foreach (var warning in result.Warnings) Warnings.Add(warning);
        RefreshView();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnSyncCancelled(SourceCardViewModel card)
    {
        RemoveBlocksForSource(card.Source.Id);
        card.Source.ParsedBlockCount = 0;
        RefreshView();
    }

    private void RemoveBlocksForSource(string sourceId)
    {
        var toRemove = AllBlocks.Where(b => b.SourceId == sourceId).ToList();
        foreach (var b in toRemove) AllBlocks.Remove(b);
    }

    private bool PassesFilter(LogBlock? block)
    {
        if (block is null) return false;
        if (_sourceLookup.TryGetValue(block.SourceId, out var card) && !card.Source.IsActive) return false;

        if (_appliedOr.Count == 0 && _appliedAnd.Count == 0 && _appliedExclusion.Count == 0) return true;
        return FilterService.BlockPasses(block, _appliedOr, _appliedAnd, _appliedExclusion);
    }

    private void ApplyFilters()
    {
        _appliedOr = FilterService.ParseTerms(OrFilterText);
        _appliedAnd = FilterService.ParseTerms(AndFilterText);
        _appliedExclusion = FilterService.ParseTerms(ExclusionFilterText);
        FiltersActive = _appliedOr.Count > 0 || _appliedAnd.Count > 0 || _appliedExclusion.Count > 0;
        RefreshView();
    }

    private void ClearFilters()
    {
        OrFilterText = string.Empty;
        AndFilterText = string.Empty;
        ExclusionFilterText = string.Empty;
        _appliedOr = new(); _appliedAnd = new(); _appliedExclusion = new();
        FiltersActive = false;
        RefreshView();
    }

    private void RefreshView() => BlocksView.Refresh();

    private void UpdateAllActiveState()
    {
        if (Sources.Count == 0) { _allActive = true; OnPropertyChanged(nameof(AllActive)); return; }
        bool allOn = Sources.All(s => s.Source.IsActive);
        bool allOff = Sources.All(s => !s.Source.IsActive);
        _allActive = allOn ? true : (allOff ? false : (bool?)null);
        OnPropertyChanged(nameof(AllActive));
    }

    private void HideDropZone()
    {
        ShowDropZone = false;
        ShowToast("Drag & drop area hidden. Re-enable it anytime from Settings > Interface.", TimeSpan.FromSeconds(6));
    }

    public void ShowToast(string message, TimeSpan? duration = null)
    {
        ToastMessage = message;
        ToastVisible = true;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastVisible = false;
            _toastTimer?.Stop();
        };
        _toastTimer.Start();
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export aggregated logs",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"log-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var items = BlocksView.Cast<LogBlock>().ToList();
        await ExportService.ExportAsync(dialog.FileName, items).ConfigureAwait(true);

        MessageBox.Show(
            $"Exported {items.Count:N0} blocks to:\n{dialog.FileName}",
            "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveConfigAsync()
    {
        var config = new AppConfig { Sources = Sources.Select(c => c.Source).ToList(), Settings = _settings };
        await _configService.SaveAsync(config).ConfigureAwait(true);
    }
}
