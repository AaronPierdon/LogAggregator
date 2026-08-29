using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;

namespace LogAggregator.ViewModels;

/// <summary>
/// Drives the "Manage Log Types" window: lists every defined LogType and handles add/edit/
/// delete. LogTypes are referenced by Id from each Source's bindings (see SourceLogType), so
/// editing one here (name/color/icon/timestamp pattern) needs to propagate to every card that
/// has it bound, and deleting one that's still in use needs to cascade - both handled here
/// rather than scattering "who references this LogType" logic elsewhere.
/// </summary>
public class LogTypesViewModel : ObservableObject
{
    private readonly ObservableCollection<SourceCardViewModel> _sourceCards;
    private readonly LogDatabase _database;

    public ObservableCollection<LogType> LogTypes { get; }

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }

    /// <summary>Requests the editor be opened for a LogType (null = create new).</summary>
    public event Action<LogType?>? EditorRequested;

    /// <summary>Raised whenever anything here changes in a way that should persist to
    /// sources.config.json.</summary>
    public event Action? ConfigChanged;

    public LogTypesViewModel(ObservableCollection<LogType> logTypes, ObservableCollection<SourceCardViewModel> sourceCards, LogDatabase database)
    {
        LogTypes = logTypes;
        _sourceCards = sourceCards;
        _database = database;

        AddCommand = new RelayCommand(() => EditorRequested?.Invoke(null));
        EditCommand = new RelayCommand(param =>
        {
            if (param is LogType logType) EditorRequested?.Invoke(logType);
        });
        DeleteCommand = new RelayCommand(param =>
        {
            if (param is LogType logType) Delete(logType);
        });
    }

    private void Delete(LogType logType)
    {
        var affectedCards = _sourceCards.Where(c => c.Source.FindBinding(logType.Id) is not null).ToList();

        string message = affectedCards.Count == 0
            ? $"Delete \"{logType.Name}\"? This cannot be undone."
            : $"Delete \"{logType.Name}\"? It's still bound to: {string.Join(", ", affectedCards.Select(c => c.Source.Name))}.\n\n" +
              "Deleting it removes those bindings - and their already-loaded rows - from each of those sources too. This cannot be undone.";

        var confirm = MessageBox.Show(message, "Delete Log Type", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var card in affectedCards)
        {
            var binding = card.Source.FindBinding(logType.Id);
            if (binding is null) continue;

            binding.CurrentCts?.Cancel();
            _database.DeleteBlocksForBinding(card.Source.Id, logType.Id);
            card.Source.LogTypes.Remove(binding);
            card.RefreshChips();
        }

        LogTypes.Remove(logType);
        ConfigChanged?.Invoke();
    }

    /// <summary>Called by the owner (MainWindow) after the editor finishes with Finish. For a
    /// new LogType, <paramref name="existing"/> is null.</summary>
    public void ApplyEditorResult(LogType? existing, LogType result)
    {
        if (existing is null)
        {
            LogTypes.Add(result);
        }
        else
        {
            existing.Name = result.Name;
            existing.Format = result.Format;
            existing.TimestampProfile = result.TimestampProfile;
            existing.ColorHex = result.ColorHex;
            existing.IconGlyph = result.IconGlyph;
            existing.DisplayMode = result.DisplayMode;

            // Propagate onto already-ingested rows and refresh every card showing this LogType.
            _database.UpdateLogTypeName(existing.Id, existing.Name);
            _database.UpdateLogTypeColor(existing.Id, existing.ColorHex);
            _database.UpdateLogTypeIcon(existing.Id, existing.IconGlyph);

            foreach (var card in _sourceCards)
                card.RefreshChips();
        }

        ConfigChanged?.Invoke();
    }
}
