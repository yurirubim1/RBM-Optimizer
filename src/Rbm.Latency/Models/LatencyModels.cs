using Rbm.Core;
using Rbm.Core.Diagnostics;

namespace Rbm.Latency;

// ============================================================================
// Resultados do analisador DPC/ISR (captura ETW de kernel, requer elevação).
// Todos os tipos são imutáveis e serializáveis com System.Text.Json
// (atravessam o pipe do RBM.Agent usando AgentJson.Options).
// Unidade de tempo: microssegundos (µs), salvo indicação em contrário.
// ============================================================================

/// <summary>Tipo de execução de kernel medida.</summary>
public enum LatencyEventKind
{
    /// <summary>Deferred Procedure Call (inclui DPCs de timer e DPCs encadeados/threaded).</summary>
    Dpc = 0,
    /// <summary>Interrupt Service Routine.</summary>
    Isr = 1,
}

/// <summary>Veredito agregado de uma captura DPC/ISR.</summary>
public enum LatencyVerdict
{
    /// <summary>Sem dados suficientes (sessão sem eventos, captura abortada etc.).</summary>
    Inconclusive = 0,
    Good = 1,
    Attention = 2,
    Problematic = 3,
}

/// <summary>Faixa do histograma de duração.</summary>
public sealed record HistogramBucket(string Label, double LowerUs, double? UpperUs, long Count);

/// <summary>Estatísticas de duração de um conjunto de execuções (DPC ou ISR).</summary>
/// <param name="P50Us">Mediana estimada por histograma fino (resolução 1 µs abaixo de 1 ms; 10 µs até 10 ms; 100 µs até 100 ms).</param>
/// <param name="P99Us">Percentil 99 estimado com a mesma resolução.</param>
public sealed record LatencyStats(
    long Count,
    double TotalUs,
    double MinUs,
    double MaxUs,
    double MeanUs,
    double P50Us,
    double P99Us,
    IReadOnlyList<HistogramBucket> Histogram)
{
    public static LatencyStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, LatencyHistogram.EmptyBuckets());

    /// <summary>Quantidade de execuções com duração &gt;= <paramref name="thresholdUs"/> (resolução das faixas do histograma).</summary>
    public long CountAtOrAbove(double thresholdUs) => Histogram.Where(b => b.LowerUs >= thresholdUs).Sum(b => b.Count);
}

/// <summary>Estatísticas por módulo de kernel (driver), separadas em DPC e ISR.</summary>
/// <param name="Module">Nome do arquivo do driver (ex.: ndis.sys). "(desconhecido)" quando o endereço não pôde ser mapeado.</param>
/// <param name="ImagePath">Caminho da imagem conforme informado pelo kernel (pode estar em formato \SystemRoot\...).</param>
/// <param name="Category">Categoria provável (rede, GPU, armazenamento, áudio...) por nome conhecido; "" se desconhecida.</param>
/// <param name="PercentOfCaptureTime">Tempo total (DPC+ISR) do módulo / (duração da captura × nº de CPUs) × 100.</param>
public sealed record ModuleLatencyStats(
    string Module,
    string? ImagePath,
    string Category,
    LatencyStats Dpc,
    LatencyStats Isr,
    double HighestUs,
    double PercentOfCaptureTime);

/// <summary>Distribuição por processador lógico.</summary>
/// <param name="PercentTime">Percentual do tempo de parede deste CPU gasto em DPC+ISR.</param>
public sealed record CpuLatencyStats(
    int Cpu,
    long DpcCount,
    double DpcTotalUs,
    double DpcMaxUs,
    long IsrCount,
    double IsrTotalUs,
    double IsrMaxUs,
    double PercentTime);

/// <summary>Ponto da linha do tempo (1 por segundo) para gráficos.</summary>
public sealed record LatencyTimelinePoint(
    int Second,
    double MaxDpcUs,
    double MaxIsrUs,
    int DpcCount,
    int IsrCount,
    double DpcTotalUs,
    double IsrTotalUs);

/// <summary>Uma execução individual (usado no "top N" das mais longas).</summary>
/// <param name="OffsetMs">Milissegundos desde o início da captura.</param>
public sealed record LatencyExecution(
    LatencyEventKind Kind,
    string Module,
    ulong Routine,
    double DurationUs,
    double OffsetMs,
    DateTimeOffset Timestamp,
    int Cpu);

/// <summary>Page faults "hard" (leitura de disco) por processo durante a captura.</summary>
public sealed record HardFaultProcessStats(int ProcessId, string ProcessName, long Count, double TotalMs);

