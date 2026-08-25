using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

public partial class WizardWindow : Window
{
    private readonly WizardViewModel _viewModel;
    private readonly Brush _dropZoneDefaultBorder;
    private readonly Brush _dropZoneHoverBorder;

    /// <summary>Raised when the user clicks "Delete Source" in edit mode. The caller closes
    /// this window and removes the source.</summary>
    public event Action? DeleteRequested;

    public WizardWindow(WizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += finished =>
        {
            DialogResult = finished;
            Close();
        };

        _dropZoneDefaultBorder = (Brush)FindResource("Brush.Border");
        _dropZoneHoverBorder = (Brush)FindResource("Brush.AccentGreen");
    }

    // ===================================================================
    // Custom window chrome
    // ===================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ===================================================================
    // File drop zone
    // ===================================================================

    private void FileDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        FileDropZone.BorderBrush = _dropZoneHoverBorder;
    }

    private void FileDropZone_DragLeave(object sender, DragEventArgs e)
    {
        FileDropZone.BorderBrush = _dropZoneDefaultBorder;
    }

    private void FileDropZone_Drop(object sender, DragEventArgs e)
    {
        FileDropZone.BorderBrush = _dropZoneDefaultBorder;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        _viewModel.AddFiles(paths);
    }

    // ===================================================================
    // Delete
    // ===================================================================

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete this source and all of its loaded rows? This cannot be undone.",
            "Delete Source", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
            DeleteRequested?.Invoke();
    }
}
