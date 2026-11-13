using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Gaming.Correlation;
using Rbm.Gaming.Frames;
using Rbm.Gaming.Sessions;

namespace Rbm.Gaming;

/// <summary>
/// Atalhos de alto nível que ligam as peças: CSV real do PresentMon → análise → sessão (+ correlação com telemetria).
/// Nenhuma métrica é inventada: sem CSV válido, o resultado é falha com mensagem pt-BR.
/// </summary>
public static class GamingPerformanceCenter
{
    /// <summary>Lê e analisa um CSV do PresentMon (qualquer layout 1.x/2.x).</summary>
    public static async Task<OperationResult<FrameAnalysis>> AnalyzeCsvAsync(string csvPath, string? processName = null, FrameAnalysisOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            var parsed = await Task.Run(() => PresentMonCsvParser.ParseFile(csvPath, new PresentMonParseOptions(processName)), ct).ConfigureAwait(false);
            if (parsed.Frames.Count < 2) return OperationResult<FrameAnalysis>.Fail("O CSV tem quadros insuficientes para análise.", "too_few_frames");
            return OperationResult<FrameAnalysis>.Ok(FrameAnalyzer.Analyze(parsed, options));
        }
        catch (FormatException ex) { return OperationResult<FrameAnalysis>.Fail($"CSV inválido: {ex.Message}", "bad_csv"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OperationResult<FrameAnalysis>.Fail($"Não foi possível ler o CSV: {ex.Message}", "io_error"); }
    }

    /// <summary>
    /// Monta a sessão e (opcionalmente) a correlação de stutters com a telemetria coletada no mesmo período.
    /// </summary>
    /// <param name="captureStart">Momento em que o PresentMon começou a gravar (FrameCaptureResult.StartedAt).</param>
    public static (FrameSession Session, StutterCorrelationReport? Correlation) BuildSession(
        string gameId, string gameName, DateTimeOffset captureStart, FrameAnalysis analysis, SessionConditions conditions,
        string? rawCsvPath, IReadOnlyList<TelemetrySnapshot>? telemetry = null, CorrelationOptions? correlationOptions = null)
    {
        var session = FrameSession.FromAnalysis(gameId, gameName, captureStart, analysis, conditions, rawCsvPath);
        StutterCorrelationReport? corr = telemetry is { Count: > 0 }
            ? StutterCorrelator.Correlate(captureStart, analysis.Metrics.DurationSeconds * 1000 + 1000, analysis.Stutters, telemetry, correlationOptions)
            : null;
        return (session, corr);
    }
}
