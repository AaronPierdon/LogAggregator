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
    /// and time (e.g. a compact "20240102150405" run, or the synthetic "whole detected
    /// timestamp" suggestion - see TimestampToken.IsComposite) - can only be assigned when it's
    /// the sole selected/tagged chunk.</summary>
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

    /// <summary>Bindable source for the per-token role ComboBox in TimestampPickerControl.</summary>
    public static IReadOnlyList<TimestampTokenRole> AvailableRoles { get; } =
        new[] { TimestampTokenRole.Date, TimestampTokenRole.Time, TimestampTokenRole.DateTime };

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
                // clicking - the user can still change it via the dropdown. The composite
                // suggestion token defaults straight to DateTime (it's a whole-blob match, not a
                // date-only/time-only chunk).
                Role = Token.IsComposite ? TimestampTokenRole.DateTime : Hint switch
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
///
/// Sample lines are ranked by TimestampDetector.FindLikelyTimestampSpan/token-hint density
/// before display (see ScoreLine) rather than trusted in raw file order - a plain "show line 0"
/// approach means a CSV/export file with a header row as its first line shows the user an
/// unhelpful, timestamp-free line by default. When the best-ranked line has a recognizable
/// leading-timestamp shape, SuggestedToken offers it as a single one-click "use this" button
/// instead of making the user select+tag every digit run individually.
/// </summary>
public class TimestampPickerViewModel : ObservableObject
{
    private readonly FileType _fileType;
    private readonly List<string> _sampleLines;
    private readonly List<int> _rankedIndices;
    private int _rankPosition;
    private string _primaryLine = string.Empty;
    private string _manualFormatString = string.Empty;
    private string _testMessage = string.Empty;
    private bool _testIsSuccess;
    private bool _canApply;
    private TimestampTokenViewModel? _suggestedToken;

    public ObservableCollection<TimestampTokenViewModel> Tokens { get; } = new();

    /// <summary>A few other sample lines shown read-only underneath, so the user can visually
    /// sanity-check that the shape they're picking really is consistent across the file.</summary>
    public ObservableCollection<string> OtherSampleLines { get; } = new();

    public string PrimaryLine
    {
        get => _primaryLine;
        private set => SetProperty(ref _primaryLine, value);
    }

    /// <summary>The single, prominent "we think this is your timestamp" suggestion for the
    /// current primary line - null when TimestampDetector.FindLikelyTimestampSpan didn't
    /// recognize a leading-timestamp shape on this line at all (the algorithm genuinely isn't
    /// sure, so it doesn't force a guess; the user falls back to the fine-grained token buttons
    /// below). Selecting it is mutually exclusive with selecting any of the fine-grained
    /// Tokens - see ClearFineGrainedSelection/ClearSuggestedSelection.</summary>
    public TimestampTokenViewModel? SuggestedToken
    {
        get => _suggestedToken;
        private set
        {
            if (SetProperty(ref _suggestedToken, value))
                OnPropertyChanged(nameof(HasSuggestion));
        }
    }

    /// <summary>Bindable convenience for XAML Visibility - true when SuggestedToken is non-null,
    /// i.e. TimestampDetector.FindLikelyTimestampSpan recognized a leading-timestamp shape on
    /// the current primary line.</summary>
    public bool HasSuggestion => SuggestedToken is not null;

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

        // Best-scoring line first, ties broken by original file order (OrderByDescending is a
        // stable sort) - see ScoreLine. "Try another sample line" then cycles through this same
        // ranked order, so it always moves toward "less obviously a timestamp" rather than just
        // wrapping through raw file order and possibly landing back on a header/blank-ish line.
        _rankedIndices = Enumerable.Range(0, _sampleLines.Count)
            .OrderByDescending(i => ScoreLine(_sampleLines[i]))
            .ToList();

        SelectAnotherLineCommand = new RelayCommand(CycleSampleLine);
        ApplyCommand = new RelayCommand(Apply, () => CanApply);

