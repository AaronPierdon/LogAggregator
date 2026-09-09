using System;
using System.Linq;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogAggregator.Models;
using LogAggregator.Services;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

public partial class MainWindow : Window
{
    private readonly LogDatabase _database = new();
    private readonly MainViewModel _viewModel;
    private readonly Brush _dropZoneDefaultBorder;
    private readonly Brush _dropZoneHoverBorder;
    private ScrollViewer? _gridScrollViewer;
    private WarningsWindow? _warningsWindow;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();

        _database.Initialize();
        _viewModel = new MainViewModel(_database);

        DataContext = _viewModel;
        _viewModel.LogTypesRequested += OnLogTypesRequested;
        _viewModel.NewLogTypeRequestedForDrop += OnNewLogTypeRequestedForDrop;
        _viewModel.LogTypeSettingsRequested += OnLogTypeSettingsRequested;
        _viewModel.ViewWarningsRequested += OnViewWarningsRequested;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();

        _dropZoneDefaultBorder = (Brush)FindResource("Brush.Border");
        _dropZoneHoverBorder = (Brush)FindResource("Brush.AccentGreen");

        LogGrid.Loaded += LogGrid_Loaded;
    }

    // ===================================================================
    // Custom window chrome
    // ===================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeRestore_Click(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Single-instance, non-modal, same pattern as OnViewWarningsRequested below - re-clicking
        // the gear while Settings is already open just brings it to the front instead of piling
        // up a second window.
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var settingsViewModel = new SettingsViewModel(_viewModel);
        _settingsWindow = new SettingsWindow(settingsViewModel) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ===================================================================
    // Quick-add drop zone
    // ===================================================================

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        DropZoneBorder.BorderBrush = _dropZoneHoverBorder;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = _dropZoneDefaultBorder;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = _dropZoneDefaultBorder;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await _viewModel.QuickAddSourceFromDropAsync(paths);
    }

    // ===================================================================
    // Message column copy
    // ===================================================================

    private void CopyFullMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is LogBlock block)
        {
            Clipboard.SetText(block.FullText);
        }
    }

    // ===================================================================
    // Grid: SQL-driven sort on header click, scroll-triggered "load more"
    // ===================================================================

    private void LogGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _gridScrollViewer ??= FindVisualChild<ScrollViewer>(LogGrid);
        if (_gridScrollViewer is not null)
            _gridScrollViewer.ScrollChanged += GridScrollViewer_ScrollChanged;
    }

    private async void GridScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Fire "load more" once the user is within a couple of screens' worth of the bottom of
        // whatever is currently loaded. LoadMoreAsync no-ops safely if a load is already in
        // flight or everything matching the filter is already loaded.
        const double threshold = 400;
        if (e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) <= threshold)
        {
            await _viewModel.LoadMoreAsync();
        }
    }

    private async void LogGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;

        var key = DataGridColumnExtensions.GetSortColumnKey(e.Column);
        if (key is null || !Enum.TryParse<SortColumn>(key, out var column))
            return;

        // Clicking the already-sorted column toggles direction; clicking a different column
        // always starts ascending.
        bool descending = _viewModel.CurrentSortColumn == column && !_viewModel.CurrentSortDescending;

        foreach (var col in LogGrid.Columns) col.SortDirection = null;
        e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        await _viewModel.SetSortAsync(column, descending);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    // ===================================================================
    // Manage Log Types
    // ===================================================================

    private void OnLogTypesRequested()
    {
        var logTypesViewModel = new LogTypesViewModel(_viewModel.LogTypes, _viewModel.Sources, _database);
        // LogTypesViewModel edits LogTypes/Sources in place (they're the same collection
        // instances MainViewModel owns) rather than going through a result object, so all this
        // needs to do is persist - there's no "apply" step like the old wizard had.
        logTypesViewModel.ConfigChanged += () => _ = _viewModel.SaveConfigAsync();

        var window = new LogTypesWindow(logTypesViewModel) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>A card-level drop didn't match any existing LogType - the user picked
    /// "+ New Log Type..." from SourceCardViewModel's popup. Open the full editor pre-seeded
    /// with the dropped sample file(s) so detection runs against real content immediately, then
    /// bind the finished LogType to the card that requested it.</summary>
    private void OnNewLogTypeRequestedForDrop(SourceCardViewModel card, List<string> droppedPaths)
    {
        var editorViewModel = new LogTypeEditorViewModel(existing: null, initialSampleFiles: droppedPaths);
        var window = new LogTypeEditorWindow(editorViewModel) { Owner = this };

        bool? finished = window.ShowDialog();
        if (finished == true)
        {
            var result = editorViewModel.BuildResult();
            _ = _viewModel.CompleteNewLogTypeDropAsync(card, result);
        }
    }

    /// <summary>A chip's right-click menu asked to open the LogType editor directly for an
    /// existing LogType ("Log Type Settings..." or "Set Timestamp Pattern...") - bypasses the
    /// "Manage Log Types" list entirely and pre-seeds the editor with this binding's real files
    /// so detection runs immediately. Builds a transient LogTypesViewModel purely to reuse its
    /// already-tested ApplyEditorResult/DeleteCommand cascade logic (propagate name/color/icon/
    /// pattern changes to every card + the database, or cascade a delete) - mirrors
    /// OnLogTypesRequested/LogTypesWindow.OnEditorRequested exactly, just without showing the
    /// list window in between.</summary>
    private void OnLogTypeSettingsRequested(SourceLogType binding, LogType logType, bool forceManualPatternStep)
    {
        var logTypesViewModel = new LogTypesViewModel(_viewModel.LogTypes, _viewModel.Sources, _database);
        logTypesViewModel.ConfigChanged += () => _ = _viewModel.SaveConfigAsync();

        var initialSampleFiles = binding.FilePaths.Count > 0 ? binding.FilePaths : null;
        var editorViewModel = new LogTypeEditorViewModel(logType, initialSampleFiles, forceManualPatternStep);
        var window = new LogTypeEditorWindow(editorViewModel) { Owner = this };

        window.DeleteRequested += () =>
        {
            window.Close();
            logTypesViewModel.DeleteCommand.Execute(logType);
        };

        bool? finished = window.ShowDialog();
        if (finished == true)
        {
            var result = editorViewModel.BuildResult();
            logTypesViewModel.ApplyEditorResult(logType, result);
        }
    }

    // ===================================================================
    // Parse Warnings
    // ===================================================================

    /// <summary>Opens the Parse Warnings list. A chip's error badge passes its own binding/
    /// LogType to filter the list down to just that chip; the status bar's counter passes null
    /// for both to show everything. Not live-bound - it's a snapshot of Warnings at the moment
    /// this opens, with its own Refresh button, so a long sync running in the background can't
    /// yank rows out from under someone reading them.</summary>
    private void OnViewWarningsRequested(SourceLogType? filterBinding, LogType? filterLogType)
    {
        // Re-clicking a badge (or the status bar counter) while the window is already open
        // replaces its contents with a fresh snapshot for the new filter, rather than piling up
        // a new non-modal window per click.
        _warningsWindow?.Close();

        var sources = _viewModel.Sources.Select(c => c.Source).ToList();
        var warningsViewModel = new WarningsViewModel(_viewModel.Warnings, sources, _viewModel.LogTypes, filterBinding, filterLogType);

        _warningsWindow = new WarningsWindow(warningsViewModel) { Owner = this };
        _warningsWindow.Closed += (_, _) => _warningsWindow = null;
        _warningsWindow.Show();
    }
}
