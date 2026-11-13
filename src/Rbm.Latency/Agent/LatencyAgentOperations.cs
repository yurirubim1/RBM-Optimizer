using Rbm.Core.Agent;

namespace Rbm.Latency;

/// <summary>
/// Operações PROPOSTAS para o RBM.Agent além de <see cref="AgentOps.LatencyCapture"/> (que já existe no Core).
/// As constantes ficam aqui até serem incorporadas a <see cref="AgentOps"/> (alteração no Rbm.Core fora do escopo deste módulo).
/// </summary>
public static class LatencyAgentOps
{
    /// <summary>powercfg /energy filtrado para pedidos de resolução de timer. Payload <see cref="TimerRequestsCaptureRequest"/> → <see cref="TimerRequestsReport"/>.</summary>
    public const string TimerRequests = "latency.timerRequests";
}

/// <summary>Payload proposto para <see cref="LatencyAgentOps.TimerRequests"/>.</summary>
public sealed record TimerRequestsCaptureRequest(int Seconds);

/// <summary>
/// Pontos de entrada para o RBM.Agent (processo elevado). Cada método valida o payload estritamente antes de executar.
/// Todos: <b>Requer elevação — executado via RBM.Agent.</b>
/// </summary>
public static class LatencyAgentHandlers
{
    /// <summary>
    /// <c>latency.capture</c>: valida <see cref="LatencyCaptureRequest.Seconds"/> (5..300) e executa a captura DPC/ISR.
    /// Resposta: <see cref="LatencyReport"/> (serializável com <see cref="AgentJson.Options"/>).
    /// O tempo limite do lado do cliente deve ser ≥ Seconds + 30 s.
    /// </summary>
    public static Task<LatencyReport> CaptureAsync(LatencyCaptureRequest? request, CancellationToken ct = default)
    {
        var options = ValidateCapture(request);
        return new DpcIsrCaptureService().CaptureAsync(options, null, ct);
    }

    /// <summary><c>latency.timerRequests</c> (proposta): valida Seconds (10..120) e executa powercfg /energy. Cliente: timeout ≥ Seconds + 150 s.</summary>
    public static Task<TimerRequestsReport> TimerRequestsAsync(TimerRequestsCaptureRequest? request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Seconds is < TimerResolutionInspector.EnergyMinSeconds or > TimerResolutionInspector.EnergyMaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(request), request.Seconds,
                $"Seconds deve estar entre {TimerResolutionInspector.EnergyMinSeconds} e {TimerResolutionInspector.EnergyMaxSeconds}.");
        return TimerResolutionInspector.CollectTimerRequestsAsync(request.Seconds, ct);
    }

    /// <summary>Validação estrita do payload de captura (usada pelo agente antes de qualquer ação).</summary>
    public static DpcIsrCaptureOptions ValidateCapture(LatencyCaptureRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new DpcIsrCaptureOptions(Seconds: request.Seconds);
        DpcIsrCaptureService.Validate(options);
        return options;
    }
}
