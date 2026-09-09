using System.Windows;
using System.Windows.Input;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

/// <summary>
/// The app's Settings window - what used to be a small dropdown Popup off the title bar's gear
/// icon is now a full window with two tabs: Interface (unchanged - the same ShowDropZone /
/// LogLineColorMode bindings the popup always had, just living here now) and Developer (new -
/// the mock log file generator, see MockDataGeneratorViewModel).
///
/// Non-modal, like WarningsWindow: there's nothing here to "finish" or "apply", and leaving it
/// open while dragging freshly-generated mock files onto the main window is a reasonable thing
/// to want to do.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

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
}
