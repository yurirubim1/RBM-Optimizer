using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Hardware;
using Rbm.Processes;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.Pages.Hardware;

/// <summary>Item de legenda de gráfico (cor + nome + valor atual).</summary>
public sealed record LegendItem(string Name, string Value, Brush Brush, string? Tip = null);

/// <summary>Utilitários comuns das páginas de hardware.</summary>
public static class Hw
{
    public const string Unavailable = "indisponível";

    public static Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static readonly Brush[] Palette = new[]
    {
        "#22D3EE", "#2F7BFF", "#8B5CF6", "#22C55E", "#F59E0B", "#EF4444", "#38BDF8", "#EC4899",
        "#A3E635", "#F97316", "#14B8A6", "#E879F9", "#FACC15", "#60A5FA", "#F472B6", "#4ADE80",
    }.Select(h => (Brush)ChartFreeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)))).ToArray();

    private static T ChartFreeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static Brush Pal(int i) => Palette[((i % Palette.Length) + Palette.Length) % Palette.Length];

    public static ChartSeries Line(string name, double[] values, Brush stroke, bool fill = false) =>
        new() { Name = name, Values = values, Stroke = stroke, Fill = fill };

    public static MainViewModel Main => App.Services.GetRequiredService<MainViewModel>();

    /// <summary>Motivo pelo qual sensores (temperatura/potência) podem estar indisponíveis.</summary>
    public static string SensorReason()
    {
        try
        {
            var settings = App.Services.GetService<ISettingsStore>();
            if (settings is not null && !settings.Get("sensors.lhm", true))
                return "Sensores do LibreHardwareMonitor estão desativados em Configurações.";
            var lhm = App.Services.GetService<LhmSensorProvider>();
            if (lhm is null) return "Provedor de sensores não registrado.";
            return string.IsNullOrWhiteSpace(lhm.StatusMessage) ? "Status dos sensores desconhecido." : lhm.StatusMessage;
        }
        catch { return "Status dos sensores desconhecido."; }
    }

    /// <summary>Abre um arquivo/URL/URI de protocolo via shell (equivalente a clicar duas vezes).</summary>
    public static bool ShellOpen(string target, string? args = null, string what = "item")
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex, $"Abrir {what}: {target}");
            App.Notify.Error($"Não foi possível abrir {what}", ex.Message);
            return false;
        }
    }

    public static string Mhz(double mhz) => mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz";
    public static string Pct(double v) => $"{v:0.#}%";
    public static string Num(double v) => v >= 1e6 ? $"{v / 1e6:0.0} mi" : v >= 1e4 ? $"{v / 1e3:0.0} mil" : $"{v:0}";
}

/// <summary>Séries a partir do histórico real do TelemetryService (sem valores fabricados).</summary>
public static class Hist
{
    public static IReadOnlyList<TelemetrySnapshot> Window(LiveTelemetry live, int cap = LiveTelemetry.Capacity)
    {
        var h = live.History;
        return h.Count <= cap ? h : h.Skip(h.Count - cap).ToList();
    }

    public static double[] Of(IReadOnlyList<TelemetrySnapshot> w, Func<TelemetrySnapshot, double> f)
    {
        var a = new double[w.Count];
        for (int i = 0; i < a.Length; i++) { double v = f(w[i]); a[i] = double.IsFinite(v) ? v : 0; }
        return a;
    }

    /// <summary>
    /// Apenas o trecho final contínuo de leituras disponíveis: lacunas nunca são desenhadas como zero.
    /// </summary>
    public static double[] Avail(IReadOnlyList<TelemetrySnapshot> w, Func<TelemetrySnapshot, Reading<double>> f)
    {
        int start = w.Count;
        while (start > 0) { var r = f(w[start - 1]); if (!r.Available || !double.IsFinite(r.Value)) break; start--; }
        var a = new double[w.Count - start];
        for (int i = 0; i < a.Length; i++) a[i] = f(w[start + i]).Value;
        return a;
    }
}

/// <summary>Linha de processo para as tabelas (taxas nulas = ainda sem amostra anterior, exibidas como "—").</summary>
public sealed record ProcRow(int Pid, string Name, double? Cpu, long WorkingSet, long PrivateBytes, double? IoRead, double? IoWrite, int Threads, int Handles)
{
    public static ProcRow From(ProcessEntry e) =>
        new(e.Pid, e.Name, e.CpuPercent, e.WorkingSetBytes, e.PrivateBytes, e.IoReadBytesPerSec, e.IoWriteBytesPerSec, e.Threads, e.Handles);

    public double CpuSort => Cpu ?? -1;
    public string CpuText => Cpu is { } c ? $"{c:0.0}%" : "—";
    public string WsText => Fmt.Bytes(WorkingSet);
    public string PrivText => Fmt.Bytes(PrivateBytes);
    public double IoReadSort => IoRead ?? -1;
    public double IoWriteSort => IoWrite ?? -1;
    public double IoTotalSort => IoRead is null && IoWrite is null ? -1 : (IoRead ?? 0) + (IoWrite ?? 0);
    public string IoReadText => IoRead is { } r ? Fmt.Rate(r) : "—";
    public string IoWriteText => IoWrite is { } w ? Fmt.Rate(w) : "—";
    public string IoTotalText => IoTotalSort < 0 ? "—" : Fmt.Rate(IoTotalSort);
}

