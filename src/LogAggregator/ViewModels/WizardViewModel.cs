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
/// Drives the 4-step new/edit source wizard:
///   0. Files (drag &amp; drop or browse)  1. Auto-detected timestamp pattern (+ manual override
///   if needed)  2. Name  3. Display color
/// File type (CSV/Tab/FlatText) and the timestamp pattern are both detected automatically from
/// the real files the user picked - nothing is ever pasted by hand. If detection is genuinely
/// ambiguous (more than one place in the file looks like a timestamp), the user is asked to
/// pick which one; manual entry only appears if detection fails outright.
/// </summary>
public class WizardViewModel : ObservableObject
{
    public static readonly string[] PresetColors =
    {
        "#3DDC97", "#4C9BFF", "#E0A63D", "#E05C5C", "#B57DE0",
        "#3DC8E0", "#E0733D", "#7DE07E", "#E03D9C", "#9BA8E0"
    };

    private const int SampleLineCount = 30;

    private readonly string _sourceId;
    private readonly List<string> _filePaths;
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
    private string _toastMessage = string.Empty;
    private bool _toastVisible;

    public bool IsEditMode { get; }
    public TimestampProfile CurrentProfile { get; private set; } = new();
    public ObservableCollection<string> PendingFilePaths { get; } = new();
    public ObservableCollection<TimestampCandidate> Candidates { get; } = new();
    public IReadOnlyList<string> Presets => PresetColors;

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

    /// <summary>"Timestamp column" for single-column mode, or "Date column" once two-column
    /// mode is checked - never just a bare "column index" per the user's request.</summary>
    public string PrimaryColumnLabel => UseTwoColumnMode ? "Date column (0-based)" : "Timestamp column (0-based)";
    public string SecondColumnLabel => "Time column (0-based)";

    public bool ShowManualOverride
    {
        get => _showManualOverride;
        set => SetProperty(ref _showManualOverride, value);
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
            {
                OnPropertyChanged(nameof(PrimaryColumnLabel));
            }
        }
    }

    public string ManualTestMessage { get => _manualTestMessage; set => SetProperty(ref _manualTestMessage, value); }
    public bool ManualTestIsSuccess { get => _manualTestIsSuccess; set => SetProperty(ref _manualTestIsSuccess, value); }

    public string SelectedColorHex
    {
        get => _selectedColorHex;
        set => SetProperty(ref _selectedColorHex, value);
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

    /// <summary>True = user finished; false = user cancelled.</summary>
    public event Action<bool>? RequestClose;

    public WizardViewModel(LogSource? existing)
    {
        IsEditMode = existing is not null;
        _sourceId = existing?.Id ?? Guid.NewGuid().ToString();
        _name = existing?.Name ?? string.Empty;
        _detectedFileType = existing?.Type ?? FileType.FlatText;
        _selectedColorHex = existing?.DisplayColor ?? PresetColors[ColorRotation.NextColorIndex()];
        _filePaths = existing is not null ? new List<string>(existing.FilePaths) : new List<string>();
        foreach (var p in _filePaths) PendingFilePaths.Add(p);

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
                _filePaths.Remove(path);
                PendingFilePaths.Remove(path);
            }
        });
        PickPresetColorCommand = new RelayCommand(param =>
        {
            if (param is string hex) SelectedColorHex = hex;
        });
    }

    // ===================================================================
    // Step navigation
    // ===================================================================

    private void GoNext()
    {
        if (StepIndex == 0)
        {
            if (_filePaths.Count == 0)
            {
                ShowToast("Add at least one file before continuing.");
                return;
            }

            RunDetection();
            StepIndex = 1;
            return;
        }

        if (StepIndex == 1 && !_hasValidProfile)
        {
            ShowToast(IsAmbiguous
                ? "Pick which column is the timestamp before continuing."
                : "Enter and test a manual pattern before continuing.");
            return;
        }

        if (StepIndex == 2 && string.IsNullOrWhiteSpace(Name))
        {
            ShowToast("Enter a name for this source before continuing.");
            return;
        }

        if (StepIndex < 3) StepIndex++;
    }

    // ===================================================================
    // Files (drag & drop + browse)
    // ===================================================================

    private void BrowseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select log files",
            Multiselect = true,
            Filter = "All supported files (*.csv;*.txt;*.log;*.tsv;*.zip)|*.csv;*.txt;*.log;*.tsv;*.zip|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
            AddFiles(dialog.FileNames);
    }

    /// <summary>Adds files (or expands a dropped .zip into its contents) to the pending list.
    /// Called both by the Browse dialog and by drag &amp; drop.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        var expanded = FileDropHelper.ExpandDroppedPaths(paths, ShowToast);
        int added = 0;

        foreach (var path in expanded)
        {
            if (!_filePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _filePaths.Add(path);
                PendingFilePaths.Add(path);
                added++;
            }
        }

        if (added > 0) ShowToast($"{added} file(s) added ({_filePaths.Count} total).");
    }

    // ===================================================================
    // Detection
    // ===================================================================

    private void RunDetection()
    {
        var firstFile = _filePaths.First();
        _sampleLines = FileTypeDetector.ReadSampleLines(firstFile, SampleLineCount);
        DetectedFileType = FileTypeDetector.DetectFromLines(_sampleLines);

        var delimiter = DetectedFileType == FileType.TabDelimited ? '\t' : ',';
        var result = TimestampDetector.AutoDetectFromLines(_sampleLines, DetectedFileType, delimiter);

        DetectionStatus = result.Status;
        DetectionMessage = result.Message;

        Candidates.Clear();
        foreach (var c in result.Candidates) Candidates.Add(c);

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

    public LogSource BuildResult()
    {
        return new LogSource
        {
            Id = _sourceId,
            Name = Name.Trim(),
            Type = DetectedFileType,
            TimestampProfile = CurrentProfile,
            DisplayColor = SelectedColorHex,
            FilePaths = new List<string>(_filePaths),
            IsActive = true
        };
    }

    /// <summary>Tiny helper so successive "new source" wizards default to different preset
    /// colors instead of always starting green.</summary>
    private static class ColorRotation
    {
        private static int _counter = -1;
        public static int NextColorIndex() => System.Threading.Interlocked.Increment(ref _counter) % PresetColors.Length;
    }
}
