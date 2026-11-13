using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;

namespace Rbm.Diagnostics;

/// <summary>Um fator mensurável que compõe a nota de uma categoria.</summary>
public sealed record IndexFactor(string Name, string Measured, double Score, double Weight, bool Available, string Source, string Rule);

/// <summary>Nota (0–100) de uma categoria com fatores, pesos e confiança explícitos.</summary>
public sealed record IndexCategory(string Name, double? Score, Confidence Confidence, IReadOnlyList<IndexFactor> Factors, string Methodology);

public sealed record HealthIndex(IReadOnlyList<IndexCategory> Categories, DateTimeOffset At)
{
    public static readonly HealthIndex Empty = new(Array.Empty<IndexCategory>(), DateTimeOffset.MinValue);
}

/// <summary>
/// Índice de CONDIÇÃO do sistema (não é benchmark de desempenho bruto).
/// Cada fator converte uma métrica medida em 0–100 por limiares documentados; a nota da categoria é a
/// média ponderada dos fatores DISPONÍVEIS e a confiança é a fração do peso com dados reais.
/// Categorias sem nenhum dado ficam sem nota ("indisponível") — nada é estimado.
/// </summary>
public static class HealthIndexCalculator
{
    /// <summary>Interpolação linear: valor ≤ good → 100; ≥ bad → 0.</summary>
    public static double Linear(double value, double good, double bad)
    {
        if (double.IsNaN(value)) return double.NaN;
        if (good < bad) return value <= good ? 100 : value >= bad ? 0 : 100 * (bad - value) / (bad - good);
        return value >= good ? 100 : value <= bad ? 0 : 100 * (value - bad) / (good - bad);
    }

