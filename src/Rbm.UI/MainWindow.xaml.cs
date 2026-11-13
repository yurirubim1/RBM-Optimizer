using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Rbm.UI.Infrastructure;

namespace Rbm.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
                UpdateStatusTexts(vm);
            }
        };
        Pages.Analysis.SnapshotHook.Attach(this); // captura em PNG só quando RBM_UI_SNAPSHOT está definida
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        // Modo de teste visual: abre fora da tela, sem foco e sem aparecer na barra de tarefas.
        // Builds de desenvolvimento (pasta RbmOptimizer.Build) SEMPRE abrem fora da tela, para não atrapalhar
        // quem usa o PC durante o desenvolvimento. Para ver na tela: defina RBM_DEV_VISIBLE=1.
        bool devBuild = AppContext.BaseDirectory.Contains("RbmOptimizer.Build", StringComparison.OrdinalIgnoreCase)
                        && Environment.GetEnvironmentVariable("RBM_DEV_VISIBLE") != "1";
        if (devBuild || Environment.GetCommandLineArgs().Contains("--offscreen"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = 0;
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowState = WindowState.Normal;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        if (e.PropertyName is nameof(MainViewModel.MonitoringActive) or nameof(MainViewModel.AgentConnected)) UpdateStatusTexts(vm);
        if (e.PropertyName == nameof(MainViewModel.CurrentItem)) SyncNavSelection(vm.CurrentItem?.Key);
    }

    private void UpdateStatusTexts(MainViewModel vm)
    {
        MonText.Text = Loc.T(vm.MonitoringActive ? "status.monitoring" : "status.paused");
        AgentText.Text = Loc.T(vm.AgentConnected ? "status.agentOn" : "status.agentOff");
    }

    private readonly List<RadioButton> _navButtons = new();
    private void NavItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && !_navButtons.Contains(rb))
        {
            _navButtons.Add(rb);
            if (DataContext is MainViewModel vm && (string)rb.Tag == vm.CurrentItem?.Key) rb.IsChecked = true;
        }
    }

    private void SyncNavSelection(string? key)
    {
        foreach (var rb in _navButtons) rb.IsChecked = (string)rb.Tag == key;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && DataContext is MainViewModel vm) vm.Navigate(key);
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
