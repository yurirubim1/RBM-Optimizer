using System.Collections.ObjectModel;
using System.IO;
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

public sealed record DiskRow(int Number, string Model, string Media, string Bus, long Size, string Firmware, string Health, string Operational, bool IsSystem, Brush HealthBrush)
{
    public string SizeText => Fmt.Bytes(Size);
    public string SystemText => IsSystem ? "Sim" : "";
    public string SystemNote => IsSystem ? " · Windows instalado aqui" : "";
    public string Kind => $"{(Media == "Não informado" ? "Disco" : Media)}{(Bus == "Não informado" ? "" : " " + Bus)} · {SizeText}";
}

public sealed record VolumeRow(string Letter, string Label, string FileSystem, long Total, long Free, double UsedPct, Brush Bar, string Detail, string? Warning, bool IsSystem = false)
{
    public string Title => string.IsNullOrWhiteSpace(Label) ? Letter : $"{Letter} {Label}";
}

public sealed record DiskOption(string Instance, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Armazenamento: discos físicos, volumes, E/S ao vivo por disco, TRIM, otimização e processos de E/S.</summary>
public sealed partial class StorageViewModel : PageViewModel
{
    private readonly ISystemInventory _inventory;
    private readonly LiveTelemetry _live;
    private readonly ProcessPoller _poller = new();
    private bool _visible;
    public override string Key => "storage";

    public ObservableCollection<DiskRow> Disks { get; } = new();
    public ObservableCollection<VolumeRow> Volumes { get; } = new();
    public ObservableCollection<DiskOption> DiskOptions { get; } = new();
    public ObservableCollection<ProcRow> TopIo { get; } = new();

    [ObservableProperty] private DiskOption? _selectedDisk;
    [ObservableProperty] private bool _hasLiveDisks;
    [ObservableProperty] private IReadOnlyList<ChartSeries> _activeChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _rateChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<ChartSeries> _latencyChart = Array.Empty<ChartSeries>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _activeLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _rateLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private IReadOnlyList<LegendItem> _latencyLegend = Array.Empty<LegendItem>();
    [ObservableProperty] private string _trimNtfs = "Verificando…";
    [ObservableProperty] private string _trimRefs = "";
    [ObservableProperty] private string _trimDetail = "";
    [ObservableProperty] private Brush _trimBrush = Brushes.Gray;
    [ObservableProperty] private string _procStatus = "Coletando processos…";
    [ObservableProperty] private string _diskStatus = "";

    // ---- Resumo em linguagem simples ----
    public HeroState Hero { get; } = new() { ChipTip = "Problema quando o Windows marca um disco como não saudável. Atenção com disco em alerta, unidade com menos de 10% livre ou TRIM desativado." };
    public Fact SpaceFact { get; } = new("Espaço livre (Windows)", "Espaço livre na unidade onde o Windows está instalado. Abaixo de ~10%, atualizações e o desempenho podem ser afetados.");
    public Fact HealthFact { get; } = new("Saúde dos discos", "Estado resumido que o próprio Windows informa para cada disco físico (HealthStatus).");
    public Fact ActivityFact { get; } = new("Atividade agora", "Tempo ativo do disco mais ocupado. Perto de 100% o tempo todo = o disco está sendo o gargalo.");
    public Fact TrimFact { get; } = new("Manutenção de SSD (TRIM)", "Com o TRIM ativo, o Windows avisa o SSD sobre dados apagados — mantém o desempenho de gravação e a vida útil.");
    [ObservableProperty] private bool _hasVolumes;
    private bool? _trimEnabled;

    public StorageViewModel(ISystemInventory inventory, LiveTelemetry live)
    {
        _inventory = inventory; _live = live;
    }

    public override async Task OnNavigatedToAsync()
    {
        _visible = true;
        BuildDisks();
        _live.Updated += OnSample;
        if (_live.Latest is { } s) OnSample(s);
        _poller.Start(OnProcesses, "Armazenamento: processos");
        await Task.WhenAll(LoadVolumesAsync(), LoadTrimAsync());
    }

    public override void OnNavigatedFrom()
    {
        _visible = false;
        _live.Updated -= OnSample;
        _poller.Stop();
    }

    private void BuildDisks()
    {
        var p = _inventory.Current;
        Disks.Clear();
        if (p is null) { DiskStatus = "Inventário ainda não coletado."; return; }
        foreach (var d in p.Disks)
        {
            var brush = d.HealthStatus switch { "Saudável" => Hw.B("B.Success"), "Atenção" => Hw.B("B.Warning"), "Não saudável" => Hw.B("B.Danger"), _ => Hw.B("B.TextMuted") };
            Disks.Add(new DiskRow(d.Number, d.Model, Media(d.MediaType), Bus(d.BusType), d.SizeBytes, string.IsNullOrWhiteSpace(d.Firmware) ? "—" : d.Firmware,
                string.IsNullOrWhiteSpace(d.HealthStatus) ? "—" : d.HealthStatus, string.IsNullOrWhiteSpace(d.OperationalStatus) ? "—" : d.OperationalStatus, d.IsSystemDisk, brush));
        }
        DiskStatus = Disks.Count == 0 ? "Nenhum disco físico retornado pela Storage Management API (MSFT_PhysicalDisk)." : $"{Disks.Count} disco(s) · Storage Management API (MSFT_PhysicalDisk)";
        UpdateSummary();
    }

    private static string Media(StorageMediaType m) => m switch { StorageMediaType.Ssd => "SSD", StorageMediaType.Hdd => "HDD", StorageMediaType.Scm => "SCM", _ => "Não informado" };
    private static string Bus(StorageBusType b) => b switch
    {
        StorageBusType.Nvme => "NVMe", StorageBusType.Sata => "SATA", StorageBusType.Usb => "USB", StorageBusType.Sas => "SAS",
        StorageBusType.Raid => "RAID", StorageBusType.Virtual => "Virtual", StorageBusType.Other => "Outro", _ => "Não informado",
    };

    private async Task LoadVolumesAsync()
    {
        try
        {
            var known = _inventory.Current?.Volumes ?? new List<VolumeInfo>();
            var rows = await Task.Run(() =>
            {
                var list = new List<VolumeRow>();
                string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string letter = d.Name.TrimEnd('\\');
                    long total = d.TotalSize, free = d.AvailableFreeSpace;
                    double used = total > 0 ? (total - free) * 100.0 / total : 0;
                    double freePct = 100 - used;
                    var info = known.FirstOrDefault(v => v.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
                    string disk = info?.DiskNumber is { } n ? $"disco {n}" : "disco não mapeado";
                    bool isSys = letter.Equals(sys, StringComparison.OrdinalIgnoreCase);
                    string? warn = freePct < 10 ? "Pouco espaço livre: abaixo de ~10% o Windows Update, arquivos temporários e (em SSDs) o desempenho de gravação podem ser afetados." : null;
                    list.Add(new VolumeRow(letter, d.VolumeLabel, d.DriveFormat, total, free, used, Brushes.Transparent,
                        $"{Fmt.Bytes(free)} livres de {Fmt.Bytes(total)} ({freePct:0}%) · {d.DriveFormat} · {disk}{(isSys ? " · Windows instalado aqui" : "")}", warn, isSys));
                }
                return list;
            });
            Volumes.Clear();
            foreach (var r in rows)
            {
                double freePct = 100 - r.UsedPct;
                // Pincéis do tema são resolvidos na thread de UI.
                Volumes.Add(r with { Bar = freePct < 10 ? Hw.B("B.Danger") : freePct < 20 ? Hw.B("B.Warning") : Hw.B("B.CyanGradient") });
            }
            HasVolumes = Volumes.Count > 0;
        }
        catch (Exception ex)
        {
            App.Log(ex, "Armazenamento: volumes");
            App.Notify.Error("Falha ao ler volumes", ex.Message);
        }
        UpdateSummary();
    }

    /// <summary>Resumo em linguagem simples a partir dos dados já lidos (saúde do Windows, espaço livre, TRIM).</summary>
    private void UpdateSummary()
    {
        // Espaço livre
        var sys = Volumes.FirstOrDefault(v => v.IsSystem) ?? Volumes.FirstOrDefault();
        UxLevel spaceLevel = UxLevel.Unknown;
        if (sys is not null)
        {
            double freePct = 100 - sys.UsedPct;
            spaceLevel = freePct < 10 ? UxLevel.Warn : UxLevel.Ok;
            SpaceFact.Set(Fmt.Bytes(sys.Free), $"{freePct:0}% livre em {sys.Letter}" + (freePct < 10 ? " · pouco espaço" : freePct < 20 ? " · ficando cheio" : ""), freePct < 20 ? UxLevel.Warn : UxLevel.Ok);
        }
        else SpaceFact.Unavailable("unidades ainda não lidas");
        var lowVols = Volumes.Where(v => 100 - v.UsedPct < 10).ToList();
        if (lowVols.Count > 0) spaceLevel = UxLevel.Warn;

        // Saúde
        var bad = Disks.FirstOrDefault(d => d.Health == "Não saudável");
        var warn = Disks.FirstOrDefault(d => d.Health == "Atenção");
        UxLevel healthLevel = bad is not null ? UxLevel.Danger : warn is not null ? UxLevel.Warn : Disks.Count > 0 ? UxLevel.Ok : UxLevel.Unknown;
        if (Disks.Count == 0) HealthFact.Unavailable("nenhum disco listado");
        else
        {
            int healthy = Disks.Count(d => d.Health == "Saudável");
            HealthFact.Set(bad is not null ? "Com problema" : warn is not null ? "Atenção" : healthy == Disks.Count ? "Saudáveis" : "Sem alerta",
                $"{healthy} de {Disks.Count} disco(s) saudável(is)", healthLevel == UxLevel.Unknown ? UxLevel.Ok : healthLevel);
        }

        // TRIM (só é relevante com SSD)
        bool hasSsd = Disks.Any(d => d.Media == "SSD");
        UxLevel trimLevel = _trimEnabled == false && hasSsd ? UxLevel.Warn : UxLevel.Unknown;
        if (_trimEnabled is null) TrimFact.Unavailable(TrimNtfs == "Verificando…" ? "verificando…" : "estado não informado");
        else TrimFact.Set(_trimEnabled == true ? "Ativado" : "Desativado",
            _trimEnabled == true ? "recomendado" : hasSsd ? "o SSD perde desempenho com o tempo" : "sem SSD detectado", _trimEnabled == true ? UxLevel.Ok : trimLevel == UxLevel.Warn ? UxLevel.Warn : UxLevel.Info);

        var level = UxLevels.Worst(spaceLevel, healthLevel, trimLevel);
        string volList = string.Join(" · ", Volumes.Select(v => $"{v.Letter} {Fmt.Bytes(v.Free)} livres"));
        string headline;
        if (bad is not null) headline = $"O disco {bad.Model} está com problema de saúde.";
        else if (warn is not null) headline = $"O disco {warn.Model} pede atenção (alerta do Windows).";
        else if (lowVols.Count > 0) headline = $"Pouco espaço livre em {string.Join(", ", lowVols.Select(v => v.Letter))} — {Fmt.Bytes(lowVols[0].Free)} livres ({100 - lowVols[0].UsedPct:0}%).";
        else if (trimLevel == UxLevel.Warn) headline = "O TRIM está desativado: seu SSD pode perder desempenho com o tempo.";
        else if (sys is not null) headline = (Disks.Count > 0 ? "Seus discos estão saudáveis" : "Suas unidades estão ok") + $" e o {sys.Letter} tem {Fmt.Bytes(sys.Free)} livres ({100 - sys.UsedPct:0}%).";
        else headline = Disks.Count > 0 ? "Seus discos estão saudáveis." : "Lendo discos e unidades…";
        string detail = bad is not null || warn is not null
            ? "Faça backup dos seus arquivos importantes e confira o disco com a ferramenta do fabricante."
            : lowVols.Count > 0 ? "Libere espaço apagando temporários e arquivos grandes que não usa mais. " + volList
            : volList;
        Hero.Set(level == UxLevel.Unknown && (Disks.Count > 0 || Volumes.Count > 0) ? UxLevel.Ok : level, headline, detail);
    }

    private async Task LoadTrimAsync()
    {
        try
        {
            var r = await TrimStatusReader.ReadAsync();
            TrimNtfs = r.Ntfs; TrimRefs = r.Refs;
            TrimBrush = r.NtfsEnabled switch { true => Hw.B("B.Success"), false => Hw.B("B.Danger"), _ => Hw.B("B.TextMuted") };
            TrimDetail = !r.Ok ? r.Raw
                : r.NtfsEnabled == false
                    ? "O TRIM está desativado para NTFS (DisableDeleteNotify = 1). Em SSDs isso reduz o desempenho de gravação e a longevidade com o tempo. Para reativar: \"fsutil behavior set DisableDeleteNotify 0\" (como administrador)."
                    : "DisableDeleteNotify = 0 significa que o Windows envia comandos TRIM ao SSD ao apagar dados — o estado recomendado.";
            _trimEnabled = r.NtfsEnabled;
        }
        catch (Exception ex)
        {
            App.Log(ex, "Armazenamento: TRIM");
            TrimNtfs = Hw.Unavailable; TrimRefs = ""; TrimBrush = Hw.B("B.TextMuted");
            TrimDetail = "Falha ao executar fsutil: " + ex.Message;
            _trimEnabled = null;
        }
        UpdateSummary();
    }

    partial void OnSelectedDiskChanged(DiskOption? value) { if (_visible && _live.Latest is { } s) OnSample(s); }

    private void OnSample(TelemetrySnapshot s)
    {
        if (!_visible) return;
        // Atualiza a lista de discos ao vivo (instâncias PDH "0 C:", "1 D:"…).
        var insts = s.Disks.Select(d => d.Instance).ToList();
        if (!insts.SequenceEqual(DiskOptions.Select(o => o.Instance)))
        {
            var prev = SelectedDisk?.Instance;
            DiskOptions.Clear();
            foreach (var i in insts) DiskOptions.Add(new DiskOption(i, LabelFor(i)));
            SelectedDisk = DiskOptions.FirstOrDefault(o => o.Instance == prev) ?? DiskOptions.FirstOrDefault();
        }
        HasLiveDisks = DiskOptions.Count > 0;
        if (s.Disks.Count > 0)
        {
            var busiest = s.Disks.OrderByDescending(x => x.ActivePercent).First();
            double act = busiest.ActivePercent;
            ActivityFact.Set($"{act:0}%", $"lendo {Fmt.Rate(s.Disks.Sum(x => x.ReadBytesPerSec))} · gravando {Fmt.Rate(s.Disks.Sum(x => x.WriteBytesPerSec))}", act >= 90 ? UxLevel.Info : UxLevel.Ok);
        }
        else ActivityFact.Unavailable("contadores de disco indisponíveis");
        var sel = SelectedDisk;
        if (sel is null) return;
        string inst = sel.Instance;
        var d = s.Disks.FirstOrDefault(x => x.Instance == inst);
        if (d is null) return;
        var w = Hist.Window(_live);
        DiskTelemetry? D(TelemetrySnapshot x) => x.Disks.FirstOrDefault(y => y.Instance == inst);

        ActiveChart = new[] { Hw.Line("Ativo", Hist.Of(w, x => D(x)?.ActivePercent ?? 0), Hw.B("B.Cyan"), fill: true) };
        ActiveLegend = new[]
        {
            new LegendItem("Tempo ativo", $"{d.ActivePercent:0}%", Hw.B("B.Cyan"), "100 − % Idle Time. Perto de 100% de forma contínua = disco é o gargalo."),
            new LegendItem("Fila", $"{d.QueueLength:0}", Hw.B("B.TextFaint"), "Current Disk Queue Length."),
            new LegendItem("Temperatura", d.TemperatureC.Available ? $"{d.TemperatureC.Value:0} °C" : Hw.Unavailable, Hw.B("B.Warning"),
                d.TemperatureC.Available ? "LibreHardwareMonitor (SMART)." : "Indisponível. " + Hw.SensorReason()),
        };
        RateChart = new[]
        {
            Hw.Line("Leitura", Hist.Of(w, x => (D(x)?.ReadBytesPerSec ?? 0) / 1048576.0), Hw.B("B.Success"), fill: true),
            Hw.Line("Gravação", Hist.Of(w, x => (D(x)?.WriteBytesPerSec ?? 0) / 1048576.0), Hw.B("B.Warning")),
        };
        RateLegend = new[]
        {
            new LegendItem("Leitura", Fmt.Rate(d.ReadBytesPerSec), Hw.B("B.Success")),
            new LegendItem("Gravação", Fmt.Rate(d.WriteBytesPerSec), Hw.B("B.Warning")),
        };
        LatencyChart = new[]
        {
            Hw.Line("Leitura", Hist.Of(w, x => D(x)?.AvgReadLatencyMs ?? 0), Hw.B("B.Violet"), fill: true),
            Hw.Line("Gravação", Hist.Of(w, x => D(x)?.AvgWriteLatencyMs ?? 0), Hw.B("B.Danger")),
        };
        LatencyLegend = new[]
        {
            new LegendItem("Leitura", $"{d.AvgReadLatencyMs:0.##} ms", Hw.B("B.Violet"), "Avg. Disk sec/Read. Referência: NVMe < 1 ms, SATA SSD poucos ms, HDD 5–20 ms."),
            new LegendItem("Gravação", $"{d.AvgWriteLatencyMs:0.##} ms", Hw.B("B.Danger"), "Avg. Disk sec/Write."),
        };
    }

    private string LabelFor(string instance)
    {
        var parts = instance.Split(' ', 2);
        string vols = parts.Length > 1 ? parts[1] : "";
        if (int.TryParse(parts[0], out var n) && _inventory.Current?.Disks.FirstOrDefault(x => x.Number == n) is { } disk)
            return $"Disco {n}{(vols.Length > 0 ? $" ({vols})" : "")} — {disk.Model}";
        return "Disco " + instance;
    }

    private Action? OnProcesses(ProcessSnapshot snap)
    {
        var top = snap.Processes.Where(p => p.Pid != 0)
            .OrderByDescending(p => (p.IoReadBytesPerSec ?? -1) + (p.IoWriteBytesPerSec ?? 0))
            .Take(15).Select(ProcRow.From).ToList();
        string status = snap.RatesAvailable
            ? $"Os 15 que mais leem e gravam agora · atualiza a cada 2 s (inclui também rede e dispositivos contabilizados pelo programa)"
            : "Taxas de E/S disponíveis a partir da próxima amostra…";
        return () =>
        {
            TopIo.Clear();
            foreach (var r in top) TopIo.Add(r);
            ProcStatus = status;
        };
    }

    [RelayCommand]
    private void OpenOptimize() => Hw.ShellOpen(ProcessRunner.System32("dfrgui.exe"), what: "Otimizar unidades");

    [RelayCommand]
    private async Task Refresh()
    {
        BuildDisks();
        await Task.WhenAll(LoadVolumesAsync(), LoadTrimAsync());
    }
}
