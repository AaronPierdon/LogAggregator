using System.Windows;
using System.Windows.Input;
using LogAggregator.Models;
using LogAggregator.ViewModels;

namespace LogAggregator.Views;

public partial class LogTypesWindow : Window
{
    private readonly LogTypesViewModel _viewModel;

    public LogTypesWindow(LogTypesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.EditorRequested += OnEditorRequested;
    }

    private void OnEditorRequested(LogType? existing)
    {
        var editorViewModel = new LogTypeEditorViewModel(existing);
        var window = new LogTypeEditorWindow(editorViewModel) { Owner = this };

        window.DeleteRequested += () =>
        {
            window.Close();
            if (existing is not null) _viewModel.DeleteCommand.Execute(existing);
        };

        bool? finished = window.ShowDialog();

        if (finished == true)
        {
            var result = editorViewModel.BuildResult();
            _viewModel.ApplyEditorResult(existing, result);
        }
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