/// <summary>Limiares usados no veredito (estilo LatencyMon). Documentados no README.</summary>
/// <param name="ElevatedUs">Execução acima disto merece atenção (padrão 500 µs).</param>
/// <param name="HighUs">Pode causar falhas de áudio em buffers pequenos (padrão 1000 µs = 1 ms).</param>
/// <param name="SevereUs">Problemático (padrão 2000 µs = 2 ms).</param>
/// <param name="SustainedSeconds">Nº mínimo de segundos distintos com picos para considerar "sustentado" (padrão 3).</param>
/// <param name="ModuleSpikeCount">Nº de execuções &gt;= ElevatedUs de um mesmo módulo que gera achado mesmo sem passar de HighUs (padrão 10).</param>
public sealed record LatencyThresholds(
    double ElevatedUs = 500,
    double HighUs = 1000,
    double SevereUs = 2000,
    int SustainedSeconds = 3,
    int ModuleSpikeCount = 10)
{
    public static LatencyThresholds Default { get; } = new();
}

/// <summary>Relatório completo de uma captura DPC/ISR.</summary>
/// <param name="UnresolvedPercent">Percentual do tempo DPC+ISR cujo endereço de rotina não foi mapeado a um módulo.</param>
public sealed record LatencyReport(
    DateTimeOffset StartedAt,
    int RequestedSeconds,
    double DurationSeconds,
    string SessionName,
    int LogicalProcessors,
    LatencyStats DpcOverall,
    LatencyStats IsrOverall,
    IReadOnlyList<ModuleLatencyStats> Modules,
    IReadOnlyList<CpuLatencyStats> PerCpu,
    IReadOnlyList<LatencyTimelinePoint> Timeline,
    IReadOnlyList<LatencyExecution> TopExecutions,
    IReadOnlyList<HardFaultProcessStats> HardFaults,
    LatencyVerdict Verdict,
    string VerdictText,
    IReadOnlyList<Finding> Findings,
    long EventsLost,
    long InvalidSamples,
    double UnresolvedPercent,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Limitations,
    LatencyThresholds Thresholds);

/// <summary>Opções da captura DPC/ISR.</summary>
/// <param name="Seconds">Duração (validada 5..300).</param>
/// <param name="TopExecutions">Quantas execuções mais longas manter (1..500).</param>
/// <param name="CollectHardFaults">Coleta page faults "hard" por processo (custo baixo).</param>
/// <param name="BufferSizeMB">Buffer da sessão ETW em tempo real (16..512).</param>
public sealed record DpcIsrCaptureOptions(
    int Seconds = 60,
    int TopExecutions = 50,
    bool CollectHardFaults = true,
    int BufferSizeMB = 64,
    LatencyThresholds? Thresholds = null);

/// <summary>Progresso da captura (para barra de progresso na UI/agente).</summary>
public sealed record LatencyCaptureProgress(double ElapsedSeconds, int TotalSeconds, long DpcEvents, long IsrEvents);

// ============================================================================
// Indicadores sem elevação (contadores de desempenho).
// ============================================================================

/// <summary>Resumo de um contador amostrado (sem NaN: valores ausentes viram 0 com Count = 0, para serializar em JSON).</summary>
public sealed record CounterSeries(
    string CounterPath,
    int Count,
    double Mean,
    double Median,
    double Min,
    double Max,
    double P95,
    double StdDev,
    IReadOnlyList<double> Samples)
{
    public static CounterSeries From(string path, IReadOnlyList<double> samples)
    {
        var s = Stats.Summarize(samples);
        static double Z(double v) => double.IsFinite(v) ? v : 0;
        return new CounterSeries(path, s.Count, Z(s.Mean), Z(s.Median), Z(s.Min), Z(s.Max), Z(s.P95), Z(s.StdDev), samples.ToArray());
    }
}

/// <summary>Valores por CPU de % DPC/% interrupção (média e pico).</summary>
public sealed record CpuCounterStats(string Instance, double MeanDpcPercent, double MaxDpcPercent, double MeanInterruptPercent, double MaxInterruptPercent);

/// <summary>Resultado da amostragem de contadores (indicativo, sem identificar drivers).</summary>
public sealed record InterruptCounterSummary(
    DateTimeOffset StartedAt,
    int Seconds,
    double IntervalSeconds,
    CounterSeries DpcTimePercent,
    CounterSeries InterruptTimePercent,
    CounterSeries DpcsQueuedPerSec,
    CounterSeries InterruptsPerSec,
    IReadOnlyList<CpuCounterStats> PerCpu,
    string Interpretation,
    IReadOnlyList<string> Limitations);

// ============================================================================
// Resolução de timer.
// ============================================================================

/// <summary>Resolução do timer do sistema (NtQueryTimerResolution). Valores em ms.</summary>
/// <param name="CoarsestMs">Resolução mais grossa suportada (normalmente 15,625 ms) — chamada "MinimumResolution" pela API.</param>
/// <param name="FinestMs">Resolução mais fina suportada (normalmente 0,5 ms) — "MaximumResolution" na API.</param>
/// <param name="CurrentMs">Resolução global atual da interrupção de timer (a mais fina pedida por algum processo).</param>
/// <param name="GlobalTimerResolutionRequests">Valor do registro Session Manager\kernel\GlobalTimerResolutionRequests (null = ausente).</param>
public sealed record TimerResolutionInfo(
    double CoarsestMs,
    double FinestMs,
    double CurrentMs,
    uint Coarsest100ns,
    uint Finest100ns,
    uint Current100ns,
    int? GlobalTimerResolutionRequests,
    string Explanation,
    IReadOnlyList<string> References);

