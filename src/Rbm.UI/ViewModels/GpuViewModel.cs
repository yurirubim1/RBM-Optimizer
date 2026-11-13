using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Rbm.Processes;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

public sealed record GpuAdapterOption(string Name, long Luid, GpuInfo Info)
{
    public override string ToString() => Name;
}

public sealed record GpuProcRow(int Pid, string Name, double MaxPct, double SumPct, string Engines)
{
    public string MaxText => $"{MaxPct:0.0}%";
    public string SumText => $"{SumPct:0.0}%";
}

/// <summary>Placa de vídeo: identidade, engines, VRAM, sensores, uso por processo, HAGS/Modo de Jogo e ferramentas oficiais.</summary>
public sealed partial class GpuViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private readonly ProcessPoller _poller = new();
    private GpuProcessUsageReader? _gpuReader;
    private readonly object _readerLock = new();
    private bool _visible;
    public override string Key => "gpu";

    public ObservableCollection<GpuAdapterOption> Adapters { get; } = new();
    public ObservableCollection<InfoRow> Identity { get; } = new();
    public ObservableCollection<GpuProcRow> Procs { get; } = new();
    public ObservableCollection<VendorToolItem> Tools { get; } = new();

    public SensorPanel TempPanel { get; } = new("Temperatura", "°C", "Temperatura do núcleo (edge) e hotspot/junção via LibreHardwareMonitor (ADL/NVAPI/IGCL).");
    public SensorPanel ClockPanel { get; } = new("Clocks", "MHz", "Clock do núcleo e da memória via LibreHardwareMonitor.");
    public SensorPanel PowerPanel { get; } = new("Potência", "W", "Potência da placa/pacote reportada pelo driver via LibreHardwareMonitor.");
    public SensorPanel FanPanel { get; } = new("Ventoinha", "RPM", "Rotação da ventoinha. 0 RPM em repouso é normal em placas com modo semipassivo.");

    [ObservableProperty] private GpuAdapterOption? _selectedAdapter;
    [ObservableProperty] private bool _hasMultiple;
    [ObservableProperty] private bool _hasAdapter;
    [ObservableProperty] private string _adapterTitle = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries> _engineChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _engineLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _vramChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _vramLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private double _vramPct;
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private bool _liveAvailable;
    [ObservableProperty] private string _procStatus = "Coletando uso por processo…";
    [ObservableProperty] private string _procTip = "";
    [ObservableProperty] private string _hagsText = "—";
    [ObservableProperty] private string _hagsHint = "";
    [ObservableProperty] private string _gameModeText = "—";
    [ObservableProperty] private string _gameModeHint = "";
    [ObservableProperty] private bool _hasTools;

    // ---- Resumo em linguagem simples ----
    public ObservableCollection<InfoRow> TechRows { get; } = new();
    public HeroState Hero { get; } = new() { ChipTip = "Atenção quando o núcleo passa de 85 °C, o hotspot de 100 °C ou a memória de vídeo chega a 95%. Problema a partir de 90 °C no núcleo ou 110 °C no hotspot." };
    public Fact UseFact { get; } = new("Uso agora", "Engine mais ocupada da GPU — o mesmo número da coluna \"GPU\" do Gerenciador de Tarefas.");
    public Fact VramFact { get; } = new("Memória de vídeo", "VRAM dedicada em uso. Quando enche, a placa passa a usar a RAM do sistema, que é bem mais lenta.");
    public Fact TempFact { get; } = new("Temperatura", "Temperatura do núcleo da GPU (e do ponto mais quente, hotspot, quando disponível).");
    public Fact PowerFact { get; } = new("Consumo", "Potência elétrica reportada pelo driver da placa.");
    [ObservableProperty] private bool _sensorMissing;
    [ObservableProperty] private string _sensorReasonText = "";
    [ObservableProperty] private bool _hagsOn;
    [ObservableProperty] private bool _gameModeOn;

    public GpuViewModel(ISystemInventory inventory, LiveTelemetry live)
    {
        _inventory = inventory; _live = live;
    }

    public override Task OnNavigatedToAsync()
    {
        _visible = true;
        LoadAdapters();
        LoadStates();
        _live.Updated += OnSample;
        if (_live.Latest is { } s) OnSample(s);
        StartProcessPolling();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
        _poller.Stop();
        GpuProcessUsageReader? r;
        lock (_readerLock) { r = _gpuReader; _gpuReader = null; }
        if (r is not null) _ = Task.Run(r.Dispose);
    }

    /// <summary>Leitor PDH criado sob demanda (fora da UI) e descartado ao sair da página.</summary>
    private GpuProcessUsageReader? AcquireReader()
    {
        lock (_readerLock)
        {
            if (!_visible) return null;
            return _gpuReader ??= new GpuProcessUsageReader();
        }
    }

    private void LoadAdapters()
    {
        var p = _inventory.Current;
        var prevLuid = SelectedAdapter?.Luid;
        Adapters.Clear();
        if (p is not null)
            foreach (var g in p.Gpus.Where(g => !g.IsVirtual).OrderByDescending(g => g.IsDiscrete).ThenByDescending(g => g.DedicatedVideoMemoryBytes))
                Adapters.Add(new GpuAdapterOption(g.Name, g.AdapterLuid, g));
        HasAdapter = Adapters.Count > 0;
        HasMultiple = Adapters.Count > 1;
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Luid == prevLuid) ?? Adapters.FirstOrDefault();
        if (SelectedAdapter is null) { Identity.Clear(); AdapterTitle = p is null ? "Inventário ainda não coletado" : "Nenhum adaptador físico detectado"; }

        Tools.Clear();
        if (p is not null)
            foreach (var t in VendorToolItem.Detect(p.Gpus.Where(g => !g.IsVirtual).Select(g => g.Vendor))) Tools.Add(t);
        HasTools = Tools.Count > 0;
    }

    private void LoadStates()
    {
        bool? hags, gm;
        (HagsText, HagsHint, hags) = GraphicsState.Hags();
        (GameModeText, GameModeHint, gm) = GraphicsState.GameMode();
        HagsOn = hags == true;
        GameModeOn = gm == true;
    }

    partial void OnSelectedAdapterChanged(GpuAdapterOption? value)
    {
        BuildIdentity();
        if (_visible && _live.Latest is { } s) OnSample(s);
    }

    private void BuildIdentity()
    {
        Identity.Clear(); TechRows.Clear();
        var g = SelectedAdapter?.Info;
        if (g is null) return;
        AdapterTitle = g.Name;
        Hero.Context = g.Name;
        Identity.Add(new("Fabricante", g.Vendor switch { GpuVendor.Amd => "AMD", GpuVendor.Nvidia => "NVIDIA", GpuVendor.Intel => "Intel", GpuVendor.Microsoft => "Microsoft", _ => "Outro" }));
        Identity.Add(new("Tipo", g.IsDiscrete ? "Placa dedicada" : "Integrada ao processador",
            g.IsDiscrete ? "Placa de vídeo própria, com memória separada (melhor para jogos)." : "Vídeo embutido no processador; usa parte da RAM do sistema."));
        Identity.Add(new("Memória de vídeo", g.DedicatedVideoMemoryBytes > 0 ? Fmt.Bytes(g.DedicatedVideoMemoryBytes) : "—", "Memória própria da placa (VRAM dedicada)."));
        Identity.Add(new("Driver", string.IsNullOrEmpty(g.DriverVersion) ? "—" : g.DriverVersion + (g.DriverDate is { } dd ? $" ({dd:d})" : ""),
            "Versão e data do driver de vídeo instalado. Atualizações de driver ficam na página Drivers."));
        if (!string.IsNullOrEmpty(g.CurrentResolution))
            Identity.Add(new("Tela", $"{g.CurrentResolution} a {g.CurrentRefreshRate} Hz", "Resolução e taxa de atualização do monitor principal ligado a esta placa."));

        TechRows.Add(new("Identificação PCI", g.VendorId != 0 ? $"VEN_{g.VendorId:X4} DEV_{g.DeviceId:X4}" : "—", "Identificadores de fabricante e dispositivo no barramento PCI."));
        TechRows.Add(new("Tipo (critério)", g.IsDiscrete ? "Dedicada (discreta)" : "Integrada", "Heurística: fabricante + VRAM dedicada."));
        TechRows.Add(new("VRAM dedicada", g.DedicatedVideoMemoryBytes > 0 ? Fmt.Bytes(g.DedicatedVideoMemoryBytes) : "—", "DXGI DedicatedVideoMemory (64 bits; o WMI limita a 4 GB)."));
        TechRows.Add(new("Memória compartilhada", g.SharedSystemMemoryBytes > 0 ? Fmt.Bytes(g.SharedSystemMemoryBytes) : "—", "Limite de RAM do sistema que o adaptador pode usar (DXGI SharedSystemMemory)."));
        TechRows.Add(new("LUID do adaptador", g.AdapterLuid != 0 ? $"0x{g.AdapterLuid:X}" : "—", "Identificador DXGI usado para casar os contadores \"GPU Engine\"."));
        TechRows.Add(new("Fontes", "WMI (Win32_VideoController) + DXGI", "Uso por engine: contadores PDH \"GPU Engine\" (mesmo método do Gerenciador de Tarefas). Sensores: LibreHardwareMonitor (ADL/NVAPI/IGCL)."));
    }

    private void OnSample(TelemetrySnapshot s)
    {
        if (!_visible) return;
        var sel = SelectedAdapter;
        if (sel is null) return;
        long luid = sel.Luid;
        var g = s.Gpus.FirstOrDefault(x => x.AdapterLuid == luid);
        LiveAvailable = g is not null;
        var w = Hist.Window(_live);
        GpuTelemetry? G(TelemetrySnapshot x) => x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid);
        string reason = Hw.SensorReason();

        if (g is null)
        {
            EngineChart = Array.Empty<ChartSeries>(); VramChart = Array.Empty<ChartSeries>();
            EngineLegend = new[] { new LegendItem("Engines", Hw.Unavailable, Hw.B("B.TextFaint"), "Os contadores \"GPU Engine\" não retornaram este adaptador (LUID).") };
            VramLegend = Array.Empty<LegendItem>();
            VramText = Hw.Unavailable; VramPct = 0;
            foreach (var p in new[] { TempPanel, ClockPanel, PowerPanel, FanPanel }) p.Set(Array.Empty<(string, double[], Reading<double>, System.Windows.Media.Brush)>(), "Telemetria deste adaptador indisponível.");
            Hero.Set(UxLevel.Unknown, "Não foi possível ler a atividade desta placa de vídeo agora.",
                "Os contadores de GPU do Windows não retornaram dados para este adaptador. As informações abaixo continuam válidas.");
            foreach (var f in new[] { UseFact, VramFact, TempFact, PowerFact }) f.Unavailable();
            SensorMissing = false;
            return;
        }

        // Engines
        var engines = new (string name, Func<GpuTelemetry, double> f, string tip)[]
        {
            ("3D", x => x.Utilization3DPercent, "Renderização 3D: jogos e a própria interface do Windows."),
            ("Cópia", x => x.CopyPercent, "Transferências de memória (DMA)."),
            ("Gravação de vídeo", x => x.VideoEncodePercent, "Codificador de vídeo (Encode): gravação, streaming, chamadas de vídeo."),
            ("Reprodução de vídeo", x => x.VideoDecodePercent, "Decodificador de vídeo (Decode): assistir vídeos e streams."),
            ("Computação", x => x.ComputePercent, "Filas de computação (Compute_0/1): IA, renderização, mineração etc."),
        };
        var es = new List<ChartSeries>(); var el = new List<LegendItem>();
        for (int i = 0; i < engines.Length; i++)
        {
            var (name, f, tip) = engines[i];
            var b = Hw.Pal(i);
            es.Add(Hw.Line(name, Hist.Of(w, x => G(x) is { } y ? f(y) : 0), b, fill: i == 0));
            el.Add(new LegendItem(name, $"{f(g):0}%", b, tip));
        }
        el.Add(new LegendItem("Uso geral", $"{g.UtilizationMaxEnginePercent:0}%", Hw.B("B.Text"), "Engine mais ocupada — equivale à coluna \"GPU\" do Gerenciador de Tarefas."));
        EngineChart = es; EngineLegend = el;

        // VRAM
        double total = g.DedicatedTotalBytes;
        VramPct = total > 0 ? g.DedicatedUsedBytes * 100.0 / total : 0;
        VramText = total > 0 ? $"{Fmt.Bytes(g.DedicatedUsedBytes)} de {Fmt.Bytes(total)} ({VramPct:0}%)" : Fmt.Bytes(g.DedicatedUsedBytes);
        VramChart = new[]
        {
            Hw.Line("Dedicada", Hist.Of(w, x => (G(x)?.DedicatedUsedBytes ?? 0) / 1073741824.0), Hw.B("B.Violet"), fill: true),
            Hw.Line("Compartilhada", Hist.Of(w, x => (G(x)?.SharedUsedBytes ?? 0) / 1073741824.0), Hw.B("B.Cyan")),
        };
        VramLegend = new[]
        {
            new LegendItem("Dedicada", Fmt.Bytes(g.DedicatedUsedBytes), Hw.B("B.Violet"), "GPU Adapter Memory → Dedicated Usage."),
            new LegendItem("Compartilhada", Fmt.Bytes(g.SharedUsedBytes), Hw.B("B.Cyan"), "GPU Adapter Memory → Shared Usage (RAM do sistema usada pela GPU)."),
        };

        // Sensores
        string sensorReason = "Sensor não exposto para este adaptador. " + reason;
        TempPanel.Set(new[]
        {
            ("Núcleo", Hist.Avail(w, x => G(x)?.TemperatureC ?? Reading<double>.None), g.TemperatureC, Hw.B("B.Warning")),
            ("Hotspot", Hist.Avail(w, x => G(x)?.HotspotC ?? Reading<double>.None), g.HotspotC, Hw.B("B.Danger")),
        }, sensorReason);
        ClockPanel.Set(new[]
        {
            ("Núcleo", Hist.Avail(w, x => G(x)?.CoreClockMHz ?? Reading<double>.None), g.CoreClockMHz, Hw.B("B.Cyan")),
            ("Memória", Hist.Avail(w, x => G(x)?.MemoryClockMHz ?? Reading<double>.None), g.MemoryClockMHz, Hw.B("B.Violet")),
        }, sensorReason);
        PowerPanel.Set(new[] { ("Potência", Hist.Avail(w, x => G(x)?.PowerW ?? Reading<double>.None), g.PowerW, Hw.B("B.Success")) }, sensorReason, "0.#");
        FanPanel.Set(new[] { ("Ventoinha", Hist.Avail(w, x => G(x)?.FanRpm ?? Reading<double>.None), g.FanRpm, Hw.B("B.Info")) }, sensorReason);

        UpdateSummary(g, reason);
    }

    /// <summary>Resumo em linguagem simples (limiares do Diagnóstico: núcleo ≥ 85 °C, hotspot ≥ 100 °C).</summary>
    private void UpdateSummary(GpuTelemetry g, string reason)
    {
        double use = g.UtilizationMaxEnginePercent;
        UseFact.Set($"{use:0}%", $"{Words.Load(use)} · 3D {g.Utilization3DPercent:0}%", UxLevel.Ok);

        double total = g.DedicatedTotalBytes;
        UxLevel vramLevel = UxLevel.Unknown;
        if (total > 0)
        {
            double pct = g.DedicatedUsedBytes * 100.0 / total;
            vramLevel = pct >= 95 ? UxLevel.Warn : UxLevel.Ok;
            VramFact.Set($"{pct:0}%", $"{Fmt.Bytes(g.DedicatedUsedBytes)} de {Fmt.Bytes(total)} · {Words.Mem(pct)}", vramLevel);
        }
        else VramFact.Set(Fmt.Bytes(g.DedicatedUsedBytes), "em uso (total desconhecido)", UxLevel.Unknown);

        UxLevel tempLevel = UxLevel.Unknown;
        string tempPart = "";
        if (g.TemperatureC.Available)
        {
            double t = g.TemperatureC.Value;
            tempLevel = Words.GpuTempLevel(t);
            string hs = "";
            if (g.HotspotC.Available)
            {
                tempLevel = UxLevels.Worst(tempLevel, Words.HotspotLevel(g.HotspotC.Value));
                hs = $" · hotspot {g.HotspotC.Value:0} °C";
            }
            TempFact.Set($"{t:0} °C", Words.Temp(t) + hs, tempLevel);
            string word = t < 70 ? "boa" : t < 85 ? "morna" : "alta";
            tempPart = $" e com temperatura {word} ({t:0} °C)";
        }
        else TempFact.Unavailable("sensor não exposto");

        if (g.PowerW.Available)
            PowerFact.Set($"{g.PowerW.Value:0} W", g.CoreClockMHz.Available ? $"núcleo a {Hw.Mhz(g.CoreClockMHz.Value)}" : "potência da placa", UxLevel.Ok);
        else PowerFact.Unavailable("sensor não exposto");

        SensorMissing = !g.TemperatureC.Available && !g.PowerW.Available;
        SensorReasonText = reason;

        var level = UxLevels.Worst(tempLevel, vramLevel);
        if (level == UxLevel.Unknown) level = UxLevel.Ok;
        string headline = level switch
        {
            UxLevel.Danger => $"A placa de vídeo está muito quente ({g.TemperatureC.Value:0} °C{(g.HotspotC.Available ? $", hotspot {g.HotspotC.Value:0} °C" : "")}).",
            UxLevel.Warn when tempLevel == UxLevel.Warn => $"A placa de vídeo está esquentando: {g.TemperatureC.Value:0} °C com {use:0}% de uso.",
            UxLevel.Warn => $"A memória de vídeo está quase cheia ({VramFact.Value}).",
            _ => $"A placa de vídeo está {Words.LoadState(use, fem: true)} ({use:0}%){tempPart}.",
        };
        string detail = total > 0
            ? $"Memória de vídeo: {Fmt.Bytes(g.DedicatedUsedBytes)} de {Fmt.Bytes(total)} em uso."
              + (vramLevel == UxLevel.Warn ? " Quando ela enche, o jogo passa a usar a RAM do sistema e pode engasgar — reduza texturas/resolução." : "")
            : "";
        Hero.Set(level, headline, detail);
    }

    private void StartProcessPolling()
    {
        _poller.Start(snap =>
        {
            var reader = AcquireReader();
            if (reader is null) return null;
            var usage = reader.Read();
            if (!usage.Available)
            {
                string err = usage.Error ?? "Contador indisponível.";
                return () => { Procs.Clear(); ProcStatus = "Uso por processo indisponível: " + err; };
            }
            var names = snap.Processes.GroupBy(p => p.Pid).ToDictionary(x => x.Key, x => x.First().Name);
            var rows = usage.ByPid.Values.Where(u => u.SumPercent > 0.05)
                .OrderByDescending(u => u.MaxEnginePercent).ThenByDescending(u => u.SumPercent).Take(20)
                .Select(u => new GpuProcRow(u.Pid, names.TryGetValue(u.Pid, out var n) ? n : "(processo encerrado)", u.MaxEnginePercent, u.SumPercent,
                    string.Join(" · ", u.ByEngineType.Where(kv => kv.Value >= 0.1).OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{EngineName(kv.Key)} {kv.Value:0}%"))))
                .ToList();
            string status = $"{rows.Count} programa(s) usando a placa de vídeo agora · atualiza a cada 2 s";
            string tip = $"{usage.ByPid.Count} processo(s) com contexto de GPU · soma de todos os adaptadores (contadores PDH \"GPU Engine\" por PID).";
            return () =>
            {
                Procs.Clear();
                foreach (var r in rows) Procs.Add(r);
                ProcStatus = rows.Count == 0 ? "Nenhum programa está usando a placa de vídeo de forma mensurável agora." : status;
                ProcTip = tip;
            };
        }, "GPU: uso por processo");
    }

    private static string EngineName(string t) => t.StartsWith("VideoDecode", StringComparison.OrdinalIgnoreCase) ? "Vídeo (reprodução)"
        : t.StartsWith("VideoEncode", StringComparison.OrdinalIgnoreCase) ? "Vídeo (gravação)"
        : t.StartsWith("Compute", StringComparison.OrdinalIgnoreCase) ? "Computação"
        : t.StartsWith("Video Codec", StringComparison.OrdinalIgnoreCase) ? "Vídeo (codec)"
        : t.StartsWith("High Priority", StringComparison.OrdinalIgnoreCase) ? t.Substring(13).Trim() + " (prioridade alta)"
        : t.Equals("Copy", StringComparison.OrdinalIgnoreCase) ? "Cópia" : t;

    [RelayCommand]
    private void OpenGraphicsSettings() => Hw.ShellOpen("ms-settings:display-advancedgraphics", what: "as configurações de gráficos");

    [RelayCommand]
    private void OpenGameModeSettings() => Hw.ShellOpen("ms-settings:gaming-gamemode", what: "as configurações do Modo de Jogo");

    [RelayCommand]
    private void RefreshStates() { LoadStates(); LoadAdapters(); }
}
