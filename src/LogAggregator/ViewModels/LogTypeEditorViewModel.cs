using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;
using Microsoft.Win32;

namespace LogAggregator.ViewModels;

/// <summary>
/// Drives the 4-step Add/Edit LogType editor - the evolution of the old per-Source wizard, now
/// scoped to "how do I parse this kind of log" instead of a Source (Sources no longer own a file
/// format or timestamp pattern themselves - see LogSource.cs/LogType.cs):
///   0. Sample files (for testing detection only - not persisted; LogTypes have no files of
///      their own, files live on each Source's binding)
///   1. Auto-detected timestamp pattern, with the interactive region picker (see
///      TimestampPickerViewModel) as the fallback when detection is ambiguous/failed, plus a
///      manual regex/column override for delimited types
///   2. Name
///   3. Color + icon + display mode
/// </summary>
public class LogTypeEditorViewModel : ObservableObject
{
    public static string[] PresetColors => ColorPresets.Colors;

    private const int SampleLineCount = 30;

    private readonly string _logTypeId;
    private readonly List<string> _sampleFilePaths = new();
    private List<string> _sampleLines = new();

    private int _stepIndex;
    private string _name;
    private FileType _detectedFileType;
    private DetectionStatus _detectionStatus = DetectionStatus.Pending;
    private string _detectionMessage = string.Empty;
    private TimestampCandidate? _selectedCandidate;
    private bool _hasValidProfile;
    private bool _showManualOverride;
    private string _manualFormatString = string.Empty;
    private string _manualRegexPattern = string.Empty;
    private int _manualColumnIndex = -1;
    private int _manualSecondColumnIndex = -1;
    private bool _useTwoColumnMode;
    private string _manualTestMessage = string.Empty;
    private bool _manualTestIsSuccess;
    private string _selectedColorHex;
    private string _selectedIconGlyph;
    private LogTypeDisplayMode _displayMode;
    private TimestampProfile _currentProfile = new();
    private TimestampPickerViewModel? _timestampPicker;
    private string _toastMessage = string.Empty;
    private bool _toastVisible;

    public bool IsEditMode { get; }
    public TimestampProfile CurrentProfile
    {
        get => _currentProfile;
        private set => SetProperty(ref _currentProfile, value);
    }

    public ObservableCollection<string> PendingFilePaths { get; } = new();
    public ObservableCollection<TimestampCandidate> Candidates { get; } = new();
    public IReadOnlyList<string> Presets => PresetColors;
    public IReadOnlyList<(string Glyph, string Label)> IconChoices => LogType.IconChoices;

    public int StepIndex
    {
        get => _stepIndex;
        private set
        {
            if (SetProperty(ref _stepIndex, value))
            {
                OnPropertyChanged(nameof(IsFirstStep));
                OnPropertyChanged(nameof(IsLastStep));
            }
        }
    }

    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == 3;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public FileType DetectedFileType
    {
        get => _detectedFileType;
        private set
        {
            if (SetProperty(ref _detectedFileType, value))
                OnPropertyChanged(nameof(DetectedFileTypeLabel));
        }
    }

    public string DetectedFileTypeLabel => DetectedFileType switch
    {
        FileType.CSV => "CSV",
        FileType.TabDelimited => "Tab-delimited",
        _ => "Flat text"
    };

