namespace Rbm.Core.Telemetry;

/// <summary>
/// Amostra de telemetria em tempo real. Todos os campos opcionais usam <see cref="Reading{T}"/>
/// para distinguir "zero" de "indisponível". Nenhum valor é estimado/fabricado.
/// </summary>
public sealed class TelemetrySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    // CPU
    public double CpuTotalPercent { get; set; }
    public double[] CpuPerCorePercent { get; set; } = Array.Empty<double>();
    /// <summary>Frequência efetiva média estimada (MHz) = base × "% Processor Performance" (mesmo método do Gerenciador de Tarefas).</summary>
    public Reading<double> CpuEffectiveClockMHz { get; set; }
    public Reading<double> CpuTemperatureC { get; set; }
    public Reading<double> CpuPackagePowerW { get; set; }
    /// <summary>"% Performance Limit" — &lt; 100 indica limitação (térmica/energia/plataforma).</summary>
    public Reading<double> CpuPerformanceLimitPercent { get; set; }
    public double ProcessorQueueLength { get; set; }
    public double ContextSwitchesPerSec { get; set; }
    public double InterruptsPerSec { get; set; }
    public double DpcTimePercent { get; set; }
    public double InterruptTimePercent { get; set; }

    // Memória
    public long MemTotalBytes { get; set; }
    public long MemAvailableBytes { get; set; }
    public long MemUsedBytes => MemTotalBytes - MemAvailableBytes;
    public double MemUsedPercent => MemTotalBytes > 0 ? MemUsedBytes * 100.0 / MemTotalBytes : 0;
    public long CommitBytes { get; set; }
    public long CommitLimitBytes { get; set; }
    public long CacheBytes { get; set; }
    public long StandbyBytes { get; set; }
    public long ModifiedBytes { get; set; }
    public long CompressedBytes { get; set; }
    public double HardFaultsPerSec { get; set; }
    public double PagesInputPerSec { get; set; }

    // GPU (por adaptador)
    public List<GpuTelemetry> Gpus { get; set; } = new();

    // Disco (por disco físico)
    public List<DiskTelemetry> Disks { get; set; } = new();

    // Rede (por interface)
    public List<NetTelemetry> Networks { get; set; } = new();

    public double NetRxBytesPerSec => Networks.Sum(n => n.RxBytesPerSec);
    public double NetTxBytesPerSec => Networks.Sum(n => n.TxBytesPerSec);
}

public sealed class GpuTelemetry
{
    public string Name { get; set; } = "";
    public long AdapterLuid { get; set; }
    public double Utilization3DPercent { get; set; }
    public double UtilizationMaxEnginePercent { get; set; }
    public double VideoDecodePercent { get; set; }
    public double VideoEncodePercent { get; set; }
    public double CopyPercent { get; set; }
    public double ComputePercent { get; set; }
    public long DedicatedUsedBytes { get; set; }
    public long DedicatedTotalBytes { get; set; }
    public long SharedUsedBytes { get; set; }
    public Reading<double> TemperatureC { get; set; }
    public Reading<double> HotspotC { get; set; }
    public Reading<double> CoreClockMHz { get; set; }
    public Reading<double> MemoryClockMHz { get; set; }
    public Reading<double> PowerW { get; set; }
    public Reading<double> FanRpm { get; set; }
}

public sealed class DiskTelemetry
{
    public string Instance { get; set; } = "";
    public double ActivePercent { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
    public double AvgReadLatencyMs { get; set; }
    public double AvgWriteLatencyMs { get; set; }
    public double QueueLength { get; set; }
    public Reading<double> TemperatureC { get; set; }
}

public sealed class NetTelemetry
{
    public string Name { get; set; } = "";
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public double PacketsReceivedErrors { get; set; }
    public double PacketsOutboundDiscarded { get; set; }
    public double PacketsReceivedDiscarded { get; set; }
    public double CurrentBandwidthBps { get; set; }
}

/// <summary>Amostra de processo para o gerenciador de processos e diagnóstico.</summary>
public sealed record ProcessSample(
    int Pid, string Name, double CpuPercent, long WorkingSetBytes, long PrivateBytes,
    double IoReadBytesPerSec, double IoWriteBytesPerSec, int Threads, int Handles, int ParentPid,
    string? Path, DateTime? StartTime, int BasePriority);
