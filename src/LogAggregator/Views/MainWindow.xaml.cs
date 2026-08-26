using System;
using System.ComponentModel;
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

    public MainWindow()
    {
        InitializeComponent();

        _database.Initialize();
        _viewModel = new MainViewModel(_database);

        DataContext = _viewModel;
        _viewModel.WizardRequested += OnWizardRequested;
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
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
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
    // Wizard flow
    // ===================================================================

    private void OnWizardRequested(SourceCardViewModel? existingCard)
    {
        var wizardViewModel = new WizardViewModel(existingCard?.Source);
        var window = new WizardWindow(wizardViewModel) { Owner = this };

        window.DeleteRequested += async () =>
        {
            window.Close();
            if (existingCard is not null)
                await _viewModel.RemoveSourceAsync(existingCard);
        };

        bool? finished = window.ShowDialog();

        if (finished == true)
        {
            var resultSource = wizardViewModel.BuildResult();
            _ = _viewModel.ApplyWizardResultAsync(existingCard, resultSource);
        }
    }
}
