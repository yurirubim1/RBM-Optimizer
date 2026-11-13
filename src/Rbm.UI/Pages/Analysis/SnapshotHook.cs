using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rbm.UI.Pages.Analysis;

/// <summary>
/// Apoio à verificação visual automatizada: se a variável de ambiente RBM_UI_SNAPSHOT apontar para um .png,
/// renderiza a janela principal nesse arquivo (RenderTargetBitmap) alguns segundos após a página carregar.
/// Sem a variável, não faz nada. Funciona com a janela fora da tela (--offscreen), onde PrintWindow devolve preto.
/// </summary>
public static class SnapshotHook
{
    private static bool s_done;

    public static void Attach(FrameworkElement view)
    {
        var path = Environment.GetEnvironmentVariable("RBM_UI_SNAPSHOT");
        if (string.IsNullOrWhiteSpace(path) || s_done) return;
        view.Loaded += (_, _) =>
        {
            if (s_done) return;
            s_done = true;
            int delay = int.TryParse(Environment.GetEnvironmentVariable("RBM_UI_SNAPSHOT_DELAY"), out var d) ? d : 6000;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var win = Application.Current.MainWindow;
                    if (win?.Content is not FrameworkElement root) return;
                    var dpi = VisualTreeHelper.GetDpi(root);
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    var bg = new DrawingVisual();
                    using (var dc = bg.RenderOpen())
                        dc.DrawRectangle(win.Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    bmp.Render(bg);
                    bmp.Render(root);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var fs = File.Create(path);
                    enc.Save(fs);
                }
                catch (Exception ex) { App.Log(ex, "SnapshotHook"); }
            };
            t.Start();
        };
    }
}
