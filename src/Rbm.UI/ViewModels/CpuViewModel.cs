using System.Collections.ObjectModel;
using System.Windows.Media;
using Rbm.Core;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

/// <summary>Núcleo físico na visão de topologia.</summary>
public sealed record CoreTile(string Title, string Threads, string Tag, string Cache, Brush Accent, string Tip);

/// <summary>Processador: identidade, topologia, carga por thread, clock efetivo, limitação, sensores e processos.</summary>
public sealed partial class CpuViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private readonly ProcessPoller _poller = new();
    private bool _visible;
    public override string Key => "cpu";

    public ObservableCollection<InfoRow> Identity { get; } = new();
    public ObservableCollection<CoreTile> Cores { get; } = new();
    public ObservableCollection<ProcRow> TopProcs { get; } = new();
    public ObservableCollection<InfoRow> PlanRows { get; } = new();

    [ObservableProperty] private string _cpuName = "—";
    [ObservableProperty] private string _topologySummary = "";
    [ObservableProperty] private string _topologyHint = "";
    [ObservableProperty] private double[] _threadLoads = Array.Empty<double>();
    [ObservableProperty] private string[] _threadLabels = Array.Empty<string>();
    [ObservableProperty] private string _loadText = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries> _clockChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _clockLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private bool _clockAvailable;
    [ObservableProperty] private IReadOnlyList<ChartSeries> _limitChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private bool _limitAvailable;
    [ObservableProperty] private string _limitText = "—";
    [ObservableProperty] private string _limitStatus = "";
    [ObservableProperty] private Brush _limitBrush = Brushes.Gray;
    [ObservableProperty] private string _tempText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _sensorReason = "";
    [ObservableProperty] private string _queueText = "—";
    [ObservableProperty] private string _ctxText = "—";
    [ObservableProperty] private string _intText = "—";
    [ObservableProperty] private string _dpcText = "—";
    [ObservableProperty] private string _isrText = "—";
    [ObservableProperty] private string _procStatus = "Coletando processos…";
    [ObservableProperty] private string _planName = "—";
    [ObservableProperty] private string _planStatus = "";

    // ---- Resumo em linguagem simples ----
    public HeroState Hero { get; } = new() { ChipTip = "Atenção quando a temperatura passa de 90 °C ou quando o desempenho fica limitado (abaixo de 90%) em pelo menos 1/4 do tempo observado." };
    public Fact LoadFact { get; } = new("Uso agora", "Utilização total do processador (mesmo número do Gerenciador de Tarefas). Pico = thread mais carregada agora.");
    public Fact ClockFact { get; } = new("Velocidade", "Média de todos os núcleos (clock efetivo). Acima do clock base = turbo/boost ativo.");
    public Fact LimitFact { get; } = new("Limitação de desempenho", "100% = o processador pode usar todo o seu desempenho. Abaixo disso ele está sendo contido por calor, energia ou pela plataforma.");
    public Fact TempFact { get; } = new("Temperatura", "Temperatura do processador lida pelo sensor (LibreHardwareMonitor).");
    [ObservableProperty] private bool _sensorMissing;
    [ObservableProperty] private string _procTip = "";

    public CpuViewModel(ISystemInventory inventory, LiveTelemetry live)
    {
        _inventory = inventory; _live = live;
    }

    public override async Task OnNavigatedToAsync()
    {
        _visible = true;
        BuildStatic();
        _live.Updated += OnSample;
        if (_live.Latest is { } s) OnSample(s);
        _poller.Start(OnProcesses, "CPU: processos");
        await LoadPlanAsync();
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
        _poller.Stop();
    }

    private void BuildStatic()
    {
        var p = _inventory.Current;
        if (p is null) { CpuName = "Inventário ainda não coletado"; Hero.Context = CpuName; return; }
        var c = p.Cpu;
        CpuName = c.Name;
        Hero.Context = c.Name;
        Identity.Clear();
        Identity.Add(new("Fabricante", c.Vendor switch { CpuVendor.Amd => "AMD", CpuVendor.Intel => "Intel", _ => string.IsNullOrEmpty(c.Manufacturer) ? "—" : c.Manufacturer },
            string.IsNullOrEmpty(c.Family) ? null : "Família: " + c.Family));
        Identity.Add(new("Núcleos", $"{c.PhysicalCores} núcleos · {c.LogicalProcessors} threads",
            c.LogicalProcessors > c.PhysicalCores ? "Cada núcleo executa 2 tarefas ao mesmo tempo (SMT/Hyper-Threading ativo)." : "Uma tarefa por núcleo (SMT/Hyper-Threading inativo ou não suportado)."));
        Identity.Add(new("Velocidade base", c.BaseClockMHz > 0 ? Hw.Mhz(c.BaseClockMHz) : "—", "Velocidade de referência. Sob carga o processador costuma ir além dela (turbo/boost). Fonte: Win32_Processor.MaxClockSpeed."));
        Identity.Add(new("Memória cache", $"L2 {Fmt.Bytes(c.L2CacheKB * 1024.0)} · L3 {Fmt.Bytes(c.L3CacheKB * 1024.0)}", "Memória ultrarrápida dentro do processador (valores totais reportados pelo WMI)."));
        if (!string.IsNullOrEmpty(c.Socket)) Identity.Add(new("Soquete", c.Socket, "Encaixe do processador na placa-mãe."));
        Identity.Add(new("Virtualização", c.VirtualizationFirmwareEnabled ? "Ativada na BIOS" : "Desativada ou não informada",
            "VT-x/AMD-V na BIOS/UEFI — necessário para WSL2, Hyper-V e Área Restrita. Com Hyper-V/VBS ativos, o WMI pode reportar \"desativada\"."));

        // ---- Topologia ----
        Cores.Clear();
        byte maxEff = c.Cores.Count > 0 ? c.Cores.Max(x => x.EfficiencyClass) : (byte)0;
        foreach (var core in c.Cores)
        {
            string tag = c.IsHybrid ? (core.EfficiencyClass == maxEff ? "P-core" : "E-core") : $"Classe {core.EfficiencyClass}";
            Brush accent = c.IsHybrid ? (core.EfficiencyClass == maxEff ? Hw.B("B.Cyan") : Hw.B("B.Violet"))
                         : c.L3Domains > 1 && core.L3Domain >= 0 ? Hw.Pal(core.L3Domain) : Hw.B("B.Accent");
            string lps = string.Join(" · ", core.LogicalProcessors);
            string cache = core.L3Domain >= 0 ? $"L3 #{core.L3Domain}" : "L3 ?";
            Cores.Add(new CoreTile($"Núcleo {core.CoreIndex}", $"LP {lps}", tag, cache, accent,
                $"Núcleo físico {core.CoreIndex} · grupo {core.Group} · threads lógicas {lps} · EfficiencyClass {core.EfficiencyClass} · domínio L3 {(core.L3Domain >= 0 ? core.L3Domain.ToString() : "desconhecido")}"));
        }
        var parts = new List<string> { $"{c.Cores.Count} núcleos físicos", $"{c.LogicalProcessors} threads lógicas" };
        if (c.IsHybrid) parts.Add($"{c.PerformanceCores} P-cores + {c.EfficiencyCores} E-cores");
        if (c.L3Domains > 0) parts.Add($"{c.L3Domains} domínio(s) de cache L3");
        TopologySummary = c.Cores.Count == 0 ? "Topologia indisponível (GetLogicalProcessorInformationEx não retornou dados)." : string.Join(" · ", parts);
        TopologyHint = c.IsHybrid
            ? "CPU híbrida: P-cores (EfficiencyClass mais alta) priorizam desempenho; E-cores, eficiência. O agendador do Windows (com Thread Director em Intel) distribui as threads."
            : c.L3Domains > 1
                ? (c.Vendor == CpuVendor.Amd
                    ? $"{c.L3Domains} domínios de L3: em processadores AMD isso normalmente indica {c.L3Domains} CCDs/CCXs. Threads que trocam dados entre domínios pagam latência extra; as cores dos núcleos indicam o domínio."
                    : $"{c.L3Domains} domínios de L3 detectados; as cores dos núcleos indicam o domínio.")
                : "Um único domínio de cache L3: todos os núcleos compartilham o mesmo L3 (sem penalidade entre CCDs).";

        ThreadLabels = Enumerable.Range(0, Math.Max(c.LogicalProcessors, 1)).Select(i => i.ToString()).ToArray();
    }

    private void OnSample(TelemetrySnapshot s)
    {
        if (!_visible) return;
        var c = _inventory.Current?.Cpu;
        var w = Hist.Window(_live);

        ThreadLoads = s.CpuPerCorePercent;
        if (ThreadLabels.Length != s.CpuPerCorePercent.Length) ThreadLabels = Enumerable.Range(0, s.CpuPerCorePercent.Length).Select(i => i.ToString()).ToArray();
        double maxLp = s.CpuPerCorePercent.DefaultIfEmpty(0).Max();
        LoadText = $"Total {s.CpuTotalPercent:0}% · thread mais carregada {maxLp:0}%";

        // Clock efetivo
        ClockAvailable = s.CpuEffectiveClockMHz.Available;
        var clock = new List<ChartSeries>();
        var cl = new List<LegendItem>();
        if (s.CpuEffectiveClockMHz.Available)
        {
            var v = Hist.Avail(w, x => x.CpuEffectiveClockMHz);
            clock.Add(Hw.Line("Efetivo", v, Hw.B("B.Cyan"), fill: true));
            cl.Add(new LegendItem("Efetivo", Hw.Mhz(s.CpuEffectiveClockMHz.Value), Hw.B("B.Cyan"), "Clock base × % Processor Performance (_Total)."));
            if (c is { BaseClockMHz: > 0 })
            {
                clock.Add(Hw.Line("Base", Enumerable.Repeat((double)c.BaseClockMHz, v.Length).ToArray(), Hw.B("B.TextFaint")));
                cl.Add(new LegendItem("Base", Hw.Mhz(c.BaseClockMHz), Hw.B("B.TextFaint"), "Clock base informado pelo WMI."));
            }
            var avail = w.Where(x => x.CpuEffectiveClockMHz.Available).Select(x => x.CpuEffectiveClockMHz.Value).ToList();
            if (avail.Count > 0) cl.Add(new LegendItem("Pico na janela", Hw.Mhz(avail.Max()), Hw.B("B.Success")));
        }
        else cl.Add(new LegendItem("Efetivo", Hw.Unavailable, Hw.B("B.TextFaint"), "Contador % Processor Performance indisponível ou clock base desconhecido."));
        ClockChart = clock; ClockLegend = cl;

        // Limite de desempenho
        LimitAvailable = s.CpuPerformanceLimitPercent.Available;
        if (LimitAvailable)
        {
            var v = Hist.Avail(w, x => x.CpuPerformanceLimitPercent);
            LimitChart = new[] { Hw.Line("% Performance Limit", v, Hw.B("B.Warning"), fill: true) };
            double cur = s.CpuPerformanceLimitPercent.Value;
            int limited = v.Count(x => x < 99.5);
            LimitText = $"{cur:0}%";
            if (cur < 99.5)
            {
                LimitStatus = "Limitado agora: a CPU está abaixo do desempenho máximo por limite térmico, de energia (PL/PPT) ou da plataforma.";
                LimitBrush = Hw.B("B.Warning");
            }
            else
            {
                LimitStatus = limited > 0 ? $"Sem limitação agora · limitado em {limited} de {v.Length} amostras da janela." : "Sem limitação na janela observada.";
                LimitBrush = limited > 0 ? Hw.B("B.Info") : Hw.B("B.Success");
            }
        }
        else
        {
            LimitChart = Array.Empty<ChartSeries>();
            LimitText = Hw.Unavailable;
            LimitStatus = "Contador \"% Performance Limit\" indisponível neste sistema.";
            LimitBrush = Hw.B("B.TextMuted");
        }

        // Sensores
        TempText = s.CpuTemperatureC.Available ? $"{s.CpuTemperatureC.Value:0} °C" : Hw.Unavailable;
        PowerText = s.CpuPackagePowerW.Available ? $"{s.CpuPackagePowerW.Value:0.#} W" : Hw.Unavailable;
        SensorReason = s.CpuTemperatureC.Available && s.CpuPackagePowerW.Available ? "Fonte: LibreHardwareMonitor." : Hw.SensorReason();

        // Contadores
        QueueText = $"{s.ProcessorQueueLength:0}";
        CtxText = $"{Hw.Num(s.ContextSwitchesPerSec)}/s";
        IntText = $"{Hw.Num(s.InterruptsPerSec)}/s";
        DpcText = $"{s.DpcTimePercent:0.00}%";
        IsrText = $"{s.InterruptTimePercent:0.00}%";

        UpdateSummary(s, c, w, maxLp);
    }

    /// <summary>Resumo em linguagem simples (mesmos critérios do Diagnóstico: limite &lt; 90% em ≥ 25% das amostras; temperatura ≥ 90 °C).</summary>
    private void UpdateSummary(TelemetrySnapshot s, CpuInfo? c, IReadOnlyList<TelemetrySnapshot> w, double maxLp)
    {
        double load = s.CpuTotalPercent;
        LoadFact.Set($"{load:0}%", $"{Words.Load(load)} · pico {maxLp:0}%", load < 90 ? UxLevel.Ok : UxLevel.Info);

        // Velocidade
        string clockPart = "";
        if (s.CpuEffectiveClockMHz.Available)
        {
            double mhz = s.CpuEffectiveClockMHz.Value;
            clockPart = $" a {Hw.Mhz(mhz)}";
            if (c is { BaseClockMHz: > 0 })
            {
                double ratio = mhz / c.BaseClockMHz;
                string note = ratio > 1.02 ? "acima da base: turbo ativo" : ratio < 0.6 ? "abaixo da base: economizando energia" : "perto da velocidade base";
                ClockFact.Set(Hw.Mhz(mhz), note, UxLevel.Ok);
            }
            else ClockFact.Set(Hw.Mhz(mhz), "média de todos os núcleos", UxLevel.Ok);
        }
        else ClockFact.Unavailable("contador não disponível");

        // Limitação
        var lim = w.Where(x => x.CpuPerformanceLimitPercent.Available).Select(x => x.CpuPerformanceLimitPercent.Value).ToList();
        UxLevel limLevel = UxLevel.Unknown;
        string limPart = "";
        if (s.CpuPerformanceLimitPercent.Available)
        {
            double cur = s.CpuPerformanceLimitPercent.Value;
            double frac = lim.Count > 0 ? lim.Count(x => x < 90) / (double)lim.Count : 0;
            bool sustained = lim.Count >= 10 && frac >= 0.25;
            limLevel = sustained ? UxLevel.Warn : UxLevel.Ok;
            if (sustained)
            {
                LimitFact.Set($"{cur:0}%", $"limitado em {frac:P0} do tempo", UxLevel.Warn);
                limPart = $"O desempenho está sendo limitado em {frac:P0} do tempo observado (calor, energia ou plataforma).";
            }
            else if (cur < 99.5)
            {
                LimitFact.Set($"{cur:0}%", "contido agora, mas não de forma contínua", UxLevel.Info);
                limPart = "Há limitação momentânea de desempenho, sem padrão contínuo.";
            }
            else
            {
                LimitFact.Set($"{cur:0}%", "sem limitação", UxLevel.Ok);
                limPart = "Nenhuma limitação de desempenho.";
            }
        }
        else LimitFact.Unavailable("contador não disponível neste PC");

        // Temperatura
        UxLevel tempLevel = UxLevel.Unknown;
        string tempPart;
        if (s.CpuTemperatureC.Available)
        {
            double t = s.CpuTemperatureC.Value;
            tempLevel = Words.CpuTempLevel(t);
            string pw = s.CpuPackagePowerW.Available ? $" · {s.CpuPackagePowerW.Value:0} W" : "";
            TempFact.Set($"{t:0} °C", Words.Temp(t) + pw, tempLevel);
            tempPart = $"Temperatura {t:0} °C ({Words.Temp(t)}).";
        }
        else
        {
            TempFact.Unavailable("precisa do driver de sensores");
            tempPart = "";
        }
        SensorMissing = !s.CpuTemperatureC.Available;

        var level = UxLevels.Worst(limLevel, tempLevel);
        if (level == UxLevel.Unknown) level = UxLevel.Ok; // uso e velocidade lidos normalmente; sem sinais de problema
        string headline = level switch
        {
            UxLevel.Danger => $"Seu processador está muito quente ({s.CpuTemperatureC.Value:0} °C).",
            UxLevel.Warn when tempLevel == UxLevel.Warn => $"Seu processador está esquentando: {s.CpuTemperatureC.Value:0} °C com {load:0}% de uso.",
            UxLevel.Warn => $"Seu processador está sendo limitado: {load:0}% de uso{clockPart}.",
            _ => $"Seu processador está {Words.LoadState(load)}: {load:0}% de uso{clockPart}.",
        };
        Hero.Set(level, headline, string.Join(" ", new[] { limPart, tempPart }.Where(x => x.Length > 0)));
    }

    private Action? OnProcesses(Rbm.Processes.ProcessSnapshot snap)
    {
        var top = snap.Processes.Where(p => p.Pid != 0)
            .OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.WorkingSetBytes)
            .Take(15).Select(ProcRow.From).ToList();
        string status = $"{snap.Processes.Count} processos em execução · atualiza a cada 2 s"
                        + (snap.RatesAvailable ? "" : " · o uso aparece a partir da próxima atualização")
                        + (snap.Warning is null ? "" : " · " + snap.Warning);
        string tip = $"Amostra coletada em {snap.CollectionMilliseconds:0.0} ms (ProcessSampler).";
        return () =>
        {
            TopProcs.Clear();
            foreach (var r in top) TopProcs.Add(r);
            ProcStatus = status;
            ProcTip = tip;
        };
    }

    private async Task LoadPlanAsync()
    {
        try
        {
            bool battery = _inventory.Current?.Power.HasBattery ?? false;
            var (name, rows) = await Task.Run(() => ProcessorPlanReader.Read(battery));
            PlanName = name;
            PlanRows.Clear();
            foreach (var r in rows) PlanRows.Add(r);
            PlanStatus = $"Lido do plano ativo às {DateTime.Now:T} (PowrProf).";
        }
        catch (Exception ex)
        {
            App.Log(ex, "CPU: leitura do plano de energia");
            PlanStatus = "Não foi possível ler o plano de energia: " + ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshPlan() => LoadPlanAsync();

    [RelayCommand]
    private void GoOptimizations() => Hw.Main.Navigate("optimizations");
}
