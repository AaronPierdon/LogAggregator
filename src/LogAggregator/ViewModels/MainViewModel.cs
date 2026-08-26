using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;
using Microsoft.Win32;

namespace LogAggregator.ViewModels;

/// <summary>
/// Orchestrates sources, config persistence, and the SQL-backed display window. Rows are never
/// all held in memory at once - DisplayedBlocks is a bounded sliding window (see MaxLoadedRows)
/// fed by paged queries against LogDatabase. This is the change that actually fixes the 36
/// million row memory problem; everything upstream of this class (ingestion) already writes
/// straight to SQLite rather than building an in-memory list.
///
/// Known limitation of this v1 windowing model: scrolling loads more rows going *forward*
/// (appending as you approach the bottom of what's loaded, trimming from the front once the
/// window exceeds its cap), but there's no symmetric "load more going backward" - if you jump
/// to the end and want to scroll back up past what's still loaded, use "Jump to Start" rather
/// than expecting the scrollbar to represent your true position across the full filtered
/// result set. True bidirectional virtualization (a custom IList a WPF DataGrid can page
/// against transparently in both directions) is a reasonable v2 if this ever feels limiting in
/// practice - flagged rather than silently implemented, since it's real additional complexity.
/// </summary>
public class MainViewModel : ObservableObject
{
    private const int PageSize = 2000;
    private const int MaxLoadedRows = 50_000;

    private readonly ConfigService _configService = new();
    private readonly LogDatabase _database;
    private readonly Dictionary<string, SourceCardViewModel> _sourceLookup = new();

    private AppSettings _settings = new();
    private DispatcherTimer? _toastTimer;

    private List<string> _appliedOr = new();
    private List<string> _appliedAnd = new();
    private List<string> _appliedExclusion = new();

    private SortColumn _sortColumn = SortColumn.UniversalTimestamp;
    private bool _sortDescending;
    private long _loadedUpToOffset;

    private string _orFilterText = string.Empty;
    private string _andFilterText = string.Empty;
    private string _exclusionFilterText = string.Empty;
    private bool _isPanelCollapsed;
    private bool? _allActive = true;
    private bool _filtersActive;
    private string _toastMessage = string.Empty;
    private bool _toastVisible;
    private long _totalMatchingCount;
    private long _grandTotalCount;
    private bool _isLoadingMore;

    public ObservableCollection<SourceCardViewModel> Sources { get; } = new();

    /// <summary>The sliding window of currently-displayed rows. Bound directly to the
    /// DataGrid's ItemsSource - never contains more than MaxLoadedRows items.</summary>
    public ObservableCollection<LogBlock> DisplayedBlocks { get; } = new();

    public ObservableCollection<IngestionWarning> Warnings { get; } = new();

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

    /// <summary>Rows matching the current filter + active sources - the denominator shown
    /// alongside how many of those are currently loaded into memory.</summary>
    public long TotalMatchingCount
    {
        get => _totalMatchingCount;
        private set => SetProperty(ref _totalMatchingCount, value);
    }