/// <summary>
/// Amostragem periódica de processos (ProcessSampler, ~4 ms) em segundo plano enquanto a página está visível.
/// O callback roda fora da UI e devolve uma ação a ser aplicada na UI (ou null).
/// </summary>
public sealed class ProcessPoller
{
    private readonly ProcessSampler _sampler = new(new ProcessSamplerOptions { ResolveImagePaths = false });
    private CancellationTokenSource? _cts;
    private bool _reported;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    public void Start(Func<ProcessSnapshot, Action?> onSnapshot, string context)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _sampler.Reset();
        _ = Task.Run(async () =>
        {
            bool first = true;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var snap = _sampler.Capture();
                    var ui = onSnapshot(snap);
                    if (ui is not null && !cts.IsCancellationRequested)
                        Ui.Post(() => { if (!cts.IsCancellationRequested) ui(); });
                }
                catch (Exception ex)
                {
                    if (!_reported)
                    {
                        _reported = true;
                        App.Log(ex, context);
                        App.Notify.Warning("Amostragem de processos falhou", ex.Message);
                    }
                }
                // A primeira amostra serve de base para as taxas: a segunda vem 1 s depois.
                try { await Task.Delay(first ? TimeSpan.FromSeconds(1) : Interval, cts.Token); }
                catch (OperationCanceledException) { break; }
                first = false;
            }
        });
    }

    public void Stop()
    {
        var c = _cts;
        _cts = null;
        c?.Cancel();
    }
}

/// <summary>Painel de sensor reutilizável (valor atual + gráfico, ou motivo da indisponibilidade).</summary>
public sealed partial class SensorPanel : ObservableObject
{
    public SensorPanel(string title, string unit, string tip, double maximum = double.NaN)
    {
        Title = title; Unit = unit; Tip = tip; Maximum = maximum;
    }

    public string Title { get; }
    public string Unit { get; }
    public string Tip { get; }
    public double Maximum { get; }

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _labelFormat = "0";
    [ObservableProperty] private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _legend = Array.Empty<LegendItem>();

    /// <summary>Atualiza a partir de uma lista de leituras (nome, série, valor atual, pincel).</summary>
    public void Set(IReadOnlyList<(string name, double[] values, Reading<double> current, Brush brush)> items, string unavailableReason, string format = "0",
        IEnumerable<LegendItem>? extraLegend = null)
    {
        var avail = items.Where(i => i.current.Available && i.values.Length > 0).ToList();
        Available = avail.Count > 0;
        Series = avail.Select(i => Hw.Line(i.name, i.values, i.brush, fill: avail.Count == 1)).ToList();
        double dataMax = avail.SelectMany(i => i.values).DefaultIfEmpty(0).Max();
        LabelFormat = dataMax >= 4 ? "0" : "0.##";
        Legend = items.Select(i => new LegendItem(i.name, i.current.Available ? i.current.Value.ToString(format) + " " + Unit : Hw.Unavailable, i.brush))
            .Concat(extraLegend ?? Enumerable.Empty<LegendItem>()).ToList();
        Value = avail.Count > 0 ? avail[0].current.Value.ToString(format) + " " + Unit : Hw.Unavailable;
        Reason = Available ? "" : unavailableReason;
    }
}

public sealed record BarSegment(string Name, double Value, Brush Brush);

/// <summary>Barra horizontal empilhada (composição da memória etc.). Renderização direta, barata.</summary>
public sealed class StackedBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<BarSegment>), typeof(StackedBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<BarSegment>? Segments { get => (IReadOnlyList<BarSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x25, 0x32)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);
        double r = Math.Min(6, h / 2);
        dc.DrawRoundedRectangle(Track, null, rect, r, r);
        var segs = Segments;
        if (segs is null || segs.Count == 0) return;
        double total = segs.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return;
        dc.PushClip(new RectangleGeometry(rect, r, r));
        double x = 0;
        foreach (var s in segs)
        {
            double sw = Math.Max(0, s.Value) / total * w;
            if (sw > 0.2) dc.DrawRectangle(s.Brush, null, new Rect(x, 0, sw, h));
            x += sw;
        }
        dc.Pop();
    }
}

/// <summary>
/// Comportamentos anexados: DataGrids dentro de páginas roláveis repassam a rolagem do mouse à página
/// (sem isso, a roda "trava" ao passar sobre a tabela).
/// </summary>
public static class HwBehaviors
{
    public static readonly DependencyProperty BubbleWheelProperty = DependencyProperty.RegisterAttached("BubbleWheel", typeof(bool), typeof(HwBehaviors),
        new PropertyMetadata(false, OnBubbleWheelChanged));

    public static bool GetBubbleWheel(DependencyObject d) => (bool)d.GetValue(BubbleWheelProperty);
    public static void SetBubbleWheel(DependencyObject d, bool v) => d.SetValue(BubbleWheelProperty, v);

    private static void OnBubbleWheelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if ((bool)e.NewValue) el.PreviewMouseWheel += OnWheel; else el.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        var parent = VisualTreeHelper.GetParent(d) as UIElement;
        if (parent is null) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }
}
