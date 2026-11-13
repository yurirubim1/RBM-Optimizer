using Rbm.Core;

namespace Rbm.Gaming.Frames;

/// <summary>
/// Métricas de frametime/FPS. Definições (ver <see cref="FrameMetrics.Definitions"/>):
/// FPS médio = quadros / tempo total (NÃO é a média dos FPS instantâneos).
/// 1% low / 0.1% low = 1000 / percentil 99 / 99,9 do frametime (interpolação linear R-7).
/// Variante "média dos piores": 1000 / média dos 1% / 0,1% maiores frametimes.
/// </summary>
public sealed record FrameMetrics(
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double MeanFrameTimeMs,
    double MedianFrameTimeMs,
    double MedianFps,
    double P95FrameTimeMs,
    double P99FrameTimeMs,
    double P999FrameTimeMs,
    double Low1PercentFps,
    double Low01PercentFps,
    double Low1PercentAvgFps,
    double Low01PercentAvgFps,
    double StdDevFrameTimeMs,
    double MinFrameTimeMs,
    double MaxFrameTimeMs,
    bool Low01PercentReliable)
{
    public const string Definitions =
        "FPS médio = nº de quadros ÷ tempo total (não é a média dos FPS instantâneos). " +
        "1% low = 1000 ÷ frametime do percentil 99; 0,1% low = 1000 ÷ frametime do percentil 99,9 (interpolação linear). " +
        "Variante 'média dos piores' = 1000 ÷ média dos 1% (ou 0,1%) maiores frametimes. " +
        "O 0,1% low só é confiável com ≥ 1000 quadros.";

    public static readonly FrameMetrics Empty = new(0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false);
}

/// <summary>Parâmetros de detecção de stutter.</summary>
/// <param name="WindowFrames">Janela da mediana móvel (quadros anteriores).</param>
/// <param name="Factor">Stutter se frametime &gt; Factor × mediana móvel...</param>
/// <param name="MinDeltaMs">... E frametime − mediana &gt; MinDeltaMs.</param>
/// <param name="MinHistoryFrames">Quadros mínimos de histórico antes de avaliar.</param>
/// <param name="ClusterGapMs">Stutters separados por menos que isto formam um mesmo agrupamento.</param>
public sealed record StutterOptions(int WindowFrames = 30, double Factor = 2.0, double MinDeltaMs = 8.0, int MinHistoryFrames = 10, double ClusterGapMs = 1000)
{
    public static readonly StutterOptions Default = new();
}

public sealed record StutterEvent(int FrameIndex, double TimeMs, double FrameTimeMs, double BaselineMs);

public sealed record StutterCluster(double StartMs, double EndMs, int Count, double WorstFrameTimeMs);

public sealed record StutterSummary(int Count, double PerMinute, IReadOnlyList<StutterEvent> Events, IReadOnlyList<StutterCluster> Clusters, StutterOptions Options)
{
    public string Definition =>
        $"Stutter = frametime > {Options.Factor:0.#}× a mediana dos {Options.WindowFrames} quadros anteriores E diferença > {Options.MinDeltaMs:0.#} ms.";
}

/// <summary>Faixa do histograma de frametime. <see cref="UpperMs"/> null = faixa de transbordo (≥ LowerMs).</summary>
public sealed record HistogramBucket(double LowerMs, double? UpperMs, int Count, double Percent);

/// <summary>Classe de latência de um modo de apresentação.</summary>
public enum PresentLatencyClass { Unknown, Lowest, Low, Higher }

public sealed record PresentModeShare(string Mode, int Count, double Percent, PresentLatencyClass Latency, string ImpactPt);

public sealed record PresentModeSummary(IReadOnlyList<PresentModeShare> Modes, string? Dominant, string ExplanationPt);

public sealed record SyncIntervalShare(int SyncInterval, int Count, double Percent);

public sealed record TearingSummary(double? AllowsTearingPercent, IReadOnlyList<SyncIntervalShare> SyncIntervals, double? DroppedPercent, string InterpretationPt);

public enum BoundKind { Indeterminate, GpuBound, CpuBound, Limited, Mixed }

/// <summary>Heurística de gargalo CPU × GPU (só com CPUBusy/GPUBusy do PresentMon 2.x).</summary>
public sealed record BoundAnalysis(
    BoundKind Kind,
    Confidence Confidence,
    string LabelPt,
    double? GpuBoundPercent,
    double? CpuBoundPercent,
    double? LimitedPercent,
    int FramesEvaluated,
    string ExplanationPt)
{
    public bool IsHeuristic => true;
}

/// <summary>Resultado completo da análise de uma captura.</summary>
public sealed record FrameAnalysis(
    PresentMonCsvLayout Layout,
    string Application,
    int ProcessId,
    string? Runtime,
    FrameMetrics Metrics,
    FrameMetrics? DisplayedMetrics,
    IReadOnlyList<HistogramBucket> Histogram,
    StutterSummary Stutters,
    PresentModeSummary PresentModes,
    TearingSummary Tearing,
    BoundAnalysis Bound,
    IReadOnlyList<double> PerSecondFps,
    IReadOnlyList<string> Warnings);

/// <summary>Opções da análise.</summary>
public sealed record FrameAnalysisOptions(StutterOptions? Stutter = null, double? HistogramBucketMs = null, double BoundThreshold = 0.85)
{
    public static readonly FrameAnalysisOptions Default = new();
}
