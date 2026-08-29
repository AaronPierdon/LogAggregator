using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;

namespace LogAggregator.ViewModels;

/// <summary>The role the user assigns to a selected chunk of the sample line.</summary>
public enum TimestampTokenRole
{
    None,
    Date,
    Time,
    /// <summary>The chunk is a single self-contained blob that already contains both the date
    /// and time (e.g. a compact "20240102150405" run) - can only be assigned when it's the sole
    /// selected/tagged chunk.</summary>
    DateTime
}

/// <summary>One selectable chunk of the primary sample line, shown as a toggle button (plus a
/// role dropdown once selected) in TimestampPickerControl.</summary>
public class TimestampTokenViewModel : ObservableObject
{
    private bool _isSelected;
    private TimestampTokenRole _role = TimestampTokenRole.None;

    public TimestampToken Token { get; }

    public TimestampTokenViewModel(TimestampToken token) => Token = token;

    public string Text => Token.Text;
    public TimestampTokenHint Hint => Token.Hint;

    private static readonly IReadOnlyList<TimestampTokenRole> AllRoles =
        new[] { TimestampTokenRole.Date, TimestampTokenRole.Time, TimestampTokenRole.DateTime };

    private IReadOnlyList<TimestampTokenRole> _availableRoles = AllRoles;

    /// <summary>Bindable source for this token's role ComboBox in TimestampPickerControl. Unlike
    /// the app's other one-role-total constraints (enforced by value, in OnRoleChanged below),
    /// this is about what's OFFERED: once some other token is tagged Date, "Date" disappears from
    /// every other token's dropdown rather than staying selectable and silently stealing the tag
    /// on pick - directly addressing the "that choice(s) are gone" behavior the user asked for.
    /// Recomputed by <see cref="RefreshAvailableRoles"/>, called by the owning
    /// TimestampPickerViewModel whenever any token's Role or IsSelected changes.</summary>
    public IReadOnlyList<TimestampTokenRole> AvailableRoles
    {
        get => _availableRoles;
        private set => SetProperty(ref _availableRoles, value);
    }

    /// <summary>Recomputes which roles this token should offer, given every other token's
    /// current Role: Date is offered unless some OTHER token already holds Date (same for Time);
    /// Date+Time is offered only when no OTHER token holds any role at all, since it can only be
    /// the sole tagged token.</summary>
    public void RefreshAvailableRoles(IEnumerable<TimestampTokenViewModel> allTokens)
    {
        var others = allTokens.Where(t => t != this).ToList();
        bool otherHasDate = others.Any(t => t.Role == TimestampTokenRole.Date);
        bool otherHasTime = others.Any(t => t.Role == TimestampTokenRole.Time);
        bool otherHasAnyRole = others.Any(t => t.Role != TimestampTokenRole.None);

        var options = new List<TimestampTokenRole>();
        if (!otherHasDate) options.Add(TimestampTokenRole.Date);
        if (!otherHasTime) options.Add(TimestampTokenRole.Time);
        if (!otherHasAnyRole) options.Add(TimestampTokenRole.DateTime);

        AvailableRoles = options;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;

            if (!value)
            {
                Role = TimestampTokenRole.None;
            }
            else if (Role == TimestampTokenRole.None)
            {
                // Default the role from the app's soft guess so most selections need no further
                // clicking - the user can still change it via the dropdown.
                Role = Hint switch
                {
                    TimestampTokenHint.LikelyDatePart => TimestampTokenRole.Date,
                    TimestampTokenHint.LikelyTimePart => TimestampTokenRole.Time,
                    _ => TimestampTokenRole.None
                };
            }
        }
    }

    public TimestampTokenRole Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
    }
}

