using Rbm.Gaming.Frames;

namespace Rbm.Gaming.Sessions;

/// <summary>Condições da sessão informadas pelo chamador (o módulo não as inventa).</summary>
/// <param name="PowerPlanName">Plano de energia ativo.</param>
/// <param name="PowerPlanGuid">GUID do plano de energia ativo.</param>
/// <param name="AppliedTweakIds">IDs dos tweaks aplicados no momento da captura.</param>
/// <param name="Resolution">Resolução do jogo (ex.: "1920x1080"), informada pelo usuário.</param>
/// <param name="SettingsLabel">Rótulo das configurações gráficas (ex.: "Alto, FSR Qualidade"), informado pelo usuário.</param>
/// <param name="Scenario">Cena/trecho testado (ex.: "benchmark interno", "mapa X, rota Y").</param>
public sealed record SessionConditions(
    string? PowerPlanName = null,
    Guid? PowerPlanGuid = null,
    IReadOnlyList<string>? AppliedTweakIds = null,
    string? Resolution = null,
    string? SettingsLabel = null,
    string? Scenario = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> TweakIds => AppliedTweakIds ?? [];
}

/// <summary>Sessão de captura de quadros completa (inclui histograma e eventos de stutter).</summary>
public sealed record FrameSession(
    Guid Id,
    string GameId,
    string GameName,
    string ProcessName,
    DateTimeOffset Start,
    TimeSpan Duration,
    FrameMetrics Metrics,
    FrameMetrics? DisplayedMetrics,
    IReadOnlyList<HistogramBucket> Histogram,
    StutterSummary Stutters,
    PresentModeSummary PresentModes,
    TearingSummary Tearing,
    BoundAnalysis Bound,
    SessionConditions Conditions,
    IReadOnlyList<double> PerSecondFps,
    PresentMonCsvLayout CsvLayout,
    string? RawCsvPath,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Monta a sessão a partir de uma análise.</summary>
    public static FrameSession FromAnalysis(string gameId, string gameName, DateTimeOffset start, FrameAnalysis a, SessionConditions conditions, string? rawCsvPath) =>
        new(Guid.NewGuid(), gameId, gameName, a.Application, start, TimeSpan.FromSeconds(a.Metrics.DurationSeconds), a.Metrics, a.DisplayedMetrics,
            a.Histogram, a.Stutters, a.PresentModes, a.Tearing, a.Bound, conditions, a.PerSecondFps, a.Layout, rawCsvPath, a.Warnings);

    /// <summary>Resumo compacto para o histórico do perfil (games.json).</summary>
    public FrameSessionSummary ToSummary() => new(
        Id, GameId, ProcessName, Start, Duration,
        Metrics.FrameCount, Metrics.AverageFps, Metrics.MedianFps, Metrics.Low1PercentFps, Metrics.Low01PercentFps,
        Metrics.Low1PercentAvgFps, Metrics.P99FrameTimeMs, Stutters.Count, Stutters.PerMinute,
        PresentModes.Dominant, Bound.Kind, Bound.LabelPt, Conditions, PerSecondFps, RawCsvPath);
}

/// <summary>Resumo de sessão persistido no perfil do jogo (contém FPS por segundo para comparações).</summary>
public sealed record FrameSessionSummary(
    Guid Id,
    string GameId,
    string ProcessName,
    DateTimeOffset Start,
    TimeSpan Duration,
    int FrameCount,
    double AverageFps,
    double MedianFps,
    double Low1PercentFps,
    double Low01PercentFps,
    double Low1PercentAvgFps,
    double P99FrameTimeMs,
    int StutterCount,
    double StuttersPerMinute,
    string? DominantPresentMode,
    BoundKind Bound,
    string BoundLabelPt,
    SessionConditions Conditions,
    IReadOnlyList<double> PerSecondFps,
    string? RawCsvPath);