    public static HealthIndex Compute(DiagnosticContext ctx, IReadOnlyList<Finding> findings)
    {
        var p = ctx.Profile; var s = ctx.Samples;
        bool hasSamples = s.Count >= 5;
        double Avg(Func<TelemetrySnapshot, double> f) => hasSamples ? s.Average(f) : double.NaN;

        var cats = new List<IndexCategory>();

        // ---- Saúde do sistema ----
        var sys = p.Volumes.FirstOrDefault(v => v.IsSystem);
        cats.Add(Cat("Saúde do sistema",
            F("Espaço livre na unidade do sistema", sys is null ? "—" : $"{sys.FreePercent:0}%", sys is null ? double.NaN : Linear(sys.FreePercent, 25, 5), 3, "Volume do sistema", "≥25% → 100; ≤5% → 0"),
            F("Saúde dos discos (Storage Management)", string.Join(", ", p.Disks.Select(d => d.HealthStatus)), p.Disks.Count == 0 ? double.NaN : p.Disks.All(d => d.HealthStatus == "Saudável") ? 100 : p.Disks.Any(d => d.HealthStatus == "Não saudável") ? 0 : 50, 3, "MSFT_PhysicalDisk", "todos saudáveis → 100; atenção → 50; falha → 0"),
            F("Dispositivos com erro", FindingCount(findings, "devices.problems"), findings.Any(f => f.RuleId == "devices.problems") ? 40 : 100, 2, "Win32_PnPEntity", "nenhum → 100; algum → 40"),
            F("Driver de vídeo atualizado (< 1 ano)", p.PrimaryGpu?.DriverDate?.ToString("d") ?? "—", p.PrimaryGpu?.DriverDate is { } dd ? Linear((DateTime.Now - dd).TotalDays, 365, 900) : double.NaN, 1, "Win32_VideoController", "≤365 dias → 100; ≥900 → 0")));

        // ---- Estabilidade ----
        var st = findings.FirstOrDefault(f => f.RuleId == "stability.events");
        cats.Add(Cat("Estabilidade",
            F("Desligamentos/telas azuis/WHEA (30 dias)", st is null ? "nenhum" : string.Join("; ", st.Evidence.Take(3)), st is null ? 100 : st.Severity >= Severity.High ? 20 : 60, 1, "Log do Sistema", "nenhum → 100; só Kernel-Power → 60; BugCheck/WHEA → 20")));

        // ---- CPU ----
        var lim = hasSamples && s.Any(x => x.CpuPerformanceLimitPercent.Available) ? s.Where(x => x.CpuPerformanceLimitPercent.Available).Average(x => x.CpuPerformanceLimitPercent.Value) : double.NaN;
        var ctemp = s.Where(x => x.CpuTemperatureC.Available).Select(x => x.CpuTemperatureC.Value).DefaultIfEmpty(double.NaN).Max();
        cats.Add(Cat("Desempenho de CPU",
            F("Folga de uso (média)", hasSamples ? $"{Avg(x => x.CpuTotalPercent):0}% em uso" : "—", Linear(Avg(x => x.CpuTotalPercent), 50, 95), 2, "PDH % Processor Utility", "≤50% → 100; ≥95% → 0"),
            F("Ausência de limitação (% Performance Limit)", double.IsNaN(lim) ? "—" : $"{lim:0}%", Linear(lim, 99, 70), 3, "PDH % Performance Limit", "≥99% → 100; ≤70% → 0"),
            F("Temperatura máxima", double.IsNaN(ctemp) ? "indisponível" : $"{ctemp:0} °C", Linear(ctemp, 75, 95), 2, "LibreHardwareMonitor", "≤75 °C → 100; ≥95 °C → 0"),
            F("Plano de energia", p.Power.ActiveSchemeName, p.Power.ActiveSchemeGuid == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a") && p.FormFactor != FormFactor.Laptop ? 30 : 100, 1, "PowrProf", "Economia em desktop → 30; demais → 100")));

        // ---- GPU ----
        var gt = s.SelectMany(x => x.Gpus).Where(g => g.TemperatureC.Available).Select(g => g.TemperatureC.Value).DefaultIfEmpty(double.NaN).Max();
        var vram = hasSamples ? s.SelectMany(x => x.Gpus).Where(g => g.DedicatedTotalBytes > 0).Select(g => g.DedicatedUsedBytes * 100.0 / g.DedicatedTotalBytes).DefaultIfEmpty(double.NaN).Max() : double.NaN;
        cats.Add(Cat("Desempenho de GPU",
            F("Temperatura máxima do núcleo", double.IsNaN(gt) ? "indisponível" : $"{gt:0} °C", Linear(gt, 75, 92), 2, "LibreHardwareMonitor (ADL/NVAPI)", "≤75 °C → 100; ≥92 °C → 0"),
            F("Folga de VRAM (pico)", double.IsNaN(vram) ? "—" : $"{vram:0}% usada", Linear(vram, 80, 98), 2, "PDH GPU Adapter Memory", "≤80% → 100; ≥98% → 0"),
            F("Driver com problema/antigo", FindingCount(findings, "gpu.driver-age"), findings.Any(f => f.RuleId == "gpu.driver-age") ? 60 : 100, 1, "Win32_VideoController", "≤1 ano → 100; mais antigo → 60")));

        // ---- Responsividade ----
        cats.Add(Cat("Responsividade",
            F("Memória disponível (média)", hasSamples ? $"{100 - Avg(x => x.MemUsedPercent):0}% livre" : "—", Linear(Avg(x => x.MemUsedPercent), 70, 95), 3, "GlobalMemoryStatusEx", "uso ≤70% → 100; ≥95% → 0"),
            F("Leituras de página (hard faults/s)", hasSamples ? $"{Avg(x => x.HardFaultsPerSec):0}/s" : "—", Linear(Avg(x => x.HardFaultsPerSec), 50, 1000), 2, "PDH Page Reads/sec", "≤50/s → 100; ≥1000/s → 0"),
            F("Fila de processador", hasSamples ? $"{Avg(x => x.ProcessorQueueLength):0.0}" : "—", Linear(Avg(x => x.ProcessorQueueLength), Math.Max(2, p.Cpu.LogicalProcessors / 2.0), p.Cpu.LogicalProcessors * 2.0), 1, "PDH Processor Queue Length", "≤ núcleos/2 → 100; ≥ 2×núcleos → 0")));

        // ---- Armazenamento ----
        var sysDisk = p.Disks.FirstOrDefault(d => d.IsSystemDisk);
        var lat = hasSamples ? s.SelectMany(x => x.Disks).Select(d => Math.Max(d.AvgReadLatencyMs, d.AvgWriteLatencyMs)).DefaultIfEmpty(double.NaN).Average() : double.NaN;
        cats.Add(Cat("Condição do armazenamento",
            F("Tipo do disco do sistema", sysDisk is null ? "—" : $"{sysDisk.MediaType} {sysDisk.BusType}", sysDisk is null ? double.NaN : sysDisk.MediaType == StorageMediaType.Hdd ? 30 : 100, 2, "MSFT_PhysicalDisk", "SSD → 100; HDD → 30"),
            F("Latência média de E/S", double.IsNaN(lat) ? "—" : $"{lat:0.0} ms", Linear(lat, 5, 50), 2, "PDH Avg. Disk sec/Transfer", "≤5 ms → 100; ≥50 ms → 0"),
            F("Espaço livre (pior volume)", p.Volumes.Count == 0 ? "—" : $"{p.Volumes.Min(v => v.FreePercent):0}%", p.Volumes.Count == 0 ? double.NaN : Linear(p.Volumes.Min(v => v.FreePercent), 20, 3), 1, "Volumes", "≥20% → 100; ≤3% → 0")));

        // ---- Latência ----
        cats.Add(Cat("Latência (indicativa)",
            F("% tempo em DPC (média)", hasSamples ? $"{Avg(x => x.DpcTimePercent):0.00}%" : "—", Linear(Avg(x => x.DpcTimePercent), 0.5, 5), 2, "PDH % DPC Time", "≤0,5% → 100; ≥5% → 0"),
            F("% tempo em interrupções (média)", hasSamples ? $"{Avg(x => x.InterruptTimePercent):0.00}%" : "—", Linear(Avg(x => x.InterruptTimePercent), 0.5, 5), 1, "PDH % Interrupt Time", "≤0,5% → 100; ≥5% → 0")));

        return new HealthIndex(cats, DateTimeOffset.Now);
    }

    private static string FindingCount(IReadOnlyList<Finding> f, string rule) => f.Any(x => x.RuleId == rule) ? "detectado" : "nenhum";

    private static IndexFactor F(string name, string measured, double score, double weight, string source, string rule) =>
        new(name, measured, double.IsNaN(score) ? double.NaN : Math.Round(score, 1), weight, !double.IsNaN(score), source, rule);

    private static IndexCategory Cat(string name, params IndexFactor[] factors)
    {
        var avail = factors.Where(f => f.Available).ToList();
        double totalW = factors.Sum(f => f.Weight), availW = avail.Sum(f => f.Weight);
        double? score = availW > 0 ? Math.Round(avail.Sum(f => f.Score * f.Weight) / availW, 0) : null;
        double coverage = totalW > 0 ? availW / totalW : 0;
        var conf = coverage >= 0.8 ? Confidence.High : coverage >= 0.5 ? Confidence.Medium : Confidence.Low;
        return new IndexCategory(name, score, conf, factors, "Média ponderada dos fatores disponíveis; confiança = fração do peso com dados reais.");
    }
}