/// <summary>Pedido de resolução de timer pendente (do relatório powercfg /energy).</summary>
public sealed record TimerResolutionRequest(int? ProcessId, string? ProcessPath, uint? RequestedPeriod100ns, double? RequestedPeriodMs);

/// <summary>Resultado de powercfg /energy filtrado para resolução de timer.</summary>
public sealed record TimerRequestsReport(
    DateTimeOffset StartedAt,
    int Seconds,
    double? PlatformCurrentResolutionMs,
    IReadOnlyList<TimerResolutionRequest> Requests,
    IReadOnlyList<string> Warnings);

// ============================================================================
// Dispositivos de entrada / USB.
// ============================================================================

public enum InputDeviceCategory { Mouse, Keyboard, GameController, OtherHid }

public enum SelectiveSuspendState
{
    /// <summary>Nenhum valor relevante no registro (padrão do driver/classe).</summary>
    NotConfigured = 0,
    /// <summary>Suspensão seletiva desativada para o dispositivo.</summary>
    Disabled = 1,
    /// <summary>Suspensão seletiva permitida para o dispositivo.</summary>
    Enabled = 2,
    /// <summary>Valores contraditórios ou formato inesperado.</summary>
    Mixed = 3,
}

/// <summary>Valores de "Device Parameters" ligados a energia/suspensão (somente leitura).</summary>
public sealed record UsbPowerSettings(
    int? SelectiveSuspendEnabled,
    int? EnhancedPowerManagementEnabled,
    int? AllowIdleIrpInD3,
    int? DeviceSelectiveSuspended,
    int? SelectiveSuspendOn,
    SelectiveSuspendState State,
    string Interpretation);

/// <summary>Nó da cadeia de pais (dispositivo → hub → controlador).</summary>
public sealed record DeviceChainNode(string InstanceId, string Name, string? Service, string? Class, string Role, UsbPowerSettings? Power);

/// <summary>Dispositivo de entrada (HID) com cadeia de pais e configurações de energia.</summary>
/// <param name="HostControllerInstanceId">Instance ID do controlador host USB (PCI) — cruzar com <see cref="PciInterruptInfo"/>.</param>
public sealed record InputDeviceInfo(
    string InstanceId,
    string Name,
    InputDeviceCategory Category,
    string PnpClass,
    string? Service,
    string Bus,
    bool Present,
    UsbPowerSettings? Power,
    IReadOnlyList<DeviceChainNode> ParentChain,
    string? HostControllerInstanceId);

/// <summary>Configuração global "Suspensão seletiva de USB" do plano de energia ativo.</summary>
public sealed record UsbSelectiveSuspendGlobal(Guid? ActiveScheme, int? AcValue, int? DcValue, string Interpretation);

public sealed record InputDevicesReport(
    IReadOnlyList<InputDeviceInfo> Devices,
    UsbSelectiveSuspendGlobal GlobalSelectiveSuspend,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes);

// ============================================================================
// Estimador de taxa de polling.
// ============================================================================

/// <summary>Estimativa da taxa de relatório de um dispositivo (a partir de timestamps de WM_INPUT).</summary>
/// <param name="StabilityPercent">% de intervalos dentro de ±20% da mediana.</param>
public sealed record PollingRateEstimate(
    long DeviceHandle,
    int IntervalCount,
    int PausesIgnored,
    double MedianIntervalMs,
    double MeanIntervalMs,
    double P1IntervalMs,
    double P99IntervalMs,
    double StdDevIntervalMs,
    double EstimatedRateHz,
    int NearestStandardRateHz,
    double StabilityPercent,
    Confidence Confidence,
    IReadOnlyList<string> Notes);

// ============================================================================
// Modo de interrupção (MSI / linha) de dispositivos PCI.
// ============================================================================

public enum InterruptMode { Unknown = 0, LineBased = 1, Msi = 2 }

public enum PciDeviceCategory { Other, Gpu, Network, Storage, UsbController, Audio, Bridge }

/// <summary>Configuração de interrupção de um dispositivo PCI (somente leitura).</summary>
/// <param name="MsiSupported">MSISupported do registro (null = valor ausente).</param>
/// <param name="Irqs">IRQs alocados segundo WMI (MSI aparecem como números negativos).</param>
public sealed record PciInterruptInfo(
    string InstanceId,
    string Name,
    PciDeviceCategory Category,
    string? Class,
    string? Service,
    bool Present,
    int? MsiSupported,
    int? MessageNumberLimit,
    int? DevicePolicy,
    string? DevicePolicyName,
    int? DevicePriority,
    string? DevicePriorityName,
    ulong? AssignmentSetOverride,
    IReadOnlyList<long> Irqs,
    InterruptMode EffectiveMode,
    string Interpretation);

public sealed record InterruptModeReport(IReadOnlyList<PciInterruptInfo> Devices, IReadOnlyList<string> Warnings, IReadOnlyList<string> Notes);
