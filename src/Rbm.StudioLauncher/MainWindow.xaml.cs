using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Rbm.StudioLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private System.Windows.Forms.NotifyIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        // Ícone na bandeja: avisos do Windows quando um Claude termina ou precisa de você.
        try
        {
            var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/rbm.ico"))?.Stream;
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
                Text = "RBM Places", Visible = true,
            };
            _tray.Click += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
            _tray.BalloonTipClicked += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); _vm.ShowChatsCommand.Execute(null); };
        }
        catch { _tray = null; }

        _vm.Notify += (title, text) => Dispatcher.Invoke(() =>
        {
            _tray?.ShowBalloonTip(6000, title, text.Length > 180 ? text[..180] + "…" : text, System.Windows.Forms.ToolTipIcon.Info);
            if (!IsActive) FlashTaskbar();
        });
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.NeedsYou)) UpdateBadge();
            if (e.PropertyName == nameof(MainViewModel.SelectedChat)) ChatScroll.ScrollToEnd();
        };
        _vm.ChatMessages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
        Closed += (_, _) => { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } };
        SetupSnapshotMode();
    }

    /// <summary>
    /// Verificação visual automatizada: com RBM_UI_SNAPSHOT=arquivo.png a janela abre fora da tela, sem foco,
    /// navega para RBM_PLACES_VIEW (chats | folders | codesession | recent | group) e salva um PNG. Sem a variável, nada muda.
    /// </summary>
    private void SetupSnapshotMode()
    {
        var path = Environment.GetEnvironmentVariable("RBM_UI_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path)) return;
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = 0; ShowActivated = false; ShowInTaskbar = false;
        if (_tray is not null) _tray.Visible = false;
        var view = Environment.GetEnvironmentVariable("RBM_PLACES_VIEW") ?? "recent";
        int delay = int.TryParse(Environment.GetEnvironmentVariable("RBM_UI_SNAPSHOT_DELAY"), out var d) ? d : 12000;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (view == "chats") _vm.ShowChatsCommand.Execute(null);
            else if (view == "group" && _vm.Groups.FirstOrDefault() is { } g) await _vm.NavigateCommand.ExecuteAsync(g);
            else if (_vm.Sections.Concat(_vm.CodeSections).FirstOrDefault(s => s.Key == view) is { } s) await _vm.NavigateCommand.ExecuteAsync(s);
            await Task.Delay(3000);
            var root = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Media.DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg); bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            Close();
        };
        timer.Start();
    }

    /// <summary>Selo na barra de tarefas com o número de conversas que precisam de você.</summary>
    private void UpdateBadge()
    {
        Taskbar.Description = _vm.NeedsYou > 0 ? $"{_vm.NeedsYou} conversa(s) do Claude precisam de você" : "RBM Places";
        Taskbar.Overlay = _vm.NeedsYou > 0 ? BadgeImage(_vm.NeedsYou) : null;
    }

    private static System.Windows.Media.ImageSource BadgeImage(int n)
    {
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)), null, new Point(16, 16), 16, 16);
            var ft = new System.Windows.Media.FormattedText(n > 9 ? "9+" : n.ToString(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI Semibold"), n > 9 ? 15 : 19, System.Windows.Media.Brushes.Black, 1.0);
            dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    private void FlashTaskbar()
    {
        var info = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = new WindowInteropHelper(this).Handle, dwFlags = 3 | 12, uCount = 3, dwTimeout = 0 };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