    /// <summary>Every row across every source, regardless of filter or active state - shown for
    /// context in the status bar.</summary>
    public long GrandTotalCount
    {
        get => _grandTotalCount;
        private set => SetProperty(ref _grandTotalCount, value);
    }

    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

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
            _ = ReloadFirstPageAsync();
        }
    }

    public bool AnySourceSyncing => Sources.Any(s => s.Source.IsSyncing);

    public ICommand AddSourceCommand { get; }
    public ICommand TogglePanelCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public ICommand HideDropZoneCommand { get; }
    public AsyncRelayCommand JumpToStartCommand { get; }
    public AsyncRelayCommand JumpToEndCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }

    /// <summary>Requests the wizard be opened. Null card = create new source; non-null = edit.</summary>
    public event Action<SourceCardViewModel?>? WizardRequested;

    public MainViewModel(LogDatabase database)
    {
        _database = database;

        AddSourceCommand = new RelayCommand(() => WizardRequested?.Invoke(null));
        TogglePanelCommand = new RelayCommand(() => IsPanelCollapsed = !IsPanelCollapsed);
        ApplyFiltersCommand = new AsyncRelayCommand(ApplyFiltersAsync, () => !AnySourceSyncing);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => GrandTotalCount > 0);
        HideDropZoneCommand = new RelayCommand(HideDropZone);
        JumpToStartCommand = new AsyncRelayCommand(ReloadFirstPageAsync);
        JumpToEndCommand = new AsyncRelayCommand(JumpToEndAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
    }

    public async Task InitializeAsync()
    {
        var config = await _configService.LoadAsync().ConfigureAwait(true);
        _settings = config.Settings ?? new AppSettings();
        OnPropertyChanged(nameof(ShowDropZone));

        foreach (var source in config.Sources)
            AddSourceCardInternal(source);

        UpdateAllActiveState();

        // Data already persists in SQLite across restarts, so - unlike the old in-memory
        // design - we do NOT automatically re-parse every source's files on every startup.
        // Just read back the counts already sitting in the database.
        foreach (var card in Sources)
            card.Source.ParsedBlockCount = (int)Math.Min(_database.CountForSource(card.Source.Id), int.MaxValue);

        await RefreshGrandTotalAsync().ConfigureAwait(true);
        await ReloadFirstPageAsync().ConfigureAwait(true);
    }

    private void AddSourceCardInternal(LogSource source)
    {
        var card = new SourceCardViewModel(source, _database);
        card.SyncCompleted += OnSyncCompleted;
        card.SyncCancelled += OnSyncCancelled;
        card.ConfigChanged += () => _ = SaveConfigAsync();
        card.EditRequested += c => WizardRequested?.Invoke(c);

        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogSource.IsActive))
            {
                _ = ReloadFirstPageAsync();
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

    public async Task RemoveSourceAsync(SourceCardViewModel card)
    {
        card.Source.CurrentCts?.Cancel();
        await Task.Run(() => _database.DeleteBlocksForSource(card.Source.Id)).ConfigureAwait(true);

        Sources.Remove(card);
        _sourceLookup.Remove(card.Source.Id);
        UpdateAllActiveState();

        await RefreshGrandTotalAsync().ConfigureAwait(true);
        await ReloadFirstPageAsync().ConfigureAwait(true);
        await SaveConfigAsync().ConfigureAwait(true);
    }

    private async void OnSyncCompleted(SourceCardViewModel card, SourceIngestionResult result)
    {
        foreach (var warning in result.Warnings) Warnings.Add(warning);
        await RefreshGrandTotalAsync().ConfigureAwait(true);
        await ReloadFirstPageAsync().ConfigureAwait(true);
        CommandManager.InvalidateRequerySuggested();
    }

    private async void OnSyncCancelled(SourceCardViewModel card)
    {
        await RefreshGrandTotalAsync().ConfigureAwait(true);
        await ReloadFirstPageAsync().ConfigureAwait(true);
    }

    // ===================================================================
    // SQL-backed windowed loading
    // ===================================================================

    private List<string> ActiveSourceIds() => Sources.Where(s => s.Source.IsActive).Select(s => s.Source.Id).ToList();

    private async Task RefreshGrandTotalAsync()
    {
        var allIds = Sources.Select(s => s.Source.Id).ToList();
        GrandTotalCount = allIds.Count == 0
            ? 0
            : await Task.Run(() => _database.CountMatching(allIds, new List<string>(), new List<string>(), new List<string>())).ConfigureAwait(true);
    }

    public async Task ReloadFirstPageAsync()
    {
        var activeIds = ActiveSourceIds();

        if (activeIds.Count == 0)
        {
            DisplayedBlocks.Clear();
            TotalMatchingCount = 0;
            _loadedUpToOffset = 0;
            return;
        }

        var count = await Task.Run(() => _database.CountMatching(activeIds, _appliedOr, _appliedAnd, _appliedExclusion)).ConfigureAwait(true);
        var page = await Task.Run(() => _database.QueryPage(activeIds, _appliedOr, _appliedAnd, _appliedExclusion, _sortColumn, _sortDescending, 0, PageSize)).ConfigureAwait(true);

        TotalMatchingCount = count;
        DisplayedBlocks.Clear();
        foreach (var b in page) DisplayedBlocks.Add(b);
        _loadedUpToOffset = page.Count;
    }

    public async Task LoadMoreAsync()
    {
        if (IsLoadingMore) return;
        if (_loadedUpToOffset >= TotalMatchingCount) return;

        IsLoadingMore = true;
        try
        {
            var activeIds = ActiveSourceIds();
            if (activeIds.Count == 0) return;

            var page = await Task.Run(() => _database.QueryPage(
                activeIds, _appliedOr, _appliedAnd, _appliedExclusion,
                _sortColumn, _sortDescending, (int)_loadedUpToOffset, PageSize)).ConfigureAwait(true);

            foreach (var b in page) DisplayedBlocks.Add(b);
            _loadedUpToOffset += page.Count;

            // Sliding window: trim from the front once we exceed the cap, so memory stays
            // bounded no matter how far the user keeps scrolling through a huge result set.
            if (DisplayedBlocks.Count > MaxLoadedRows)
            {
                int toRemove = DisplayedBlocks.Count - MaxLoadedRows;
                for (int i = 0; i < toRemove; i++) DisplayedBlocks.RemoveAt(0);
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    public async Task JumpToEndAsync()
    {
        var activeIds = ActiveSourceIds();
        if (activeIds.Count == 0) return;

        var count = await Task.Run(() => _database.CountMatching(activeIds, _appliedOr, _appliedAnd, _appliedExclusion)).ConfigureAwait(true);
        TotalMatchingCount = count;

        var tailOffset = (int)Math.Max(0, count - PageSize);
        var page = await Task.Run(() => _database.QueryPage(activeIds, _appliedOr, _appliedAnd, _appliedExclusion, _sortColumn, _sortDescending, tailOffset, PageSize)).ConfigureAwait(true);

        DisplayedBlocks.Clear();
        foreach (var b in page) DisplayedBlocks.Add(b);
        _loadedUpToOffset = count;
    }

    /// <summary>Called from the DataGrid header click handler in MainWindow.xaml.cs, since
    /// sorting is now a fresh SQL query rather than an in-memory ICollectionView re-sort.</summary>
    public async Task SetSortAsync(SortColumn column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        await ReloadFirstPageAsync().ConfigureAwait(true);
    }

    public SortColumn CurrentSortColumn => _sortColumn;
    public bool CurrentSortDescending => _sortDescending;

    // ===================================================================
    // Filters
    // ===================================================================

    private async Task ApplyFiltersAsync()
    {
        _appliedOr = FilterService.ParseTerms(OrFilterText);
        _appliedAnd = FilterService.ParseTerms(AndFilterText);
        _appliedExclusion = FilterService.ParseTerms(ExclusionFilterText);
        FiltersActive = _appliedOr.Count > 0 || _appliedAnd.Count > 0 || _appliedExclusion.Count > 0;
        await ReloadFirstPageAsync().ConfigureAwait(true);
    }

    private async Task ClearFiltersAsync()
    {
        OrFilterText = string.Empty;
        AndFilterText = string.Empty;
        ExclusionFilterText = string.Empty;
        _appliedOr = new(); _appliedAnd = new(); _appliedExclusion = new();
        FiltersActive = false;
        await ReloadFirstPageAsync().ConfigureAwait(true);
    }

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

    // ===================================================================
    // Quick-add drop zone
    // ===================================================================

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

    // ===================================================================
    // Export
    // ===================================================================

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export aggregated logs",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"log-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var activeIds = ActiveSourceIds();
        if (activeIds.Count == 0)
        {
            MessageBox.Show("No active sources to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExportService.ExportAsync(
            dialog.FileName, _database, activeIds,
            _appliedOr, _appliedAnd, _appliedExclusion,
            _sortColumn, _sortDescending).ConfigureAwait(true);

        MessageBox.Show(
            $"Exported to:\n{dialog.FileName}",
            "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SaveConfigAsync()
    {
        var config = new AppConfig { Sources = Sources.Select(c => c.Source).ToList(), Settings = _settings };
        await _configService.SaveAsync(config).ConfigureAwait(true);
    }
}
