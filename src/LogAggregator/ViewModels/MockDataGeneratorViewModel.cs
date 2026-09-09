using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LogAggregator.Common;
using LogAggregator.Models;
using LogAggregator.Services;
using Microsoft.Win32;

namespace LogAggregator.ViewModels;

/// <summary>
/// Backs the Settings window's Developer tab - "Generate Mock Log Files". Every bindable
/// property here maps onto a MockDataOptions field; GenerateAsync hands them to
/// MockLogGeneratorService, which does the actual work. Generated files have no special
/// integration with the app's own ingestion pipeline - they're just normal files afterward, to
/// be dragged onto the main window's drop zone (or added via "+ Add Source" / "Manage Log
/// Types") exactly like any real log file.
/// </summary>
public class MockDataGeneratorViewModel : ObservableObject
{
    private FileType _outputFormat = FileType.FlatText;
    private MockWildness _wildness = MockWildness.Standard;
    private MockUsagePreset _usagePreset = MockUsagePreset.SimpleExample;
    private int _fileCount = 3;
    private int _recordsPerFile = 250;
    private string _outputFolder;
    private string _statusMessage = "Click \"Generate Mock Log Files\" to write files to the folder below.";
    private bool _isGenerating;
    private bool _lastRunSucceeded;

    public FileType OutputFormat { get => _outputFormat; set => SetProperty(ref _outputFormat, value); }
    public MockWildness Wildness { get => _wildness; set => SetProperty(ref _wildness, value); }
    public MockUsagePreset UsagePreset { get => _usagePreset; set => SetProperty(ref _usagePreset, value); }

    /// <summary>Text-box-friendly view of FileCount - forwards a successful parse to the real
    /// (clamped) int property without ever writing back to this string, so the box never fights
    /// the user mid-keystroke.</summary>
    public string FileCountText
    {
        get => _fileCount.ToString();
        set { if (int.TryParse(value, out var n)) FileCount = n; }
    }

    public int FileCount { get => _fileCount; set => SetProperty(ref _fileCount, Math.Clamp(value, 1, 50)); }

    public string RecordsPerFileText
    {
        get => _recordsPerFile.ToString();
        set { if (int.TryParse(value, out var n)) RecordsPerFile = n; }
    }

    public int RecordsPerFile { get => _recordsPerFile; set => SetProperty(ref _recordsPerFile, Math.Clamp(value, 5, 200_000)); }

    public string OutputFolder { get => _outputFolder; set => SetProperty(ref _outputFolder, value); }

    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsGenerating { get => _isGenerating; private set => SetProperty(ref _isGenerating, value); }
    public bool LastRunSucceeded { get => _lastRunSucceeded; private set => SetProperty(ref _lastRunSucceeded, value); }

    public ICommand BrowseFolderCommand { get; }
    public AsyncRelayCommand GenerateCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    public MockDataGeneratorViewModel()
    {
        _outputFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LogAggregator", "MockLogs");

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, () => LastRunSucceeded && Directory.Exists(OutputFolder));
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to write mock log files",
            InitialDirectory = Directory.Exists(OutputFolder)
                ? OutputFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
        }
    }

    private async Task GenerateAsync()
    {
        IsGenerating = true;
        StatusMessage = "Generating...";
        try
        {
            var options = new MockDataOptions
            {
                OutputFormat = OutputFormat,
                Wildness = Wildness,
                UsagePreset = UsagePreset,
                FileCount = FileCount,
                RecordsPerFile = RecordsPerFile,
                OutputFolder = OutputFolder
            };

            var result = await MockLogGeneratorService.GenerateAsync(options).ConfigureAwait(true);
            var names = string.Join(", ", result.Files.Select(f => f.ServerName));

            StatusMessage = result.InjectedDefectCount > 0
                ? $"Generated {result.Files.Count} file(s), {result.TotalRecords:N0} records total ({result.InjectedDefectCount} intentional defect(s) mixed in), as: {names}. Drag them onto the main window (or use \"+ Add Source\") to try them out."
                : $"Generated {result.Files.Count} file(s), {result.TotalRecords:N0} records total, as: {names}. Drag them onto the main window (or use \"+ Add Source\") to try them out.";
            LastRunSucceeded = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Generation failed: {ex.Message}";
            LastRunSucceeded = false;
        }
        finally
        {
            IsGenerating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OpenOutputFolder()
    {
        if (!Directory.Exists(OutputFolder)) return;
        Process.Start(new ProcessStartInfo { FileName = OutputFolder, UseShellExecute = true });
    }
}
