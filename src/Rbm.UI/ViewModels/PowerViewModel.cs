using System.Collections.ObjectModel;
using System.Windows.Media;
using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Hardware;
using Rbm.Optimization.Providers;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

public sealed record PlanRow(Guid Guid, string Name, bool IsActive, string Hint)
{
    public bool CanActivate => !IsActive;
    public string GuidText => Guid.ToString();
}

/// <summary>Energia e temperatura: planos (ativação confirmada), tomada/bateria, temperaturas e limitação.</summary>
public sealed partial class PowerViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private bool _visible;
    private int _sensorKey;
    public override string Key => "power";

    public ObservableCollection<PlanRow> Plans { get; } = new();
    public ObservableCollection<SensorPanel> TempPanels { get; } = new();

    [ObservableProperty] private string _activePlanName = "—";
    [ObservableProperty] private string _activePlanHint = "";
    [ObservableProperty] private string _planStatus = "";
    [ObservableProperty] private bool _isActivating;
    [ObservableProperty] private string _acText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private double _batteryPct;
    [ObservableProperty] private string _saverText = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries> _limitChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _limitLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private bool _limitAvailable;
    [ObservableProperty] private string _limitStatus = "";
    [ObservableProperty] private Brush _limitBrush = Brushes.Gray;
    [ObservableProperty] private string _sensorReason = "";

    // ---- Resumo em linguagem simples ----
    public HeroState Hero { get; } = new() { ChipTip = "Atenção quando o processador fica limitado (abaixo de 90%) em pelo menos 1/4 do tempo, a economia de energia está ativa ou uma temperatura passa do normal (CPU ≥ 90 °C, GPU ≥ 85 °C)." };
    public Fact PlanFact { get; } = new("Plano de energia", "Define como o Windows equilibra desempenho e consumo.");
    public Fact SourceFact { get; } = new("Alimentação", "Fonte de energia atual (tomada ou bateria), lida a cada atualização.");
    public Fact LimitFact { get; } = new("Limitação do processador", "100% = sem limitação. Abaixo disso, calor ou limites de energia estão segurando o processador.");
    public Fact TempFact { get; } = new("Mais quente agora", "Maior temperatura entre os sensores disponíveis (CPU e GPU).");
    [ObservableProperty] private bool _sensorMissing;
    [ObservableProperty] private bool _activePlanIsSaver;

    public PowerViewModel(ISystemInventory inventory, LiveTelemetry live)
    {
        _inventory = inventory; _live = live;
    }

    public override async Task OnNavigatedToAsync()
    {
        _visible = true;
        _sensorKey = 0;
        _live.Updated += OnSample;
        ReadPowerStatus();
        if (_live.Latest is { } s) OnSample(s);
        await LoadPlansAsync();
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
    }

    private static string PlanHint(Guid g) =>
        g == PowerSchemes.Balanced ? "Recomendado na maioria dos casos: o processador acelera (boost) sob carga e economiza em repouso."
        : g == PowerSchemes.HighPerformance ? "Mantém estados de desempenho mais altos: menos variação de clock, porém mais consumo e calor em repouso."
        : g == PowerSchemes.UltimatePerformance ? "Variante do Alto desempenho para estações de trabalho; elimina micro-economias de energia. Ganho em jogos costuma ser pequeno ou nulo."
        : g == PowerSchemes.PowerSaver ? "Reduz desempenho para economizar energia (limita o processador)."
        : "Plano personalizado ou do fabricante.";

    private async Task LoadPlansAsync()
    {
        try
        {
            var (list, active) = await Task.Run(() => (PowerApi.ListSchemes(), PowerApi.GetActiveScheme()));
            Plans.Clear();
            foreach (var (g, n) in list.OrderByDescending(x => x.Guid == active).ThenBy(x => x.Name))
                Plans.Add(new PlanRow(g, n, g == active, PlanHint(g)));
            var a = list.FirstOrDefault(x => x.Guid == active);
            ActivePlanName = a.Name ?? PowerApi.FriendlyName(active);
            ActivePlanHint = PlanHint(active);
            ActivePlanIsSaver = active == PowerSchemes.PowerSaver;
            PlanStatus = $"{list.Count} plano(s) disponíveis · lido às {DateTime.Now:T}";
            // Mantém o inventário coerente para as outras páginas (ex.: Dashboard).
            if (_inventory.Current is { } p) { p.Power.ActiveSchemeGuid = active; p.Power.ActiveSchemeName = ActivePlanName; }
        }
        catch (Exception ex)
        {
            App.Log(ex, "Energia: listar planos");
            PlanStatus = "Falha ao listar planos de energia: " + ex.Message;
        }
        UpdateSummary();
    }

    [RelayCommand]
    private Task RefreshPlans() => LoadPlansAsync();

    [RelayCommand]
    private async Task ActivatePlan(PlanRow? row)
    {
        if (row is null || row.IsActive || IsActivating) return;
        bool ok = await App.Dialogs.ConfirmAsync("Ativar plano de energia",
            $"Ativar o plano \"{row.Name}\"?",
            $"{row.Hint}\n\nA troca vale imediatamente e pode ser desfeita a qualquer momento ativando o plano anterior (\"{ActivePlanName}\").",
            confirmText: "Ativar");
        if (!ok) return;
        IsActivating = true;
        try
        {
            using var _ = App.Tasks.Begin("Ativando plano de energia");
            await Task.Run(() => PowerApi.SetActive(row.Guid));
            App.Notify.Success("Plano de energia ativado", row.Name);
            Serilog.Log.Information("Plano de energia ativado pelo usuário: {Name} ({Guid})", row.Name, row.Guid);
        }
        catch (Exception ex)
        {
            App.Log(ex, $"Energia: ativar plano {row.Guid}");
            App.Notify.Error("Não foi possível ativar o plano", ex.Message + " — a política do sistema pode exigir privilégios de administrador.");
        }
        finally
        {
            IsActivating = false;
            await LoadPlansAsync();
        }
    }

    [RelayCommand]
    private void OpenPowerOptions() => Hw.ShellOpen(ProcessRunner.System32("control.exe"), "/name Microsoft.PowerOptions", "Opções de Energia");

    private void ReadPowerStatus()
    {
        var st = PowerStatus.Read();
        if (!st.Known) { AcText = Hw.Unavailable; BatteryText = "GetSystemPowerStatus falhou."; HasBattery = false; SaverText = ""; return; }
        AcText = st.OnAc switch { true => "Na tomada (CA)", false => "Na bateria", _ => "Fonte desconhecida" };
        HasBattery = st.HasBattery;
        if (!st.HasBattery) { BatteryText = "Sem bateria detectada (desktop ou bateria removida)."; BatteryPct = 0; }
        else
        {
            BatteryPct = st.Percent ?? 0;
            var parts = new List<string> { st.Percent is { } p ? $"{p}%" : "carga desconhecida" };
            if (st.Charging) parts.Add("carregando");
            if (st.Remaining is { } r) parts.Add($"~{Fmt.Duration(r)} restantes (estimativa do Windows)");
            BatteryText = string.Join(" · ", parts);
        }
        SaverText = st.Saver ? "Economia de bateria/energia ativa: o Windows pode limitar o desempenho e atividades em segundo plano." : "";
    }

    private void OnSample(TelemetrySnapshot s)
    {
        if (!_visible) return;
        ReadPowerStatus();
        var w = Hist.Window(_live);
        string reason = Hw.SensorReason();
        SensorReason = reason;

        // Painéis de temperatura: recriados apenas se a composição de dispositivos mudar.
        int key = HashCode.Combine(s.Gpus.Count, string.Join("|", s.Gpus.Select(g => g.AdapterLuid)), string.Join("|", s.Disks.Select(d => d.Instance)));
        if (key != _sensorKey)
        {
            _sensorKey = key;
            TempPanels.Clear();
            TempPanels.Add(new SensorPanel("CPU", "°C", "Temperatura do processador (pacote/Tctl) e potência via LibreHardwareMonitor."));
            foreach (var g in s.Gpus) TempPanels.Add(new SensorPanel("GPU · " + g.Name, "°C", "Temperatura do núcleo e hotspot da GPU via LibreHardwareMonitor."));
            foreach (var d in s.Disks) TempPanels.Add(new SensorPanel("Disco " + DiskLabel(d.Instance), "°C", "Temperatura SMART do disco via LibreHardwareMonitor."));
        }
        int i = 0;
        TempPanels[i++].Set(new[]
        {
            ("Temperatura", Hist.Avail(w, x => x.CpuTemperatureC), s.CpuTemperatureC, Hw.B("B.Warning")),
        }, "Indisponível. " + reason, extraLegend: new[]
        {
            new LegendItem("Potência do pacote", s.CpuPackagePowerW.Available ? $"{s.CpuPackagePowerW.Value:0.#} W" : Hw.Unavailable, Hw.B("B.TextFaint")),
        });
        foreach (var g in s.Gpus)
        {
            long luid = g.AdapterLuid;
            GpuTelemetry? G(TelemetrySnapshot x) => x.Gpus.FirstOrDefault(y => y.AdapterLuid == luid);
            if (i >= TempPanels.Count) break;
            TempPanels[i++].Set(new[]
            {
                ("Núcleo", Hist.Avail(w, x => G(x)?.TemperatureC ?? Reading<double>.None), g.TemperatureC, Hw.B("B.Warning")),
                ("Hotspot", Hist.Avail(w, x => G(x)?.HotspotC ?? Reading<double>.None), g.HotspotC, Hw.B("B.Danger")),
            }, "Sensor não exposto para este adaptador. " + reason);
        }
        foreach (var d in s.Disks)
        {
            string inst = d.Instance;
            if (i >= TempPanels.Count) break;
            TempPanels[i++].Set(new[]
            {
                ("Temperatura", Hist.Avail(w, x => x.Disks.FirstOrDefault(y => y.Instance == inst)?.TemperatureC ?? Reading<double>.None), d.TemperatureC, Hw.B("B.Cyan")),
            }, "Indisponível: a leitura SMART de temperatura normalmente exige execução elevada. " + reason);
        }

        // Limitação (throttling)
        LimitAvailable = s.CpuPerformanceLimitPercent.Available;
        if (LimitAvailable)
        {
            var v = Hist.Avail(w, x => x.CpuPerformanceLimitPercent);
            LimitChart = new[] { Hw.Line("% Performance Limit", v, Hw.B("B.Warning"), fill: true) };
            double cur = s.CpuPerformanceLimitPercent.Value;
            int limited = v.Count(x => x < 99.5);
            LimitLegend = new[]
            {
                new LegendItem("Agora", $"{cur:0}%", Hw.B("B.Warning"), "100% = sem limitação."),
                new LegendItem("Mínimo na janela", v.Length > 0 ? $"{v.Min():0}%" : "—", Hw.B("B.Danger")),
                new LegendItem("Amostras limitadas", $"{limited} de {v.Length}", Hw.B("B.TextFaint")),
                new LegendItem("Clock efetivo", s.CpuEffectiveClockMHz.Available ? Hw.Mhz(s.CpuEffectiveClockMHz.Value) : Hw.Unavailable, Hw.B("B.Cyan")),
            };
            if (cur < 99.5) { LimitStatus = "A CPU está limitada agora (térmica, energia ou plataforma)."; LimitBrush = Hw.B("B.Warning"); }
            else if (limited > 0) { LimitStatus = "Sem limitação agora, mas houve limitação na janela observada."; LimitBrush = Hw.B("B.Info"); }
            else { LimitStatus = "Sem limitação na janela observada."; LimitBrush = Hw.B("B.Success"); }
        }
        else
        {
            LimitChart = Array.Empty<ChartSeries>();
            LimitLegend = Array.Empty<LegendItem>();
            LimitStatus = "Contador \"% Performance Limit\" indisponível neste sistema.";
            LimitBrush = Hw.B("B.TextMuted");
        }

        _last = s;
        UpdateSummary();
    }

    private TelemetrySnapshot? _last;

    /// <summary>Resumo em linguagem simples (critérios do Diagnóstico: limite &lt; 90% em ≥ 25% das amostras; temperaturas de CPU ≥ 90 °C e GPU ≥ 85 °C).</summary>
    private void UpdateSummary()
    {
        PlanFact.Set(ActivePlanName, ActivePlanIsSaver ? "limita o desempenho" : "plano ativo", ActivePlanIsSaver ? UxLevel.Info : UxLevel.Ok);
        bool saver = SaverText.Length > 0;
        SourceFact.Set(AcText, HasBattery ? BatteryText : saver ? "economia de energia ativa" : "sem bateria", saver ? UxLevel.Warn : AcText == Hw.Unavailable ? UxLevel.Unknown : UxLevel.Ok);

        var s = _last;
        UxLevel limLevel = UxLevel.Unknown, tempLevel = UxLevel.Unknown;
        string limPart = "";
        if (s is not null && s.CpuPerformanceLimitPercent.Available)
        {
            var lim = Hist.Window(_live).Where(x => x.CpuPerformanceLimitPercent.Available).Select(x => x.CpuPerformanceLimitPercent.Value).ToList();
            double frac = lim.Count > 0 ? lim.Count(x => x < 90) / (double)lim.Count : 0;
            bool sustained = lim.Count >= 10 && frac >= 0.25;
            double cur = s.CpuPerformanceLimitPercent.Value;
            limLevel = sustained ? UxLevel.Warn : UxLevel.Ok;
            LimitFact.Set($"{cur:0}%", sustained ? $"limitado em {frac:P0} do tempo" : cur < 99.5 ? "contido agora, sem padrão contínuo" : "sem limitação", sustained ? UxLevel.Warn : cur < 99.5 ? UxLevel.Info : UxLevel.Ok);
            limPart = sustained ? $"O processador está sendo limitado em {frac:P0} do tempo — verifique a refrigeração." : "O processador não está sendo limitado.";
        }
        else LimitFact.Unavailable("contador não disponível neste PC");

        var temps = new List<(string name, double c, UxLevel l)>();
        if (s is not null)
        {
            if (s.CpuTemperatureC.Available) temps.Add(("CPU", s.CpuTemperatureC.Value, Words.CpuTempLevel(s.CpuTemperatureC.Value)));
            foreach (var g in s.Gpus.Where(g => g.TemperatureC.Available))
                temps.Add(("GPU", g.TemperatureC.Value, UxLevels.Worst(Words.GpuTempLevel(g.TemperatureC.Value), g.HotspotC.Available ? Words.HotspotLevel(g.HotspotC.Value) : UxLevel.Ok)));
            SensorMissing = !s.CpuTemperatureC.Available;
        }
        if (temps.Count > 0)
        {
            var hot = temps.OrderByDescending(t => t.c).First();
            tempLevel = UxLevels.Worst(temps.Select(t => t.l).ToArray());
            TempFact.Set($"{hot.c:0} °C", string.Join(" · ", temps.Select(t => $"{t.name} {t.c:0} °C")) + $" · {Words.Temp(hot.c)}", tempLevel);
        }
        else TempFact.Unavailable("sem sensores disponíveis");

        var level = UxLevels.Worst(limLevel, tempLevel, saver ? UxLevel.Warn : UxLevel.Ok);
        string src = AcText switch { "Na tomada (CA)" => "ligado na tomada", "Na bateria" => "na bateria", _ => "" };
        string headline = ActivePlanName == "—" ? "Lendo o plano de energia…"
            : $"Você está no plano {ActivePlanName}{(src.Length > 0 ? ", " + src : "")}.";
        var parts = new List<string>();
        if (limPart.Length > 0) parts.Add(limPart);
        if (temps.Count > 0) parts.Add($"Temperaturas: {string.Join(", ", temps.Select(t => $"{t.name} {t.c:0} °C"))} ({Words.Temp(temps.Max(t => t.c))}).");
        if (saver) parts.Add("A economia de energia está ativa e pode reduzir o desempenho.");
        Hero.Set(level, headline, string.Join(" ", parts));
    }

    private string DiskLabel(string instance)
    {
        var parts = instance.Split(' ', 2);
        if (int.TryParse(parts[0], out var n) && _inventory.Current?.Disks.FirstOrDefault(x => x.Number == n) is { } disk)
            return $"{n} · {disk.Model}";
        return instance;
    }
}
