using System.ComponentModel;
using System.Windows;
using Rbm.UI.ViewModels;

namespace Rbm.UI.Views;

public partial class HistoryView : System.Windows.Controls.UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
            {
                vm.PropertyChanged += OnVm;
                ShowTab(vm.Tab);
            }
        };
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.Tab) && sender is HistoryViewModel vm) ShowTab(vm.Tab);
    }

    private void ShowTab(string tab)
    {
        PanelChanges.Visibility = tab == "changes" ? Visibility.Visible : Visibility.Collapsed;
        PanelDrift.Visibility = tab == "drift" ? Visibility.Visible : Visibility.Collapsed;
        PanelRestore.Visibility = tab == "restore" ? Visibility.Visible : Visibility.Collapsed;
        PanelImport.Visibility = tab == "import" ? Visibility.Visible : Visibility.Collapsed;
        PanelAudit.Visibility = tab == "audit" ? Visibility.Visible : Visibility.Collapsed;
        TabChanges.IsChecked = tab == "changes"; TabDrift.IsChecked = tab == "drift"; TabRestore.IsChecked = tab == "restore";
        TabImport.IsChecked = tab == "import"; TabAudit.IsChecked = tab == "audit";
    }
}
