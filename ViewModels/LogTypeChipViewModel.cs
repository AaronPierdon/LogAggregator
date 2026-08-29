using System;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;

namespace LogAggregator.ViewModels;

/// <summary>
/// One chip on a Source card - one LogType bound to that source (see SourceLogType), rendered
/// as a color swatch, an icon glyph, or both (per the LogType's DisplayMode). Wraps both the
/// binding (files/parse state, which is per-source) and the LogType it points to (name/color/
/// icon, which is shared and edited from the LogTypes window) so the card template has one
/// object to bind everything from.
/// </summary>
public class LogTypeChipViewModel : ObservableObject
{
    public SourceLogType Binding { get; }
    public LogType LogType { get; private set; }

    public LogTypeChipViewModel(SourceLogType binding, LogType logType, ICommand dropCommand, ICommand cancelSyncCommand)
    {
        Binding = binding;
        LogType = logType;
        DropCommand = dropCommand;
        CancelSyncCommand = cancelSyncCommand;

        ChangeTimestampCommand = new RelayCommand(() => ChangeTimestampRequested?.Invoke(this));
        RemoveFromSourceCommand = new RelayCommand(() => RemoveFromSourceRequested?.Invoke(this));
        DeleteLogTypeCommand = new RelayCommand(() => DeleteLogTypeRequested?.Invoke(this));

        Binding.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SourceLogType.ParsedBlockCount))
                OnPropertyChanged(nameof(ParsedBlockCount));
            if (e.PropertyName is nameof(SourceLogType.IsSyncing))
                OnPropertyChanged(nameof(IsSyncing));
            if (e.PropertyName is nameof(SourceLogType.HasError) or nameof(SourceLogType.ErrorMessage))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorMessage));
            }
        };
    }

    public string Name => LogType.Name;
    public string ColorHex => LogType.ColorHex;
    public string IconGlyph => LogType.IconGlyph;
    public LogTypeDisplayMode DisplayMode => LogType.DisplayMode;

    public int ParsedBlockCount => Binding.ParsedBlockCount;
    public bool IsSyncing => Binding.IsSyncing;
    public bool HasError => Binding.HasError;
    public string ErrorMessage => Binding.ErrorMessage;

    /// <summary>Drop target for files landing directly on this chip - wired by
    /// SourceCardViewModel to load them straight into this LogType's binding, no "which
    /// LogType?" prompt needed since the user already picked it by aiming at the icon.</summary>
    public ICommand DropCommand { get; }

    /// <summary>Cancels an in-flight sync for just this chip's binding.</summary>
    public ICommand CancelSyncCommand { get; }

    /// <summary>Called when the underlying LogType's name/color/icon/display mode changes (from
    /// the LogTypes window) - LogType itself isn't observable, so this forces a blanket refresh
    /// of everything this chip shows.</summary>
    public void NotifyAppearanceChanged() => OnPropertyChanged(string.Empty);

    // ===================================================================
    // Right-click context menu (see Views/LogTypeChip.xaml). This view model doesn't own the
    // database or the other Source cards a LogType might also be bound to, so all three actions
    // just bubble an event up to SourceCardViewModel, which has what it needs to actually carry
    // them out (see SourceCardViewModel.RebuildChips/RemoveBindingFromSource).
    // ===================================================================

    public ICommand ChangeTimestampCommand { get; }
    public ICommand RemoveFromSourceCommand { get; }
    public ICommand DeleteLogTypeCommand { get; }

    /// <summary>"Change timestamp pattern..." - open the LogType editor jumped straight to the
    /// graphical timestamp picker, pre-seeded with this binding's real files.</summary>
    public event Action<LogTypeChipViewModel>? ChangeTimestampRequested;

    /// <summary>"Remove from this source" - unbind just this LogType from this one Source
    /// (leaving the LogType itself, and its bindings on any other Source, untouched).</summary>
    public event Action<LogTypeChipViewModel>? RemoveFromSourceRequested;

    /// <summary>"Delete Log Type entirely..." - the global, cross-source cascade delete (same as
    /// deleting it from the "Manage Log Types" window).</summary>
    public event Action<LogTypeChipViewModel>? DeleteLogTypeRequested;
}
