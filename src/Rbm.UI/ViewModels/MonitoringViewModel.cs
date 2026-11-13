using System.ComponentModel;
using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

/// <summary>Telemetria em tempo real de todos os componentes (séries do histórico real do TelemetryService).</summary>
public sealed partial class MonitoringViewModel : PageViewModel
{
    private readonly LiveTelemetry _live;
    private readonly ITelemetryService _svc;
    private bool _visible;
    public override string Key => "monitoring";

    [ObservableProperty] private IReadOnlyList<ChartSeries> _cpuChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _cpuLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _memChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _memLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _gpuChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _gpuLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _tempChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _tempLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _diskRateChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _diskRateLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _diskActiveChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _diskActiveLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _netChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _netLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _latencyChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _latencyLegend = Array.Empty<LegendItem>();

    [ObservableProperty] private bool _perCore;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Is1s), nameof(Is2s), nameof(Is5s))]
    private int _intervalSeconds = 1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseText), nameof(PauseIcon))]
    private bool _isPaused;
    [ObservableProperty] private string _windowText = "Aguardando amostras…";
    [ObservableProperty] private bool _hasTemps;
    [ObservableProperty] private string _tempStatus = "";
    [ObservableProperty] private bool _hasGpu = true;
    [ObservableProperty] private bool _hasSamples;
    public HeroState Hero { get; } = new() { ChipTip = "A coleta de telemetria é compartilhada por todo o aplicativo (mesmo controle da barra de status)." };

    public bool Is1s => IntervalSeconds == 1;
    public bool Is2s => IntervalSeconds == 2;
    public bool Is5s => IntervalSeconds == 5;
    public string PauseText => IsPaused ? "Retomar" : "Pausar";
    public string PauseIcon => IsPaused ? "" : "";

    public MonitoringViewModel(LiveTelemetry live, ITelemetryService svc)
    {
        _live = live; _svc = svc;
    }

    public override Task OnNavigatedToAsync()
    {
        _visible = true;
        IntervalSeconds = (int)Math.Clamp(Math.Round(_svc.Interval.TotalSeconds), 1, 5);
        IsPaused = !_svc.IsRunning;
        _live.Updated += OnSample;
        Hw.Main.PropertyChanged += OnMainChanged;
        Refresh();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
        Hw.Main.PropertyChanged -= OnMainChanged;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.MonitoringActive)) return;
        IsPaused = !_svc.IsRunning;
        Refresh();
    }

    private void OnSample(TelemetrySnapshot s) { if (_visible) Refresh(); }

    partial void OnPerCoreChanged(bool value) { if (_visible) Refresh(); }

    [RelayCommand]
    private void SetInterval(string? seconds)
    {
        if (!int.TryParse(seconds, out var n) || n is not (1 or 2 or 5)) return;
        _svc.Interval = TimeSpan.FromSeconds(n);
        IntervalSeconds = n;
        Refresh();
    }

    [RelayCommand]
    private void TogglePause()
    {
        try
        {
            Hw.Main.ToggleMonitoringCommand.Execute(null);
            IsPaused = !_svc.IsRunning;
            Refresh();
        }
        catch (Exception ex) { App.Log(ex, "Monitoramento: pausar/retomar"); App.Notify.Error("Falha ao alternar monitoramento", ex.Message); }
    }

    private void Refresh()
    {
        var w = Hist.Window(_live);
        HasSamples = w.Count > 0;
        if (w.Count == 0)
        {
            WindowText = IsPaused ? "Monitoramento pausado e sem amostras." : "Aguardando as primeiras amostras…";
            Hero.Set(UxLevel.Unknown, IsPaused ? "Monitoramento pausado." : "Aguardando as primeiras leituras…",
                IsPaused ? "Clique em Retomar para voltar a coletar." : "Os gráficos aparecem em alguns segundos.", IsPaused ? "Pausado" : "Iniciando");
            return;
        }
        var s = w[^1];
        var span = w[^1].Timestamp - w[0].Timestamp;
        WindowText = (IsPaused ? "Pausado — exibindo as últimas amostras coletadas · " : "")
                     + $"Janela: {Fmt.Duration(span)} ({w.Count} amostras) · intervalo {IntervalSeconds} s · vale para todo o aplicativo";

        // ---- CPU ----
        var cpu = new List<ChartSeries>();
        int lps = s.CpuPerCorePercent.Length;
        if (PerCore)
            for (int i = 0; i < lps; i++)
            {
                int k = i;
                cpu.Add(Hw.Line($"LP {k}", Hist.Of(w, x => k < x.CpuPerCorePercent.Length ? x.CpuPerCorePercent[k] : 0), Hw.Pal(k)));
            }
        var totalBrush = PerCore ? Hw.B("B.Text") : Hw.B("B.Cyan");
        cpu.Add(Hw.Line("Total", Hist.Of(w, x => x.CpuTotalPercent), totalBrush, fill: !PerCore));
        CpuChart = cpu;
        var cl = new List<LegendItem>
        {
            new("Total", Hw.Pct(Math.Round(s.CpuTotalPercent)), totalBrush, "% Processor Utility (_Total) — mesmo contador do Gerenciador de Tarefas."),
            new("Clock efetivo", s.CpuEffectiveClockMHz.Available ? Hw.Mhz(s.CpuEffectiveClockMHz.Value) : Hw.Unavailable, Hw.B("B.TextFaint"),
                "Clock base × % Processor Performance (método do Gerenciador de Tarefas)."),
            new("Limite de desempenho", s.CpuPerformanceLimitPercent.Available ? Hw.Pct(Math.Round(s.CpuPerformanceLimitPercent.Value)) : Hw.Unavailable, Hw.B("B.TextFaint"),
                "% Performance Limit: abaixo de 100% indica limitação térmica, de energia ou da plataforma."),
            new("Fila", $"{s.ProcessorQueueLength:0}", Hw.B("B.TextFaint"), "Processor Queue Length: threads prontas aguardando CPU."),
        };
        if (PerCore) cl.Add(new LegendItem("Por thread", $"{lps} linhas coloridas", Hw.Pal(0), "Cada linha é uma thread lógica (LP)."));
        CpuLegend = cl;

        // ---- Memória ----
        double commitPct = s.CommitLimitBytes > 0 ? s.CommitBytes * 100.0 / s.CommitLimitBytes : 0;
        MemChart = new[]
        {
            Hw.Line("RAM", Hist.Of(w, x => x.MemUsedPercent), Hw.B("B.Accent"), fill: true),
            Hw.Line("Commit", Hist.Of(w, x => x.CommitLimitBytes > 0 ? x.CommitBytes * 100.0 / x.CommitLimitBytes : 0), Hw.B("B.Violet")),
        };
        MemLegend = new[]
        {
            new LegendItem("RAM em uso", $"{s.MemUsedPercent:0}% · {Fmt.Bytes(s.MemUsedBytes)} de {Fmt.Bytes(s.MemTotalBytes)}", Hw.B("B.Accent"), "Total − disponível (GlobalMemoryStatusEx)."),
            new LegendItem("Commit", s.CommitLimitBytes > 0 ? $"{commitPct:0}% · {Fmt.Bytes(s.CommitBytes)} de {Fmt.Bytes(s.CommitLimitBytes)}" : Hw.Unavailable, Hw.B("B.Violet"),
                "Memória comprometida vs. limite (RAM + arquivo de paginação). Perto de 100% causa falhas de alocação."),
        };

        // ---- GPU ----
        var gpus = s.Gpus;
        HasGpu = gpus.Count > 0;
        var gs = new List<ChartSeries>();
        var gl = new List<LegendItem>();
        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            long luid = g.AdapterLuid;
            string tag = gpus.Count > 1 ? " · " + Short(g.Name) : "";
            var b3d = Hw.Pal(2 + i * 2); var bv = Hw.Pal(3 + i * 2);
            gs.Add(Hw.Line("3D" + tag, Hist.Of(w, x => x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid)?.Utilization3DPercent ?? 0), b3d, fill: gpus.Count == 1));
            gs.Add(Hw.Line("VRAM" + tag, Hist.Of(w, x => VramPct(x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid))), bv));
            gl.Add(new LegendItem("3D" + tag, $"{g.Utilization3DPercent:0}%", b3d, "Engine 3D (GPU Engine, mesmo método do Gerenciador de Tarefas)."));
            gl.Add(new LegendItem("VRAM" + tag, g.DedicatedTotalBytes > 0 ? $"{VramPct(g):0}% · {Fmt.Bytes(g.DedicatedUsedBytes)} de {Fmt.Bytes(g.DedicatedTotalBytes)}" : Fmt.Bytes(g.DedicatedUsedBytes),
                bv, "Memória dedicada em uso (GPU Adapter Memory)."));
        }
        if (gpus.Count == 0) gl.Add(new LegendItem("GPU", Hw.Unavailable, Hw.B("B.TextFaint"), "Contadores \"GPU Engine\"/\"GPU Adapter Memory\" não retornaram adaptadores."));
        GpuChart = gs; GpuLegend = gl;

        // ---- Temperaturas ----
        var ts = new List<ChartSeries>();
        var tl = new List<LegendItem>();
        int ci = 0;
        void Temp(string name, Func<TelemetrySnapshot, Reading<double>> f, Reading<double> cur)
        {
            var brush = Hw.Pal(4 + ci++);
            if (cur.Available) { var v = Hist.Avail(w, f); if (v.Length > 0) ts.Add(Hw.Line(name, v, brush)); }
            tl.Add(new LegendItem(name, cur.Available ? $"{cur.Value:0} °C" : Hw.Unavailable, brush));
        }
        Temp("CPU", x => x.CpuTemperatureC, s.CpuTemperatureC);
        foreach (var g in gpus)
        {
            long luid = g.AdapterLuid;
            string tag = gpus.Count > 1 ? " " + Short(g.Name) : "";
            Temp("GPU" + tag, x => x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid)?.TemperatureC ?? Reading<double>.None, g.TemperatureC);
            if (g.HotspotC.Available) Temp("Hotspot" + tag, x => x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid)?.HotspotC ?? Reading<double>.None, g.HotspotC);
        }
        foreach (var d in s.Disks)
        {
            string inst = d.Instance;
            if (d.TemperatureC.Available) Temp("Disco " + inst, x => x.Disks.FirstOrDefault(y => y.Instance == inst)?.TemperatureC ?? Reading<double>.None, d.TemperatureC);
        }
        TempChart = ts; TempLegend = tl;
        HasTemps = ts.Count > 0;
        bool anyMissing = tl.Any(l => l.Value == Hw.Unavailable) || s.Disks.All(d => !d.TemperatureC.Available);
        TempStatus = anyMissing ? "Leituras indisponíveis não são estimadas. " + Hw.SensorReason() : "";

        // ---- Disco ----
        DiskRateChart = new[]
        {
            Hw.Line("Leitura", Hist.Of(w, x => x.Disks.Sum(d => d.ReadBytesPerSec) / 1048576.0), Hw.B("B.Cyan"), fill: true),
            Hw.Line("Gravação", Hist.Of(w, x => x.Disks.Sum(d => d.WriteBytesPerSec) / 1048576.0), Hw.B("B.Warning")),
        };
        DiskRateLegend = new[]
        {
            new LegendItem("Leitura", Fmt.Rate(s.Disks.Sum(d => d.ReadBytesPerSec)), Hw.B("B.Cyan"), "Soma de todos os discos físicos (PhysicalDisk)."),
            new LegendItem("Gravação", Fmt.Rate(s.Disks.Sum(d => d.WriteBytesPerSec)), Hw.B("B.Warning"), "Soma de todos os discos físicos (PhysicalDisk)."),
        };
        var da = new List<ChartSeries>();
        var dl = new List<LegendItem>();
        for (int i = 0; i < s.Disks.Count; i++)
        {
            string inst = s.Disks[i].Instance;
            var b = Hw.Pal(i);
            da.Add(Hw.Line("Disco " + inst, Hist.Of(w, x => x.Disks.FirstOrDefault(y => y.Instance == inst)?.ActivePercent ?? 0), b, fill: s.Disks.Count == 1));
            dl.Add(new LegendItem("Disco " + inst, $"{s.Disks[i].ActivePercent:0}% ativo · {s.Disks[i].AvgReadLatencyMs:0.#}/{s.Disks[i].AvgWriteLatencyMs:0.#} ms", b,
                "Tempo ativo (100 − % Idle Time) e latência média de leitura/gravação."));
        }
        DiskActiveChart = da; DiskActiveLegend = dl;

        // ---- Rede ----
        NetChart = new[]
        {
            Hw.Line("Download", Hist.Of(w, x => x.NetRxBytesPerSec * 8 / 1e6), Hw.B("B.Success"), fill: true),
            Hw.Line("Upload", Hist.Of(w, x => x.NetTxBytesPerSec * 8 / 1e6), Hw.B("B.Violet")),
        };
        NetLegend = new[]
        {
            new LegendItem("Download", $"{s.NetRxBytesPerSec * 8 / 1e6:0.00} Mb/s", Hw.B("B.Success"), "Soma das interfaces (exceto loopback)."),
            new LegendItem("Upload", $"{s.NetTxBytesPerSec * 8 / 1e6:0.00} Mb/s", Hw.B("B.Violet"), "Soma das interfaces (exceto loopback)."),
        };

        // ---- DPC/ISR ----
        LatencyChart = new[]
        {
            Hw.Line("DPC", Hist.Of(w, x => x.DpcTimePercent), Hw.B("B.Warning"), fill: true),
            Hw.Line("ISR", Hist.Of(w, x => x.InterruptTimePercent), Hw.B("B.Danger")),
        };
        LatencyLegend = new[]
        {
            new LegendItem("DPC", $"{s.DpcTimePercent:0.00}%", Hw.B("B.Warning"), "% DPC Time: tempo da CPU em chamadas de procedimento adiado (drivers)."),
            new LegendItem("ISR", $"{s.InterruptTimePercent:0.00}%", Hw.B("B.Danger"), "% Interrupt Time: tempo em rotinas de interrupção de hardware."),
            new LegendItem("Interrupções", $"{Hw.Num(s.InterruptsPerSec)}/s", Hw.B("B.TextFaint")),
            new LegendItem("Trocas de contexto", $"{Hw.Num(s.ContextSwitchesPerSec)}/s", Hw.B("B.TextFaint")),
        };

        // ---- Resumo ----
        var g0 = gpus.OrderByDescending(x => x.DedicatedTotalBytes).FirstOrDefault();
        var parts = new List<string> { $"CPU {s.CpuTotalPercent:0}%", $"RAM {s.MemUsedPercent:0}%" };
        if (g0 is not null) parts.Add($"GPU {g0.Utilization3DPercent:0}%");
        parts.Add($"Disco {Fmt.Rate(s.Disks.Sum(d => d.ReadBytesPerSec) + s.Disks.Sum(d => d.WriteBytesPerSec))}");
        parts.Add($"Rede ↓ {s.NetRxBytesPerSec * 8 / 1e6:0.0} · ↑ {s.NetTxBytesPerSec * 8 / 1e6:0.0} Mb/s");
        Hero.Set(IsPaused ? UxLevel.Unknown : UxLevel.Ok,
            IsPaused ? "Monitoramento pausado — os gráficos mostram as últimas leituras." : "Acompanhando o PC em tempo real.",
            "Agora: " + string.Join(" · ", parts), IsPaused ? "Pausado" : "Ao vivo");
        Hero.Context = $"Últimos {Fmt.Duration(span)} ({w.Count} leituras) · uma a cada {IntervalSeconds} s · vale para todo o aplicativo";
    }

    private static double VramPct(GpuTelemetry? g) => g is { DedicatedTotalBytes: > 0 } ? g.DedicatedUsedBytes * 100.0 / g.DedicatedTotalBytes : 0;

    private static string Short(string name)
    {
        var n = name.Replace("NVIDIA", "").Replace("GeForce", "").Replace("AMD", "").Replace("Radeon", "").Replace("Intel(R)", "").Replace("Graphics", "").Trim();
        return n.Length > 14 ? n[..14] : n.Length == 0 ? name : n;
    }
}
