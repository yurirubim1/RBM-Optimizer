namespace Rbm.Gaming.Frames;

/// <summary>Layout de colunas do CSV do PresentMon (detectado pelo cabeçalho).</summary>
public enum PresentMonCsvLayout
{
    Unknown = 0,
    /// <summary>PresentMon 1.x ou 2.x com --v1_metrics: msBetweenPresents, msBetweenDisplayChange, msUntilDisplayed, Dropped, TimeInSeconds.</summary>
    V1,
    /// <summary>PresentMon 2.0–2.2 (padrão) ou 2.x com --v2_metrics: CPUStartTime, FrameTime, CPUBusy, CPUWait, GPUBusy, GPUWait, DisplayLatency, DisplayedTime.</summary>
    V2Metrics,
    /// <summary>PresentMon 2.3+ (padrão): TimeInMs/TimeInSeconds, MsBetweenPresents, MsBetweenDisplayChange, MsUntilDisplayed, MsCPUBusy, MsGPUBusy...</summary>
    V2Default,
}

/// <summary>
/// Um quadro (Present) do processo-alvo. Valores ausentes são <see cref="double.NaN"/> (nunca estimados).
/// </summary>
/// <param name="TimeMs">Instante do quadro em ms desde o início da gravação do PresentMon.</param>
/// <param name="FrameTimeMs">Tempo entre quadros (msBetweenPresents / FrameTime / MsBetweenAppStart).</param>
/// <param name="DisplayChangeMs">Tempo entre mudanças de imagem na tela (msBetweenDisplayChange / DisplayedTime).</param>
/// <param name="UntilDisplayedMs">Do Present() até aparecer na tela.</param>
/// <param name="CpuBusyMs">CPUBusy (v2).</param>
/// <param name="CpuWaitMs">CPUWait (v2).</param>
/// <param name="GpuBusyMs">GPUBusy (v2).</param>
/// <param name="GpuWaitMs">GPUWait (v2).</param>
/// <param name="DisplayLatencyMs">DisplayLatency (v2) — início da CPU até a tela.</param>
/// <param name="ClickToPhotonMs">ClickToPhotonLatency (v2), quando houve clique.</param>
/// <param name="Displayed">true = exibido; false = descartado (dropped); null = desconhecido (sem rastreamento de display).</param>
/// <param name="PresentMode">Texto do PresentMon (ex.: "Hardware: Independent Flip").</param>
/// <param name="AllowsTearing">AllowsTearing (null se ausente).</param>
/// <param name="SyncInterval">SyncInterval pedido pela aplicação (null se ausente).</param>
public readonly record struct FrameRecord(
    double TimeMs,
    double FrameTimeMs,
    double DisplayChangeMs,
    double UntilDisplayedMs,
    double CpuBusyMs,
    double CpuWaitMs,
    double GpuBusyMs,
    double GpuWaitMs,
    double DisplayLatencyMs,
    double ClickToPhotonMs,
    bool? Displayed,
    string? PresentMode,
    bool? AllowsTearing,
    int? SyncInterval);

/// <summary>Outra combinação processo/swap chain presente no CSV (não analisada).</summary>
public sealed record CsvTargetInfo(string Application, int ProcessId, string SwapChain, int Frames);

/// <summary>Resultado do parse de um CSV do PresentMon, já filtrado para um processo + swap chain principal.</summary>
public sealed record ParsedFrameCapture(
    PresentMonCsvLayout Layout,
    string Application,
    int ProcessId,
    string SwapChain,
    string? Runtime,
    IReadOnlyList<FrameRecord> Frames,
    IReadOnlyList<CsvTargetInfo> OtherTargets,
    int SkippedRows,
    string FrameTimeColumn,
    string TimeSource,
    IReadOnlyList<string> Warnings)
{
    public bool HasCpuGpuBusy => Frames.Any(f => double.IsFinite(f.GpuBusyMs) && f.GpuBusyMs > 0) && Frames.Any(f => double.IsFinite(f.CpuBusyMs) && f.CpuBusyMs > 0);
    public bool HasDisplayTracking => Frames.Any(f => f.Displayed is not null);
}
