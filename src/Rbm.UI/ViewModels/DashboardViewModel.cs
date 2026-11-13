using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Rbm.Diagnostics;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

public sealed record InfoRow(string Label, string Value, string? Hint = null);

/// <summary>Ação recomendada em linguagem simples, derivada de um achado do diagnóstico.</summary>
public sealed record ActionItem(string Title, string Detail, string ButtonText, string NavigateKey, Severity Severity);

/// <summary>Área de saúde (categoria do índice) com palavra amigável no lugar de números técnicos.</summary>
public sealed record HealthArea(string Name, double? Score, string Word, string Tooltip);

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private readonly DiagnosticsState _diag;
    public override string Key => "dashboard";

    public ObservableCollection<InfoRow> Identity { get; } = new();
    public ObservableCollection<ActionItem> Actions { get; } = new();
    public ObservableCollection<HealthArea> Areas { get; } = new();

    // Resumo
    [ObservableProperty] private string _healthTitle = "Verificando seu PC…";
    [ObservableProperty] private string _healthSentence = "Estamos observando o uso do computador por alguns segundos para chegar a uma conclusão confiável.";
    [ObservableProperty] private string _healthKind = "unknown"; // ok | warn | danger | unknown
    [ObservableProperty] private string _healthChip = "Analisando";
    [ObservableProperty] private string _lastCheck = "";
    [ObservableProperty] private string _machineTitle = "";
    [ObservableProperty] private string _machineSubtitle = "";

    // Tempo real (linguagem simples)
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private double _gpu;
    [ObservableProperty] private double _ram;
    [ObservableProperty] private double _vram;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private string _cpuWord = "";
    [ObservableProperty] private string _gpuWord = "";
    [ObservableProperty] private string _ramWord = "";
    [ObservableProperty] private string _vramWord = "";
    [ObservableProperty] private string _cpuDetail = "";
    [ObservableProperty] private string _gpuDetail = "";
    [ObservableProperty] private string _ramDetail = "";
    [ObservableProperty] private string _vramDetail = "";
    [ObservableProperty] private double[] _cpuSeries = Array.Empty<double>();
    [ObservableProperty] private double[] _gpuSeries = Array.Empty<double>();
    [ObservableProperty] private double[] _ramSeries = Array.Empty<double>();
    [ObservableProperty] private double[] _vramSeries = Array.Empty<double>();
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _netText = "—";
    [ObservableProperty] private string _tempsText = "—";
    [ObservableProperty] private string _sensorNote = "";

    public DashboardViewModel(ISystemInventory inventory, LiveTelemetry live, DiagnosticsState diag)
    {
        _inventory = inventory; _live = live; _diag = diag;
        _diag.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiagnosticsState.LastRun)) Ui.Post(ApplyRun);
            if (e.PropertyName == nameof(DiagnosticsState.Running) && _diag.Running) Ui.Post(() => { HealthChip = "Analisando"; HealthKind = "unknown"; });
        };
    }

    public override async Task OnNavigatedToAsync()
    {
        BuildIdentity();
        _live.Updated += OnSample;
        if (_live.Latest is { } s) OnSample(s);
        ApplyRun();
        if (_diag.LastRun is null || DateTimeOffset.Now - _diag.LastRun.At > TimeSpan.FromMinutes(10))
        {
            for (int i = 0; i < 30 && _live.History.Count < 30; i++) await Task.Delay(1000);
            await _diag.RunAsync();
        }
    }

    public override void OnNavigatedFrom() => _live.Updated -= OnSample;

    private void BuildIdentity()
    {
        var p = _inventory.Current;
        if (p is null) return;
        MachineTitle = $"{p.Board.SystemManufacturer} {p.Board.SystemModel}".Trim() is { Length: > 0 } m && !m.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) && !m.Contains("System Product", StringComparison.OrdinalIgnoreCase)
            ? m : (p.FormFactor == FormFactor.Laptop ? "Seu notebook" : "Seu computador");
        MachineSubtitle = $"{p.Os.Caption.Replace("Microsoft ", "")} {p.Os.DisplayVersion} · ligado há {Fmt.Duration(p.Os.Uptime)}";
        Identity.Clear();
        Identity.Add(new("Processador", p.Cpu.Name, $"{p.Cpu.PhysicalCores} núcleos e {p.Cpu.LogicalProcessors} threads"));
        var g = p.PrimaryGpu;
        Identity.Add(new("Placa de vídeo", g?.Name ?? "—", g is null ? null : $"{Fmt.Bytes(g.DedicatedVideoMemoryBytes)} de memória de vídeo"));
        var mem = p.Memory;
        Identity.Add(new("Memória", $"{mem.TotalPhysicalBytes / 1073741824.0:0} GB {mem.MemoryType}".Trim(),
            $"{mem.ConfiguredSpeedMTs} MT/s · " + (mem.EstimatedChannels is { } ch ? (ch >= 2 ? "dual-channel (ideal)" : "canal único") : "configuração de canais não identificada")));
        Identity.Add(new("Armazenamento", string.Join(" · ", p.Volumes.Select(v => $"{v.Letter} {Fmt.Bytes(v.FreeBytes)} livres")),
            string.Join(" · ", p.Disks.Select(d => $"{d.Model} ({(d.MediaType == StorageMediaType.Hdd ? "HD" : d.MediaType == StorageMediaType.Ssd ? "SSD" : "disco")})"))));
        Identity.Add(new("Placa-mãe", $"{p.Board.Manufacturer} {p.Board.Product}".Trim(), $"BIOS {p.Board.BiosVersion} de {p.Board.BiosDate:d}"));
        Identity.Add(new("Energia", p.Power.ActiveSchemeName, p.Power.HasBattery ? $"Bateria {p.Power.BatteryPercent}% · {(p.Power.OnAcPower ? "na tomada" : "na bateria")}" : "Na tomada"));
    }

    private static string LoadWord(double pct) => pct < 30 ? "tranquilo" : pct < 70 ? "uso moderado" : pct < 90 ? "uso alto" : "uso intenso";
    private static string MemWord(double pct) => pct < 60 ? "folgada" : pct < 80 ? "confortável" : pct < 90 ? "ficando cheia" : "quase cheia";
    private static string TempWord(double c) => c < 70 ? "normal" : c < 85 ? "morna" : "quente";

    private void OnSample(TelemetrySnapshot s)
    {
        Cpu = s.CpuTotalPercent; CpuText = $"{s.CpuTotalPercent:0}%"; CpuWord = LoadWord(s.CpuTotalPercent);
        CpuDetail = s.CpuEffectiveClockMHz.Available ? $"{s.CpuEffectiveClockMHz.Value / 1000:0.0} GHz" : "";

        var g = s.Gpus.OrderByDescending(x => x.DedicatedTotalBytes).FirstOrDefault();
        if (g is not null)
        {
            Gpu = g.Utilization3DPercent; GpuText = $"{g.Utilization3DPercent:0}%"; GpuWord = LoadWord(g.Utilization3DPercent);
            GpuDetail = g.TemperatureC.Available ? $"{g.TemperatureC.Value:0} °C · {TempWord(g.TemperatureC.Value)}" : "";
            Vram = g.DedicatedTotalBytes > 0 ? g.DedicatedUsedBytes * 100.0 / g.DedicatedTotalBytes : 0;
            VramText = $"{Vram:0}%"; VramWord = MemWord(Vram);
            VramDetail = $"{Fmt.Bytes(g.DedicatedUsedBytes)} de {Fmt.Bytes(g.DedicatedTotalBytes)}";
        }
        Ram = s.MemUsedPercent; RamText = $"{s.MemUsedPercent:0}%"; RamWord = MemWord(s.MemUsedPercent);
        RamDetail = $"{Fmt.Bytes(s.MemUsedBytes)} de {Fmt.Bytes(s.MemTotalBytes)}";

        var temps = new List<string>();
        if (s.CpuTemperatureC.Available) temps.Add($"CPU {s.CpuTemperatureC.Value:0} °C");
        if (g?.TemperatureC.Available == true) temps.Add($"GPU {g.TemperatureC.Value:0} °C");
        TempsText = temps.Count > 0 ? string.Join(" · ", temps) : "indisponível";
        SensorNote = s.CpuTemperatureC.Available ? "" : "A temperatura do processador não pode ser lida sem um driver de sensores (PawnIO) e permissão de administrador.";
        DiskText = $"{Fmt.Rate(s.Disks.Sum(d => d.ReadBytesPerSec) + s.Disks.Sum(d => d.WriteBytesPerSec))}";
        NetText = $"↓ {s.NetRxBytesPerSec * 8 / 1e6:0.0} · ↑ {s.NetTxBytesPerSec * 8 / 1e6:0.0} Mb/s";

        CpuSeries = _live.Series("cpu"); GpuSeries = _live.Series("gpu"); RamSeries = _live.Series("ram"); VramSeries = _live.Series("vram");
    }

    private void ApplyRun()
    {
        var run = _diag.LastRun;
        if (run is null) return;
        var relevant = run.Findings.Where(f => f.Severity >= Severity.Low).ToList();
        int critical = relevant.Count(f => f.Severity >= Severity.High);
        int attention = relevant.Count(f => f.Severity == Severity.Medium);
        (HealthKind, HealthChip, HealthTitle, HealthSentence) = critical > 0
            ? ("danger", "Precisa de cuidado", $"Encontramos {critical} problema(s) importante(s)",
               "Resolver estes itens deve trazer mais estabilidade e desempenho do que qualquer ajuste fino. Veja abaixo o que fazer.")
            : attention > 0
                ? ("warn", "Atenção", $"Seu PC está bem, com {attention} ponto(s) de atenção",
                   "Nada grave, mas há melhorias simples que valem a pena.")
                : ("ok", "Tudo certo", "Seu PC está saudável",
                   "Não encontramos problemas relevantes. Para ganhar desempenho, experimente um perfil de otimização seguro.");
        LastCheck = $"Verificado às {run.At:HH:mm}";

        Actions.Clear();
        foreach (var f in relevant.Take(4))
            Actions.Add(new ActionItem(f.Title, FirstSentence(f.Solution), ButtonFor(f.NavigateTo), KeyFor(f.NavigateTo), f.Severity));

        Areas.Clear();
        foreach (var c in run.Index.Categories)
        {
            var word = c.Score switch { null => "sem dados", >= 85 => "ótimo", >= 65 => "bom", >= 45 => "atenção", _ => "crítico" };
            var tip = string.Join("\n", c.Factors.Select(x => $"• {x.Name}: {x.Measured}")) + $"\n\nConfiança: {c.Confidence.Pt()} (fração dos dados disponíveis).";
            Areas.Add(new HealthArea(c.Name, c.Score, word, tip));
        }
    }

    private static string FirstSentence(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var i = s.IndexOfAny(new[] { '.', ';' });
        return i > 20 && i < 200 ? s[..(i + 1)] : (s.Length > 200 ? s[..200] + "…" : s);
    }

    public static string KeyFor(string? nav) => nav switch
    {
        "Cleanup" => "cleanup", "Storage" => "storage", "Processes" => "processes", "Memory" => "memory", "Thermal" or "Power" => "power",
        "Latency" => "latency", "Drivers" => "drivers", "Network" => "network", "Startup" => "startup", "Gaming" => "gaming",
        _ => "diagnostics",
    };

    public static string ButtonFor(string? nav) => nav switch
    {
        "Cleanup" => "Liberar espaço", "Storage" => "Ver discos", "Processes" => "Ver programas", "Memory" => "Ver memória",
        "Thermal" or "Power" => "Ver temperaturas", "Latency" => "Analisar", "Drivers" => "Ver drivers", "Network" => "Ver rede",
        "Startup" => "Ver inicialização", "Gaming" => "Ver jogos", _ => "Ver detalhes",
    };

    [RelayCommand]
    private async Task RunDiagnostics() => await RunBusy("Verificando seu PC…", () => _diag.RunAsync());

    [RelayCommand]
    private void Go(string key) => App.Services.GetRequiredService<MainViewModel>().Navigate(key);
}
