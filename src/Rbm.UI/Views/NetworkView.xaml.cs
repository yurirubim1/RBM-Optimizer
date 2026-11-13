using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Rbm.UI.Views;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        // Tabelas internas têm rolagem própria: quando chegam ao fim (ou não rolam), a roda do mouse passa para a página.
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var inner = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (inner is null || ReferenceEquals(inner, Root)) return;
        bool canScroll = inner.ScrollableHeight > 0 && (e.Delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight : inner.VerticalOffset > 0);
        if (canScroll) return;
        e.Handled = true;
        Root.ScrollToVerticalOffset(Root.VerticalOffset - e.Delta);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ScrollViewer sv) return sv;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
