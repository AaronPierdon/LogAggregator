using System.Windows.Input;
using LogAggregator.Common;

namespace LogAggregator.ViewModels;

public enum SettingsTab
{
    Interface,
    Developer
}

/// <summary>
/// Backs the Settings window - a thin shell around two independent panes: the existing
/// Interface preferences (bound straight through to MainViewModel, unchanged from when these
/// lived in the title bar's Settings popup - see ShowDropZone/LogLineColorMode there) and the
/// new Developer pane (mock log data generation, bound to its own MockDataGeneratorViewModel).
/// Deliberately owns only which tab is selected, not any of the settings themselves.
/// </summary>
public class SettingsViewModel : ObservableObject
{
    private SettingsTab _selectedTab = SettingsTab.Interface;

    public MainViewModel MainViewModel { get; }
    public MockDataGeneratorViewModel Developer { get; }

    public SettingsTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            OnPropertyChanged(nameof(IsInterfaceTab));
            OnPropertyChanged(nameof(IsDeveloperTab));
        }
    }

    public bool IsInterfaceTab => SelectedTab == SettingsTab.Interface;
    public bool IsDeveloperTab => SelectedTab == SettingsTab.Developer;

    public ICommand SelectInterfaceTabCommand { get; }
    public ICommand SelectDeveloperTabCommand { get; }

    public SettingsViewModel(MainViewModel mainViewModel)
    {
        MainViewModel = mainViewModel;
        Developer = new MockDataGeneratorViewModel();

        SelectInterfaceTabCommand = new RelayCommand(() => SelectedTab = SettingsTab.Interface);
        SelectDeveloperTabCommand = new RelayCommand(() => SelectedTab = SettingsTab.Developer);
    }
}
