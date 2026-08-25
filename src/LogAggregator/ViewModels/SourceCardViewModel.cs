using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;
using Microsoft.Win32;

namespace LogAggregator.ViewModels;

/// <summary>
/// View model for one card in the left panel. Owns the source's own ingestion lifecycle
/// (import, sync, cancel) and raises events the MainViewModel listens to for merging results
/// into the shared display collection.
/// </summary>
public class SourceCardViewModel : ObservableObject
{
    private readonly IngestionService _ingestionService = new();

    public LogSource Source { get; }

    /// <summary>Raised when a sync completes successfully with a fresh batch of blocks - the
    /// MainViewModel replaces this source's previously-displayed blocks with these.</summary>
    public event Action<SourceCardViewModel, SourceIngestionResult>? SyncCompleted;

    /// <summary>Raised when the user cancels an in-flight sync - the MainViewModel purges any
    /// rows already shown for this source.</summary>
    public event Action<SourceCardViewModel>? SyncCancelled;

    /// <summary>Raised whenever anything about this card changes in a way that should persist
    /// to sources.config.json (import, edit, active toggle).</summary>
    public event Action? ConfigChanged;

    /// <summary>Raised to ask the owner to open the edit wizard for this source.</summary>
    public event Action<SourceCardViewModel>? EditRequested;

    public SourceCardViewModel(LogSource source)
    {
        Source = source;
        Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogSource.IsActive))
                ConfigChanged?.Invoke();
        };

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !Source.IsSyncing);
        SettingsCommand = new RelayCommand(() => EditRequested?.Invoke(this), () => !Source.IsSyncing);
        CancelSyncCommand = new RelayCommand(CancelSync, () => Source.IsSyncing);
        SetColorCommand = new RelayCommand(param =>
        {
            if (param is string hex)
            {
                Source.DisplayColor = hex;
                ConfigChanged?.Invoke();
            }
        });
    }

    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand CancelSyncCommand { get; }
    public RelayCommand SetColorCommand { get; }

    public static IReadOnlyList<string> PresetColors => WizardViewModel.PresetColors;

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Import files for \"{Source.Name}\"",
            Multiselect = true,
            Filter = Source.Type switch
            {
                FileType.CSV => "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileType.TabDelimited => "Text/Tab files (*.txt;*.tsv)|*.txt;*.tsv|All files (*.*)|*.*",
                _ => "Text files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*"
            }
        };

        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames)
        {
            if (!Source.FilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                Source.FilePaths.Add(path);
        }
        Source.RefreshComputedState();
        ConfigChanged?.Invoke();

        await SyncAsync();
    }

    public async Task SyncAsync()
    {
        if (Source.IsSyncing || !Source.HasFiles) return;

        var cts = new CancellationTokenSource();
        Source.CurrentCts = cts;
        Source.IsSyncing = true;

        try
        {
            var result = await _ingestionService.IngestSourceAsync(Source, cts.Token).ConfigureAwait(true);
            Source.ParsedBlockCount = result.Blocks.Count;
            SyncCompleted?.Invoke(this, result);
        }
        catch (OperationCanceledException)
        {
            SyncCancelled?.Invoke(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to sync source \"{Source.Name}\":\n\n{ex.Message}",
                "Log Aggregator - Sync Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SyncCancelled?.Invoke(this);
        }
        finally
        {
            Source.IsSyncing = false;
            Source.CurrentCts = null;
        }
    }

    private void CancelSync()
    {
        Source.CurrentCts?.Cancel();
    }
}