    public DetectionStatus DetectionStatus
    {
        get => _detectionStatus;
        private set
        {
            if (SetProperty(ref _detectionStatus, value))
            {
                OnPropertyChanged(nameof(IsConfident));
                OnPropertyChanged(nameof(IsAmbiguous));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(ShowTimestampPicker));
            }
        }
    }

    public bool IsConfident => DetectionStatus == DetectionStatus.Confident;
    public bool IsAmbiguous => DetectionStatus == DetectionStatus.Ambiguous;
    public bool IsFailed => DetectionStatus == DetectionStatus.Failed;

    public string DetectionMessage
    {
        get => _detectionMessage;
        private set => SetProperty(ref _detectionMessage, value);
    }

    public TimestampCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value) && value is not null)
            {
                CurrentProfile = value.Profile;
                _hasValidProfile = true;
            }
        }
    }

    public bool IsDelimitedType => DetectedFileType != FileType.FlatText;

    /// <summary>The interactive region picker - only meaningful for FlatText LogTypes, and only
    /// shown once detection has actually failed (or the user opts into "pick manually" on an
    /// ambiguous FlatText result). Delimited types keep the existing column-index override,
    /// since "which column" is already a simple, unambiguous choice there.</summary>
    public TimestampPickerViewModel? TimestampPicker
    {
        get => _timestampPicker;
        private set => SetProperty(ref _timestampPicker, value);
    }

    /// <summary>Was IsFailed || (IsAmbiguous && ShowManualOverride) - i.e. ShowManualOverride
    /// only mattered when detection was Ambiguous, because the only UI that ever set it true was
    /// the "I'll pick it myself" button, which XAML only shows in the Ambiguous case. Broadened
    /// to just IsFailed || ShowManualOverride so a chip's right-click "Set Timestamp Pattern..."
    /// (which forces ShowManualOverride true unconditionally - see the constructor) also opens
    /// the picker when detection against this binding's real files comes back Confident, which
    /// is the common case: the binding already has working files, the user just wants to point
    /// at a different chunk of the line than what auto-detect picked. Behaviorally identical to
    /// before for every path that doesn't force it - RunDetection() still explicitly resets
    /// ShowManualOverride to false on Confident, so nothing here opens the picker on its own.</summary>
    public bool ShowTimestampPicker => !IsDelimitedType && (IsFailed || ShowManualOverride);

    public string PrimaryColumnLabel => UseTwoColumnMode ? "Date column (0-based)" : "Timestamp column (0-based)";
    public string SecondColumnLabel => "Time column (0-based)";

    public bool ShowManualOverride
    {
        get => _showManualOverride;
        set
        {
            if (SetProperty(ref _showManualOverride, value))
                OnPropertyChanged(nameof(ShowTimestampPicker));
        }
    }

    public string ManualFormatString { get => _manualFormatString; set => SetProperty(ref _manualFormatString, value); }
    public string ManualRegexPattern { get => _manualRegexPattern; set => SetProperty(ref _manualRegexPattern, value); }
    public int ManualColumnIndex { get => _manualColumnIndex; set => SetProperty(ref _manualColumnIndex, value); }
    public int ManualSecondColumnIndex { get => _manualSecondColumnIndex; set => SetProperty(ref _manualSecondColumnIndex, value); }

    public bool UseTwoColumnMode
    {
        get => _useTwoColumnMode;
        set
        {
            if (SetProperty(ref _useTwoColumnMode, value))
                OnPropertyChanged(nameof(PrimaryColumnLabel));
        }
    }

    public string ManualTestMessage { get => _manualTestMessage; set => SetProperty(ref _manualTestMessage, value); }
    public bool ManualTestIsSuccess { get => _manualTestIsSuccess; set => SetProperty(ref _manualTestIsSuccess, value); }

    public string SelectedColorHex
    {
        get => _selectedColorHex;
        set => SetProperty(ref _selectedColorHex, value);
    }

    public string SelectedIconGlyph
    {
        get => _selectedIconGlyph;
        set => SetProperty(ref _selectedIconGlyph, value);
    }

    public LogTypeDisplayMode DisplayMode
    {
        get => _displayMode;
        set => SetProperty(ref _displayMode, value);
    }

    public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
    public bool ToastVisible { get => _toastVisible; set => SetProperty(ref _toastVisible, value); }

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand ApplyManualCommand { get; }
    public ICommand BrowseFilesCommand { get; }
    public ICommand RemoveFileCommand { get; }
    public ICommand PickPresetColorCommand { get; }
    public ICommand PickIconCommand { get; }
    public ICommand ShowRegionPickerCommand { get; }

    /// <summary>True = user finished; false = user cancelled.</summary>
    public event Action<bool>? RequestClose;

    /// <summary>
    /// </summary>
    /// <param name="existing">Null to create a new LogType; non-null to edit one in place.</param>
    /// <param name="initialSampleFiles">Pre-seeds the "sample files" step - used by the
    /// quick-create flow from a Source card's drop popup, so the file(s) the user just dropped
    /// become the sample data immediately instead of asking them to browse again. When
    /// provided, detection runs immediately and the editor opens straight on step 1.</param>
    /// <param name="forceManualPatternStep">When true (and <paramref name="initialSampleFiles"/>
    /// produced at least one usable sample line), forces the manual timestamp picker open right
    /// after detection - used by a chip's right-click "Set Timestamp Pattern..." so the user
    /// lands straight on the picker instead of whatever DetectionStatus happened to come back.</param>
    public LogTypeEditorViewModel(LogType? existing, IEnumerable<string>? initialSampleFiles = null, bool forceManualPatternStep = false)
    {
        IsEditMode = existing is not null;
        _logTypeId = existing?.Id ?? Guid.NewGuid().ToString();
        _name = existing?.Name ?? string.Empty;
        _detectedFileType = existing?.Format ?? FileType.FlatText;
        _selectedColorHex = existing?.ColorHex ?? ColorPresets.NextColor();
        _selectedIconGlyph = existing?.IconGlyph ?? LogType.IconChoices[0].Glyph;
        _displayMode = existing?.DisplayMode ?? LogTypeDisplayMode.Both;

        if (existing is not null)
        {
            CurrentProfile = existing.TimestampProfile.Clone();
            _hasValidProfile = true;
        }

        NextCommand = new RelayCommand(GoNext);
        BackCommand = new RelayCommand(() => StepIndex--, () => StepIndex > 0);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        FinishCommand = new RelayCommand(Finish);
        ApplyManualCommand = new RelayCommand(ApplyManual);
        BrowseFilesCommand = new RelayCommand(BrowseFiles);
        RemoveFileCommand = new RelayCommand(param =>
        {
            if (param is string path)
            {
                _sampleFilePaths.Remove(path);
                PendingFilePaths.Remove(path);
            }
        });
        PickPresetColorCommand = new RelayCommand(param =>
        {
            if (param is string hex) SelectedColorHex = hex;
        });
        PickIconCommand = new RelayCommand(param =>
        {
            if (param is string glyph) SelectedIconGlyph = glyph;
        });
        ShowRegionPickerCommand = new RelayCommand(() => ShowManualOverride = true);

        if (initialSampleFiles is not null)
        {
            AddFiles(initialSampleFiles);
            if (_sampleFilePaths.Count > 0)
            {
                RunDetection();
                StepIndex = 1;
                if (forceManualPatternStep) ShowManualOverride = true;
            }
        }
    }

    // ===================================================================
    // Step navigation
    // ===================================================================

    private void GoNext()
    {
        if (StepIndex == 0)
        {
            if (_sampleFilePaths.Count == 0)
            {
                ShowToast("Add at least one sample file before continuing - it's only used to test detection, not saved.");
                return;
            }

            RunDetection();
            StepIndex = 1;
            return;
        }

        if (StepIndex == 1 && !_hasValidProfile)
        {
            ShowToast(IsAmbiguous
                ? "Pick which part is the timestamp before continuing."
                : "Select and confirm the timestamp chunks (or enter a manual pattern) before continuing.");
            return;
        }

        if (StepIndex == 2 && string.IsNullOrWhiteSpace(Name))
        {
            ShowToast("Enter a name for this LogType before continuing.");
            return;
        }

        if (StepIndex < 3) StepIndex++;
    }

    // ===================================================================
    // Sample files (test data only - never persisted onto the LogType)
    // ===================================================================

    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select sample log file(s) to test detection against",
            Multiselect = true,
            Filter = "All supported files (*.csv;*.txt;*.log;*.tsv;*.zip)|*.csv;*.txt;*.log;*.tsv;*.zip|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            AddFiles(dialog.FileNames);
    }

    /// <summary>Adds files (or expands a dropped .zip into its contents) to the pending sample
    /// list. Called both by the Browse dialog and by drag &amp; drop.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var expanded = FileDropHelper.ExpandDroppedPaths(paths, ShowToast);
        int added = 0;

        foreach (var path in expanded)
        {
            if (!_sampleFilePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _sampleFilePaths.Add(path);
                PendingFilePaths.Add(path);
                added++;
            }
        }

        if (added > 0) ShowToast($"{added} sample file(s) added ({_sampleFilePaths.Count} total).");
    }

    // ===================================================================
    // Detection
    // ===================================================================

    private void RunDetection()
    {
        var firstFile = _sampleFilePaths.First();
        _sampleLines = FileTypeDetector.ReadSampleLines(firstFile, SampleLineCount);
        DetectedFileType = FileTypeDetector.DetectFromLines(_sampleLines);

        var delimiter = DetectedFileType == FileType.TabDelimited ? '\t' : ',';
        var result = TimestampDetector.AutoDetectFromLines(_sampleLines, DetectedFileType, delimiter);

        DetectionStatus = result.Status;
        DetectionMessage = result.Message;

        Candidates.Clear();
        foreach (var c in result.Candidates) Candidates.Add(c);

        TimestampPicker = !IsDelimitedType ? new TimestampPickerViewModel(_sampleLines, DetectedFileType) : null;
        if (TimestampPicker is not null)
        {
            TimestampPicker.Applied += success =>
            {
                if (success && TimestampPicker.ResultProfile is not null)
                {
                    CurrentProfile = TimestampPicker.ResultProfile;
                    _hasValidProfile = true;
                    ShowToast("Timestamp selection applied.");
                }
            };
        }

        switch (result.Status)
        {
            case DetectionStatus.Confident:
                CurrentProfile = result.Candidates[0].Profile;
                _hasValidProfile = true;
                ShowManualOverride = false;
                break;

            case DetectionStatus.Ambiguous:
                _hasValidProfile = false;
                SelectedCandidate = null;
                ShowManualOverride = false;
                break;

            case DetectionStatus.Failed:
            default:
                _hasValidProfile = false;
                ShowManualOverride = true;
                break;
        }

        OnPropertyChanged(nameof(ShowTimestampPicker));

        // Suggest a name from the first file, but only if the user hasn't typed one yet.
        if (string.IsNullOrWhiteSpace(Name))
            Name = Path.GetFileNameWithoutExtension(firstFile).Replace('_', ' ').Replace('-', ' ');
    }

    private void ApplyManual()
    {
        var profile = new TimestampProfile { FormatString = ManualFormatString };
        var delimiter = DetectedFileType == FileType.TabDelimited ? '\t' : ',';

        if (DetectedFileType == FileType.FlatText)
        {
            profile.Mode = TimestampLocationMode.LineStart;
            profile.RegexPattern = ManualRegexPattern ?? string.Empty;
        }
        else
        {
            profile.Mode = UseTwoColumnMode ? TimestampLocationMode.DelimitedTwoColumn : TimestampLocationMode.DelimitedColumn;
            profile.ColumnIndex = ManualColumnIndex;
            profile.SecondColumnIndex = ManualSecondColumnIndex;
        }

        var (success, message) = TimestampDetector.TestManualPattern(_sampleLines, DetectedFileType, profile, delimiter);
        ManualTestIsSuccess = success;
        ManualTestMessage = message;

        if (success)
        {
            profile.Description = DetectedFileType == FileType.FlatText
                ? "Manually configured leading-timestamp pattern"
                : UseTwoColumnMode
                    ? $"Manually configured columns {ManualColumnIndex} + {ManualSecondColumnIndex}"
                    : $"Manually configured column {ManualColumnIndex}";

            CurrentProfile = profile;
            _hasValidProfile = true;
            ShowToast("Manual pattern applied successfully.");
        }
        else
        {
            _hasValidProfile = false;
            ShowToast("Manual pattern failed - adjust and try again.");
        }
    }

    private void Finish() => RequestClose?.Invoke(true);

    private void ShowToast(string message)
    {
        ToastMessage = message;
        ToastVisible = true;
    }

    public LogType BuildResult()
    {
        return new LogType
        {
            Id = _logTypeId,
            Name = Name.Trim(),
            Format = DetectedFileType,
            TimestampProfile = CurrentProfile,
            ColorHex = SelectedColorHex,
            IconGlyph = SelectedIconGlyph,
            DisplayMode = DisplayMode
        };
    }
}
