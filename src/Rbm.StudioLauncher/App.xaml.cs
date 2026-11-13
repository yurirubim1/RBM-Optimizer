using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rbm.StudioLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) => { MessageBox.Show(a.Exception.Message, "RBM Places", MessageBoxButton.OK, MessageBoxImage.Warning); a.Handled = true; };
        base.OnStartup(e);
    }
}

public sealed class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, int i => i > 0, string s => !string.IsNullOrWhiteSpace(s), null => false, _ => true };
        return (Invert ? !b : b) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Not : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}
