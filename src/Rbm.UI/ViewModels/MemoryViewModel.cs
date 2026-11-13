using System.Collections.ObjectModel;
using System.Windows.Media;
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

public sealed record ModuleRow(string Slot, string Bank, string Manufacturer, string PartNumber, long Capacity, int Rated, int Configured, string FormFactor)
{
    public string CapacityText => Fmt.Bytes(Capacity);
    public string RatedText => Rated > 0 ? $"{Rated} MT/s" : "—";
    public string ConfiguredText => Configured > 0 ? $"{Configured} MT/s" : "—";
}

public sealed record LeakRow(string Name, int Pid, string Observed, long PrivateStart, long PrivateEnd, double SlopeMbMin, double Monotonicity, bool PossibleLeak, string Notes)
{
    public string PrivateText => $"{Fmt.Bytes(PrivateStart)} → {Fmt.Bytes(PrivateEnd)}";
    public string SlopeText => $"{SlopeMbMin:+0.0;-0.0;0} MB/min";
    public string MonoText => $"{Monotonicity * 100:0}%";
    public string Verdict => PossibleLeak ? "Possível vazamento" : "Inconclusivo";
}

/// <summary>Memória: módulos, canais, XMP/EXPO, composição em tempo real, paginação, processos e suspeitas de vazamento.</summary>
public sealed partial class MemoryViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private readonly ProcessPoller _poller = new();
    private readonly object _windowLock = new();
    private readonly List<ProcessSnapshot> _window = new();
    private int _captureCount;
    private DateTimeOffset _sessionStart;
    private bool _visible;
    private const int AnalysisEvery = 5;      // 1 em cada 5 capturas (≈ 10 s) entra na janela de análise
    private const int WindowCapacity = 90;    // ≈ 15 min

    public override string Key => "memory";

    public ObservableCollection<InfoRow> Summary { get; } = new();
    public ObservableCollection<ModuleRow> Modules { get; } = new();
    public ObservableCollection<PageFileRow> PageFiles { get; } = new();
    public ObservableCollection<ProcRow> TopProcs { get; } = new();
    public ObservableCollection<LeakRow> Leaks { get; } = new();

    [ObservableProperty] private string _xmpTitle = "";
    [ObservableProperty] private string _xmpText = "";
    [ObservableProperty] private Brush _xmpBrush = Brushes.Gray;
    [ObservableProperty] private string _channelText = "—";
    [ObservableProperty] private string _channelEvidence = "";
    [ObservableProperty] private IReadOnlyList<BarSegment> _composition = Array.Empty<BarSegment>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _compositionLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private string _commitText = "—";
    [ObservableProperty] private double _commitPct;
    [ObservableProperty] private string _cacheText = "—";
    [ObservableProperty] private IReadOnlyList<ChartSeries> _usageChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _faultsChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _faultsLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private string _pageFileConfig = "Lendo…";
    [ObservableProperty] private string _pageFileDetail = "";
    [ObservableProperty] private string _procStatus = "Coletando processos…";
    [ObservableProperty] private string _leakStatus = "A análise começa após algumas amostras.";

    // ---- Resumo em linguagem simples ----
    public HeroState Hero { get; } = new() { ChipTip = "Atenção quando a RAM ou a memória comprometida (commit) passa de 85%. Problema com commit ≥ 95% ou RAM ≥ 90% com leituras de disco altas e contínuas." };
    public Fact UsedFact { get; } = new("RAM em uso", "Memória ocupada por programas, Windows e drivers (Total − disponível).");
    public Fact AvailFact { get; } = new("Disponível", "Livre + cache em espera. O cache é entregue instantaneamente a quem precisar — não é desperdício.");
    public Fact CommitFact { get; } = new("Memória comprometida (commit)", "Quanto os programas reservaram, comparado ao limite (RAM + arquivo de paginação). Perto de 100%, programas podem fechar por falta de memória.");
    public Fact FaultsFact { get; } = new("Leituras do disco", "Falhas de página (hard faults): vezes por segundo em que o Windows teve de buscar no disco algo que não estava na RAM. Picos são normais; valores altos e contínuos indicam falta de RAM.");
    [ObservableProperty] private bool _xmpWarn;

    public MemoryViewModel(ISystemInventory inventory, LiveTelemetry live)
    {
        _inventory = inventory; _live = live;
    }

    public override async Task OnNavigatedToAsync()
    {
        _visible = true;
        BuildStatic();
        _live.Updated += OnSample;
        if (_live.Latest is { } s) OnSample(s);
        lock (_windowLock) { _window.Clear(); _captureCount = 0; }
        _sessionStart = DateTimeOffset.Now;
        Leaks.Clear();
        LeakStatus = "Observando desde agora. A análise usa apenas amostras coletadas enquanto esta página está aberta.";
        _poller.Start(OnProcesses, "Memória: processos");
        await LoadPageFileAsync();
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
        _poller.Stop();
        lock (_windowLock) _window.Clear();
    }

    private void BuildStatic()
    {
        var p = _inventory.Current;
        Summary.Clear(); Modules.Clear(); XmpWarn = false;
        if (p is null) { ChannelText = "Inventário ainda não coletado"; return; }
        var m = p.Memory;
        Hero.Context = $"{m.TotalPhysicalBytes / 1073741824.0:0} GB{(string.IsNullOrEmpty(m.MemoryType) ? "" : " " + m.MemoryType)}"
                       + (m.ConfiguredSpeedMTs > 0 ? $" · {m.ConfiguredSpeedMTs} MT/s" : "");
        Summary.Add(new("Instalada", $"{m.TotalPhysicalBytes / 1073741824.0:0.#} GB utilizáveis{(string.IsNullOrEmpty(m.MemoryType) ? "" : " · " + m.MemoryType)}",
            m.Modules.Count > 0 ? $"Soma dos módulos: {Fmt.Bytes(m.Modules.Sum(x => (double)x.CapacityBytes))} (a diferença é reservada pelo hardware/firmware/vídeo integrado)." : null));
        Summary.Add(new("Pentes / slots", m.SlotsTotal > 0 ? $"{m.Modules.Count} de {m.SlotsTotal} ocupados" : $"{m.Modules.Count} módulo(s)",
            m.SlotsTotal > m.Modules.Count && m.SlotsTotal > 0 ? "Há slots livres para expandir a memória." : null));
        Summary.Add(new("Velocidade", m.ConfiguredSpeedMTs > 0 ? $"{m.ConfiguredSpeedMTs} MT/s em uso" : "—",
            m.RatedSpeedMTs > 0 ? $"Velocidade padrão declarada pelos pentes: {m.RatedSpeedMTs} MT/s" : "Velocidade nominal não informada"));
        foreach (var x in m.Modules)
            Modules.Add(new ModuleRow(x.DeviceLocator, x.BankLabel, string.IsNullOrWhiteSpace(x.Manufacturer) ? "—" : x.Manufacturer,
                string.IsNullOrWhiteSpace(x.PartNumber) ? "—" : x.PartNumber, x.CapacityBytes, x.SpeedMTs, x.ConfiguredSpeedMTs, x.FormFactor));

        ChannelText = m.EstimatedChannels switch
        {
            null => "Indeterminado",
            1 => "Canal único (single-channel)",
            2 => "Dual-channel",
            int n => $"{n} canais",
        };
        ChannelEvidence = m.ChannelEvidence;

        int rated = m.RatedSpeedMTs, conf = m.ConfiguredSpeedMTs;
        if (rated <= 0 || conf <= 0)
        {
            XmpTitle = "Perfil XMP/EXPO: indeterminado"; XmpBrush = Hw.B("B.TextMuted");
            XmpText = "O firmware não informou a velocidade nominal e/ou configurada dos módulos.";
        }
        else if (conf > rated)
        {
            XmpTitle = "Perfil XMP/EXPO (ou ajuste manual) aparentemente ativo"; XmpBrush = Hw.B("B.Success");
            XmpText = $"Os módulos operam a {conf} MT/s, acima da velocidade JEDEC reportada ({rated} MT/s). Isso indica perfil XMP/EXPO ou configuração manual no BIOS.";
        }
        else if (conf < rated)
        {
            XmpTitle = "Memória abaixo da velocidade nominal"; XmpBrush = Hw.B("B.Warning"); XmpWarn = true;
            XmpText = $"Os módulos suportam {rated} MT/s, mas operam a {conf} MT/s. Pode ser limite do controlador de memória/placa, mistura de módulos ou perfil XMP/EXPO desativado no BIOS. Verifique a lista de compatibilidade da placa antes de ativar.";
        }
        else
        {
            XmpTitle = "Operando na velocidade nominal reportada"; XmpBrush = Hw.B("B.Info");
            XmpText = $"Configurada = nominal ({conf} MT/s). Atenção: o WMI expõe a velocidade JEDEC do SPD, não a do perfil XMP/EXPO. Se o kit é vendido como mais rápido (confira o part number), o perfil pode estar desativado no BIOS.";
        }
    }

    private void OnSample(TelemetrySnapshot s)
    {
        if (!_visible) return;
        long total = s.MemTotalBytes, avail = s.MemAvailableBytes, standby = s.StandbyBytes, modified = s.ModifiedBytes;
        long inUse = Math.Max(0, total - avail - modified);
        long free = Math.Max(0, avail - standby);
        Composition = new[]
        {
            new BarSegment("Em uso", inUse, Hw.B("B.Accent")),
            new BarSegment("Modificada", modified, Hw.B("B.Warning")),
            new BarSegment("Em espera", standby, Hw.B("B.Cyan")),
            new BarSegment("Livre", free, Hw.B("B.BorderStrong")),
        };
        CompositionLegend = new[]
        {
            new LegendItem("Em uso", Fmt.Bytes(inUse), Hw.B("B.Accent"), "Total − disponível − modificada: páginas ativas de processos, kernel e drivers."),
            new LegendItem("Modificada", Fmt.Bytes(modified), Hw.B("B.Warning"), "Páginas alteradas que precisam ser gravadas em disco antes de reutilizadas."),
            new LegendItem("Em espera (cache)", Fmt.Bytes(standby), Hw.B("B.Cyan"), "Cache de dados recentes. Conta como DISPONÍVEL: é liberada instantaneamente quando um programa precisa."),
            new LegendItem("Livre", Fmt.Bytes(free), Hw.B("B.BorderStrong"), "Memória sem conteúdo útil (disponível − em espera)."),
            new LegendItem("Disponível", Fmt.Bytes(avail), Hw.B("B.Success"), "Em espera + livre (GlobalMemoryStatusEx)."),
        };
        CommitPct = s.CommitLimitBytes > 0 ? s.CommitBytes * 100.0 / s.CommitLimitBytes : 0;
        CommitText = s.CommitLimitBytes > 0 ? $"{Fmt.Bytes(s.CommitBytes)} de {Fmt.Bytes(s.CommitLimitBytes)} ({CommitPct:0}%)" : Hw.Unavailable;
        CacheText = Fmt.Bytes(s.CacheBytes);

        var w = Hist.Window(_live);
        UsageChart = new[]
        {
            Hw.Line("RAM em uso", Hist.Of(w, x => x.MemUsedPercent), Hw.B("B.Accent"), fill: true),
            Hw.Line("Commit", Hist.Of(w, x => x.CommitLimitBytes > 0 ? x.CommitBytes * 100.0 / x.CommitLimitBytes : 0), Hw.B("B.Violet")),
        };
        FaultsChart = new[]
        {
            Hw.Line("Hard faults/s", Hist.Of(w, x => x.HardFaultsPerSec), Hw.B("B.Warning"), fill: true),
            Hw.Line("Páginas lidas/s", Hist.Of(w, x => x.PagesInputPerSec), Hw.B("B.Danger")),
        };
        double avgFaults = w.Count > 0 ? w.Average(x => x.HardFaultsPerSec) : 0;
        FaultsLegend = new[]
        {
            new LegendItem("Leituras (hard faults)", $"{s.HardFaultsPerSec:0}/s", Hw.B("B.Warning"), "Memory\\Page Reads/sec: leituras de disco para resolver falhas de página."),
            new LegendItem("Páginas lidas", $"{s.PagesInputPerSec:0}/s", Hw.B("B.Danger"), "Memory\\Pages Input/sec."),
            new LegendItem("Média recente", $"{avgFaults:0.#}/s", Hw.B("B.TextFaint"), "Picos ao abrir programas são normais; valores altos e contínuos com pouca RAM disponível indicam falta de memória."),
        };

        UpdateSummary(s, avail, standby, avgFaults);
    }

    /// <summary>Resumo em linguagem simples (critérios do Diagnóstico: RAM ou commit ≥ 85%; paginação = média de hard faults &gt; 200/s).</summary>
    private void UpdateSummary(TelemetrySnapshot s, long avail, long standby, double avgFaults)
    {
        double used = s.MemUsedPercent;
        bool hasCommit = s.CommitLimitBytes > 0;
        double commit = hasCommit ? s.CommitBytes * 100.0 / s.CommitLimitBytes : 0;
        bool paging = avgFaults > 200;

        UsedFact.Set($"{used:0}%", $"{Fmt.Bytes(s.MemUsedBytes)} de {Fmt.Bytes(s.MemTotalBytes)} · {Words.Mem(used)}", Words.MemLevel(used));
        AvailFact.Set(Fmt.Bytes(avail), $"inclui {Fmt.Bytes(standby)} de cache", avail > 0 ? UxLevel.Ok : UxLevel.Unknown);
        if (hasCommit) CommitFact.Set($"{commit:0}%", $"{Fmt.Bytes(s.CommitBytes)} de {Fmt.Bytes(s.CommitLimitBytes)} · {Words.Mem(commit)}", Words.MemLevel(commit));
        else CommitFact.Unavailable("limite não informado");
        FaultsFact.Set($"{s.HardFaultsPerSec:0}/s", paging ? $"altas · média {avgFaults:0}/s" : $"normal · média {avgFaults:0}/s", paging ? UxLevel.Warn : UxLevel.Ok);

        UxLevel level = UxLevel.Ok;
        if ((hasCommit && commit >= 95) || (used >= 90 && paging)) level = UxLevel.Danger;
        else if (used >= 85 || (hasCommit && commit >= 85)) level = UxLevel.Warn;

        string headline = level switch
        {
            UxLevel.Danger => $"Está faltando memória: {used:0}% da RAM em uso e o Windows recorrendo ao disco.",
            UxLevel.Warn => $"A memória está {Words.Mem(Math.Max(used, commit))}: {used:0}% da RAM em uso.",
            _ => $"Memória {Words.Mem(used)}: {used:0}% em uso ({Fmt.Bytes(s.MemUsedBytes)} de {Fmt.Bytes(s.MemTotalBytes)}).",
        };
        string detail = (hasCommit ? $"Memória comprometida (commit) em {commit:0}% do limite. " : "")
                        + (paging ? "O Windows está lendo muito do disco para repor a RAM — feche programas que não está usando."
                           : level == UxLevel.Ok ? "Sem sinais de falta de memória." : "Feche o que não está usando se notar lentidão.");
        Hero.Set(level, headline, detail);
    }

    private Action? OnProcesses(ProcessSnapshot snap)
    {
        var top = snap.Processes.Where(p => p.Pid != 0).OrderByDescending(p => p.WorkingSetBytes).Take(25).Select(ProcRow.From).ToList();
        string status = $"Ordenados por memória em uso · clique nos títulos das colunas para reordenar · atualiza a cada 2 s";

        Action? leakUi = null;
        bool analyze = false;
        List<ProcessSnapshot> copy;
        lock (_windowLock)
        {
            if (_captureCount++ % AnalysisEvery == 0)
            {
                _window.Add(snap);
                while (_window.Count > WindowCapacity) _window.RemoveAt(0);
                analyze = _window.Count >= 2;
            }
            copy = analyze ? _window.ToList() : new List<ProcessSnapshot>();
        }
        if (analyze)
        {
            var report = BackgroundImpactAnalyzer.Analyze(copy, new ImpactAnalysisOptions { TopN = 10 });
            var span0 = report.To - report.From;
            if (span0 < TimeSpan.FromSeconds(60) || report.SnapshotCount < 4)
            {
                // Janela curta demais: a inclinação extrapolada seria enganosa — não listamos ainda.
                string wait = $"Observando… {report.SnapshotCount} amostra(s) em {Fmt.Duration(span0)}. A lista aparece após 1 minuto de observação; um alerta de vazamento só depois de 2 minutos.";
                return () =>
                {
                    TopProcs.Clear();
                    foreach (var r in top) TopProcs.Add(r);
                    ProcStatus = status;
                    Leaks.Clear();
                    LeakStatus = wait;
                };
            }
            var rows = report.TopByMemoryGrowth
                .Where(i => i.PrivateBytesSlopeBytesPerMin > 0)
                .Select(i => new LeakRow(i.Name, i.Key.Pid, Fmt.Duration(i.ObservedFor), i.PrivateBytesStart, i.PrivateBytesEnd,
                    i.PrivateBytesSlopeBytesPerMin / 1048576.0, i.GrowthMonotonicity, i.PossibleLeak, string.Join(" ", i.Notes)))
                .OrderByDescending(r => r.PossibleLeak).ThenByDescending(r => r.SlopeMbMin).Take(10).ToList();
            var span = report.To - report.From;
            int leaks = rows.Count(r => r.PossibleLeak);
            string ls = $"{report.SnapshotCount} amostras em {Fmt.Duration(span)} (1 a cada ~10 s, desde que a página foi aberta). "
                        + (leaks > 0 ? $"{leaks} processo(s) com padrão de possível vazamento." : "Nenhum processo atende a todos os critérios de vazamento.")
                        + (report.Notes.Count > 0 ? " " + string.Join(" ", report.Notes) : "");
            leakUi = () =>
            {
                Leaks.Clear();
                foreach (var r in rows) Leaks.Add(r);
                LeakStatus = ls;
            };
        }

        return () =>
        {
            TopProcs.Clear();
            foreach (var r in top) TopProcs.Add(r);
            ProcStatus = status;
            leakUi?.Invoke();
        };
    }

    private async Task LoadPageFileAsync()
    {
        try
        {
            var (rows, config, detail) = await Task.Run(PageFileReader.Read);
            PageFiles.Clear();
            foreach (var r in rows) PageFiles.Add(r);
            PageFileConfig = config;
            PageFileDetail = detail;
        }
        catch (Exception ex)
        {
            App.Log(ex, "Memória: arquivo de paginação");
            PageFileConfig = Hw.Unavailable;
            PageFileDetail = "Falha ao ler a configuração do arquivo de paginação: " + ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshPageFile() => LoadPageFileAsync();
}