/// <summary>
/// Drives the interactive timestamp region picker: the fallback UI shown when auto-detection is
/// Ambiguous or Failed (see TimestampDetector.AutoDetectFromLines). The app pre-highlights
/// chunks of a real sample line it thinks might be date/time parts (TimestampTokenizer), but
/// every chunk is an independently selectable/deselectable button - nothing is free-text or
/// drag-selected. Each selected chunk gets its own Date / Time / Date+Time role, with live
/// validation: at most one chunk may be Date, at most one may be Time, and Date+Time is only
/// assignable to a chunk when it's the only one selected (assigning it clears every other
/// chunk's role, and assigning Date or Time to another chunk clears an existing Date+Time tag).
/// </summary>
public class TimestampPickerViewModel : ObservableObject
{
    private readonly FileType _fileType;
    private readonly List<string> _sampleLines;
    private int _primaryLineIndex;
    private string _primaryLine = string.Empty;
    private string _manualFormatString = string.Empty;
    private string _testMessage = string.Empty;
    private bool _testIsSuccess;
    private bool _canApply;

    public ObservableCollection<TimestampTokenViewModel> Tokens { get; } = new();

    /// <summary>A few other sample lines shown read-only underneath, so the user can visually
    /// sanity-check that the shape they're picking really is consistent across the file.</summary>
    public ObservableCollection<string> OtherSampleLines { get; } = new();

    public string PrimaryLine
    {
        get => _primaryLine;
        private set => SetProperty(ref _primaryLine, value);
    }

    /// <summary>Optional .NET custom format string, same as the wizard's existing manual
    /// override - most of the time left blank, since TryParseTimestampText already tries a wide
    /// library of built-in formats plus a free-form fallback against whatever text the selected
    /// chunk(s) capture.</summary>
    public string ManualFormatString
    {
        get => _manualFormatString;
        set => SetProperty(ref _manualFormatString, value);
    }

    public string TestMessage
    {
        get => _testMessage;
        private set => SetProperty(ref _testMessage, value);
    }

    public bool TestIsSuccess
    {
        get => _testIsSuccess;
        private set => SetProperty(ref _testIsSuccess, value);
    }

    public bool CanApply
    {
        get => _canApply;
        private set => SetProperty(ref _canApply, value);
    }

    /// <summary>Set once Apply succeeds against the real sample lines - the profile the caller
    /// (LogTypeEditorViewModel) should adopt.</summary>
    public TimestampProfile? ResultProfile { get; private set; }

    public ICommand SelectAnotherLineCommand { get; }
    public ICommand ApplyCommand { get; }

    /// <summary>Raised when Apply runs; true means ResultProfile is now set and valid.</summary>
    public event Action<bool>? Applied;

    public TimestampPickerViewModel(IEnumerable<string> sampleLines, FileType fileType)
    {
        _sampleLines = sampleLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        _fileType = fileType;

        SelectAnotherLineCommand = new RelayCommand(CycleSampleLine);
        ApplyCommand = new RelayCommand(Apply, () => CanApply);

        LoadPrimaryLine(0);
    }

    private void CycleSampleLine()
    {
        if (_sampleLines.Count == 0) return;
        LoadPrimaryLine((_primaryLineIndex + 1) % _sampleLines.Count);
    }

