using System.Windows;
using System.Windows.Input;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

/// <summary>
/// The "error chip" ask made browsable: lists every parse warning raised this session (or just
/// the ones for one chip, when opened from a badge), so the user can see which files or lines
/// didn't match a LogType's timestamp pattern - including the worst case, a file that produced
/// no blocks at all - instead of only ever seeing the first warning's summary text.
///
/// Non-modal (Show, not ShowDialog) - unlike the other secondary windows in this app, there's
/// nothing here to "finish" or "apply"; the user should be able to leave it open, glance back
/// and forth to the main window, and hit Refresh after fixing something..
/// </summary>
public partial class WarningsWindow : Window
{
    public WarningsWindow(WarningsViewModel viewModel)
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