        _rankPosition = 0;
        LoadPrimaryLine(_rankedIndices.Count > 0 ? _rankedIndices[0] : 0);
    }

    /// <summary>Higher = more likely this line actually contains a real timestamp. Strongly
    /// prefers a line TimestampDetector recognizes a whole leading-timestamp shape on (the same
    /// regexes AutoDetectLineStartMultiLine votes across); falls back to counting how many
    /// individual chunks TimestampTokenizer hints as date/time-shaped, so even without a full
    /// recognized shape, a line with several date/time-looking chunks still outranks one with
    /// none (e.g. a column-header line full of plain words, which is the case this exists to
    /// avoid defaulting to).</summary>
    private static int ScoreLine(string line)
    {
        if (TimestampDetector.FindLikelyTimestampSpan(line) is { } span)
            return 1000 + span.Length;

        return TimestampTokenizer.Tokenize(line).Count(t => t.Hint != TimestampTokenHint.None);
    }

    private void CycleSampleLine()
    {
        if (_rankedIndices.Count == 0) return;
        _rankPosition = (_rankPosition + 1) % _rankedIndices.Count;
        LoadPrimaryLine(_rankedIndices[_rankPosition]);
    }

    private void LoadPrimaryLine(int index)
    {
        Tokens.Clear();
        OtherSampleLines.Clear();
        SuggestedToken = null;

        if (_sampleLines.Count == 0)
        {
            PrimaryLine = string.Empty;
            RecomputeCanApply();
            return;
        }

        PrimaryLine = _sampleLines[index];

        if (TimestampDetector.FindLikelyTimestampSpan(PrimaryLine) is { } span)
        {
            var compositeToken = new TimestampToken
            {
                Text = PrimaryLine.Substring(span.Start, span.Length),
                StartIndex = span.Start,
                Length = span.Length,
                IsComposite = true
            };
            var suggestedVm = new TimestampTokenViewModel(compositeToken);
            suggestedVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TimestampTokenViewModel.IsSelected) && suggestedVm.IsSelected)
                    ClearFineGrainedSelection();
                if (e.PropertyName is nameof(TimestampTokenViewModel.IsSelected) or nameof(TimestampTokenViewModel.Role))
                    RecomputeCanApply();
            };
            SuggestedToken = suggestedVm;
        }

        foreach (var token in TimestampTokenizer.Tokenize(PrimaryLine))
        {
            var vm = new TimestampTokenViewModel(token);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TimestampTokenViewModel.IsSelected) && vm.IsSelected)
                    ClearSuggestedSelection();
                if (e.PropertyName == nameof(TimestampTokenViewModel.Role))
                    OnRoleChanged(vm);
                if (e.PropertyName is nameof(TimestampTokenViewModel.IsSelected) or nameof(TimestampTokenViewModel.Role))
                    RecomputeCanApply();
            };
            Tokens.Add(vm);
        }

        // Ranked order (best-looking-for-a-timestamp first), skipping whichever line is
        // currently primary - matches "Try another sample line"'s own cycling order, so what's
        // shown here previews where the button goes next.
        foreach (var i in _rankedIndices)
        {
            if (OtherSampleLines.Count >= 4) break;
            if (i != index) OtherSampleLines.Add(_sampleLines[i]);
        }

        RecomputeCanApply();
    }

    /// <summary>Deselects every fine-grained Tokens entry - called when the user selects
    /// SuggestedToken instead, since the two are mutually exclusive (a composite whole-blob
    /// selection can't be combined with individually-tagged chunks).</summary>
    private void ClearFineGrainedSelection()
    {
        foreach (var t in Tokens.Where(t => t.IsSelected))
            t.IsSelected = false;
    }

    /// <summary>Deselects SuggestedToken - called when the user selects a fine-grained token
    /// instead (see ClearFineGrainedSelection).</summary>
    private void ClearSuggestedSelection()
    {
        if (SuggestedToken is { IsSelected: true } suggested)
            suggested.IsSelected = false;
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
        CanApply = (SuggestedToken?.IsSelected ?? false) || Tokens.Any(t => t.Role != TimestampTokenRole.None);
        (ApplyCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Apply()
    {
        List<TimestampTokenViewModel> selected;

        if (SuggestedToken is { IsSelected: true })
        {
            selected = new List<TimestampTokenViewModel> { SuggestedToken };
        }
        else
        {
            selected = Tokens.Where(t => t.Role != TimestampTokenRole.None)
                .OrderBy(t => t.Token.StartIndex).ToList();
        }

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
    /// isn't a chunk, a generic character-class pattern (\d+ / [A-Za-z]+) for every fine-grained
    /// chunk so other lines' differing digit/letter values still match, and named-group wrapping
    /// - (?&lt;date&gt;...), (?&lt;time&gt;...), or (?&lt;ts&gt;...) - around whichever chunk(s)
    /// the user tagged. TimestampDetector.ExtractTimestampText already knows how to combine
    /// separate date+time groups (see TimestampDetector.cs).
    ///
    /// The single-composite-token case (the user clicked SuggestedToken rather than tagging
    /// fine-grained chunks by hand) is handled separately: that token's span isn't one atomic
    /// digit/letter run the way every other token is - it's a whole "2026-08-08 14:23:05 -0400"-
    /// shaped blob including internal punctuation - so a single \d+/[A-Za-z]+ class can't
    /// represent it. BuildClassPatternForRange reproduces its exact digit/letter-run shape
    /// (reusing the same technique this method uses for the rest of the line) and the whole
    /// result is wrapped in one (?&lt;ts&gt;...) group - deliberately just one group, so there's
    /// no risk of the multiple-same-named-group pitfall a naive "tag every underlying chunk as
    /// Time" auto-fill would hit (.NET keeps only the *last* capture under a repeated group
    /// name, which would silently drop everything but the seconds digits).
    /// </summary>
    private static string BuildRegexPattern(string line, List<TimestampTokenViewModel> orderedSelected)
    {
        if (orderedSelected.Count == 1 && orderedSelected[0].Token.IsComposite)
        {
            var span = orderedSelected[0].Token;
            var prefix = Regex.Escape(line.Substring(0, span.StartIndex));
            var interior = BuildClassPatternForRange(line, span.StartIndex, span.StartIndex + span.Length);
            return $"^{prefix}(?<ts>{interior})";
        }

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

    /// <summary>Reproduces [start, end)'s exact digit/letter-run shape as a regex fragment:
    /// \d+ or [A-Za-z]+ for each atomic run, Regex.Escape'd literal text for every gap between
    /// them (punctuation, whitespace, brackets). Used by BuildRegexPattern's composite-token
    /// case above to build the interior of a single (?&lt;ts&gt;...) group spanning a whole
    /// detected timestamp blob, rather than just one atomic token.</summary>
    private static string BuildClassPatternForRange(string line, int start, int end)
    {
        var sb = new StringBuilder();
        int cursor = start;

        foreach (var token in TimestampTokenizer.Tokenize(line))
        {
            if (token.StartIndex < start) continue;
            if (token.StartIndex >= end) break;

            if (token.StartIndex > cursor)
                sb.Append(Regex.Escape(line.Substring(cursor, token.StartIndex - cursor)));

            sb.Append(token.IsNumeric ? @"\d+" : "[A-Za-z]+");
            cursor = token.StartIndex + token.Length;
        }

        if (cursor < end)
            sb.Append(Regex.Escape(line.Substring(cursor, end - cursor)));

        return sb.ToString();
    }
}
