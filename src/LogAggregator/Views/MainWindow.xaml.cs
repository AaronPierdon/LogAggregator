using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LogAggregator.Models;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly Brush _dropZoneDefaultBorder;
    private readonly Brush _dropZoneHoverBorder;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.WizardRequested += OnWizardRequested;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();

        _dropZoneDefaultBorder = (Brush)FindResource("Brush.Border");
        _dropZoneHoverBorder = (Brush)FindResource("Brush.AccentGreen");
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