    private void LoadPrimaryLine(int index)
    {
        Tokens.Clear();
        OtherSampleLines.Clear();

        if (_sampleLines.Count == 0)
        {
            PrimaryLine = string.Empty;
            RecomputeCanApply();
            return;
        }

        _primaryLineIndex = index;
        PrimaryLine = _sampleLines[index];

        foreach (var token in TimestampTokenizer.Tokenize(PrimaryLine))
        {
            var vm = new TimestampTokenViewModel(token);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TimestampTokenViewModel.Role))
                    OnRoleChanged(vm);
                if (e.PropertyName is nameof(TimestampTokenViewModel.IsSelected) or nameof(TimestampTokenViewModel.Role))
                {
                    RecomputeCanApply();
                    RefreshAllAvailableRoles();
                }
            };
            Tokens.Add(vm);
        }

        for (int i = 0; i < _sampleLines.Count && OtherSampleLines.Count < 4; i++)
        {
            if (i != index) OtherSampleLines.Add(_sampleLines[i]);
        }

        RecomputeCanApply();
        RefreshAllAvailableRoles();
    }

    /// <summary>Recomputes every token's AvailableRoles from every other token's current Role -
    /// see TimestampTokenViewModel.RefreshAvailableRoles for what "available" means here.</summary>
    private void RefreshAllAvailableRoles()
    {
        foreach (var token in Tokens) token.RefreshAvailableRoles(Tokens);
    }

    private void OnRoleChanged(TimestampTokenViewModel changed)
    {
        if (changed.Role == TimestampTokenRole.None) return;

        if (changed.Role == TimestampTokenRole.DateTime)
        {
            // Sole DateTime token - clear every other token's role (and thus its selection).
            foreach (var other in Tokens.Where(t => t != changed && t.Role != TimestampTokenRole.None))
                other.Role = TimestampTokenRole.None;
            return;
        }

        // Assigning Date or Time: clear an existing DateTime tag elsewhere (can't combine with
        // a separate Date/Time), and clear any other token that already holds this same role
        // (only one Date total, one Time total).
        foreach (var other in Tokens.Where(t => t != changed))
        {
            if (other.Role == TimestampTokenRole.DateTime || other.Role == changed.Role)
                other.Role = TimestampTokenRole.None;
        }
    }

    private void RecomputeCanApply()
    {
        CanApply = Tokens.Any(t => t.Role != TimestampTokenRole.None);
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Apply()
    {
        var selected = Tokens.Where(t => t.Role != TimestampTokenRole.None)
            .OrderBy(t => t.Token.StartIndex).ToList();

        if (selected.Count == 0)
        {
            TestIsSuccess = false;
            TestMessage = "Select at least one chunk and give it a Date, Time, or Date+Time role.";
            return;
        }

        var pattern = BuildRegexPattern(PrimaryLine, selected);
        var profile = new TimestampProfile
        {
            Mode = TimestampLocationMode.LineStart,
            RegexPattern = pattern,
            FormatString = ManualFormatString ?? string.Empty,
            Description = "Manually selected from sample text"
        };

        var (success, message) = TimestampDetector.TestManualPattern(_sampleLines, _fileType, profile, ',');
        TestIsSuccess = success;
        TestMessage = message;

        if (success)
        {
            ResultProfile = profile;
        }

        Applied?.Invoke(success);
    }

    /// <summary>
    /// Builds a regex that reproduces the primary line's structure from the start of the line
    /// through the end of the last selected chunk: literal text (escaped) for everything that
    /// isn't a chunk, a generic character-class pattern (\d+ / [A-Za-z]+) for every chunk so
    /// other lines' differing digit/letter values still match, and named-group wrapping -
    /// (?&lt;date&gt;...), (?&lt;time&gt;...), or (?&lt;ts&gt;...) - around whichever chunk(s) the
    /// user tagged. TimestampDetector.ExtractTimestampText already knows how to combine
    /// separate date+time groups (see TimestampDetector.cs).
    /// </summary>
    private static string BuildRegexPattern(string line, List<TimestampTokenViewModel> orderedSelected)
    {
        var lastEnd = orderedSelected.Max(t => t.Token.StartIndex + t.Token.Length);
        var allTokens = TimestampTokenizer.Tokenize(line);
        var roleByStart = orderedSelected.ToDictionary(t => t.Token.StartIndex, t => t.Role);

        var sb = new StringBuilder("^");
        int cursor = 0;

        foreach (var token in allTokens)
        {
            if (token.StartIndex > lastEnd) break;

            if (token.StartIndex > cursor)
                sb.Append(Regex.Escape(line.Substring(cursor, token.StartIndex - cursor)));

            var classPattern = token.IsNumeric ? @"\d+" : "[A-Za-z]+";
            if (roleByStart.TryGetValue(token.StartIndex, out var role) && role != TimestampTokenRole.None)
            {
                var groupName = role switch
                {
                    TimestampTokenRole.Date => "date",
                    TimestampTokenRole.Time => "time",
                    _ => "ts"
                };
                sb.Append($"(?<{groupName}>{classPattern})");
            }
            else
            {
                sb.Append(classPattern);
            }

            cursor = token.StartIndex + token.Length;
            if (cursor >= lastEnd) break;
        }

        return sb.ToString();
    }
}
