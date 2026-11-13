using System.Windows.Controls;
using System.Windows.Input;
using Rbm.UI.ViewModels;

namespace Rbm.UI.Views;

public partial class SpaceView : UserControl
{
    public SpaceView() => InitializeComponent();

    /// <summary>Duplo clique numa pasta da lista entra nela (mesma ação do clique no mapa).</summary>
    private void ItemsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SpaceViewModel vm || vm.SelectedRow?.Node is not { CanDrill: true } node) return;
        if (e.OriginalSource is System.Windows.DependencyObject d && ItemsControl.ContainerFromElement(ItemsGrid, d) is not DataGridRow) return;
        vm.DrillCommand.Execute(node);
        e.Handled = true;
    }
}
