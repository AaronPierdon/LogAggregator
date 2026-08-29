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

    public LogTypeChipViewModel(SourceLogType binding, LogType logType, ICommand dropCommand, ICommand cancelSyncCommand,
        ICommand openSettingsCommand, ICommand setTimestampPatternCommand, ICommand removeFromSourceCommand, ICommand viewWarningsCommand)
    {
        Binding = binding;
        LogType = logType;
        DropCommand = dropCommand;
        CancelSyncCommand = cancelSyncCommand;
        OpenSettingsCommand = openSettingsCommand;
        SetTimestampPatternCommand = setTimestampPatternCommand;
        RemoveFromSourceCommand = removeFromSourceCommand;
        ViewWarningsCommand = viewWarningsCommand;

        Binding.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SourceLogType.ParsedBlockCount))
                OnPropertyChanged(nameof(ParsedBlockCount));
            if (e.PropertyName is nameof(SourceLogType.IsSyncing))
                OnPropertyChanged(nameof(IsSyncing));
            if (e.PropertyName is nameof(SourceLogType.HasError) or nameof(SourceLogType.ErrorMessage) or nameof(SourceLogType.ErrorCount))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(ErrorCount));
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

    /// <summary>How many actionable warnings the last sync produced - shown as the badge's
    /// count ("3") so a user can tell "one thing to check" apart from "this file is a mess"
    /// without opening anything, before deciding whether to click through.</summary>
    public int ErrorCount => Binding.ErrorCount;

    /// <summary>Drop target for files landing directly on this chip - wired by
    /// SourceCardViewModel to load them straight into this LogType's binding, no "which
    /// LogType?" prompt needed since the user already picked it by aiming at the icon.</summary>
    public ICommand DropCommand { get; }

    /// <summary>Cancels an in-flight sync for just this chip's binding.</summary>
    public ICommand CancelSyncCommand { get; }

    /// <summary>Right-click menu: opens the full LogType editor for this LogType (name/color/
    /// icon/timestamp pattern), pre-seeded with this binding's already-loaded files so detection
    /// runs against real content immediately instead of asking the user to browse again.</summary>
    public ICommand OpenSettingsCommand { get; }

    /// <summary>Right-click menu: opens the same editor but jumps straight to the timestamp step
    /// with the manual pattern picker forced open, for "this LogType's pattern is fine, I just
    /// want to change it" without wading through the rest of the editor first.</summary>
    public ICommand SetTimestampPatternCommand { get; }

    /// <summary>Right-click menu: removes this LogType's binding (and its loaded rows) from just
    /// this source card - the LogType itself, and its bindings on any other card, are untouched.</summary>
    public ICommand RemoveFromSourceCommand { get; }

    /// <summary>Clicking the error badge itself (not the right-click menu) opens the full
    /// Parse Warnings list, filtered to just this binding - "let the user see the files that
    /// didn't match" for this specific chip, not just the first warning's summary text.</summary>
    public ICommand ViewWarningsCommand { get; }

    /// <summary>Called when the underlying LogType's name/color/icon/display mode changes (from
    /// the LogTypes window) - LogType itself isn't observable, so this forces a blanket refresh
    /// of everything this chip shows.</summary>
    public void NotifyAppearanceChanged() => OnPropertyChanged(string.Empty);
}
