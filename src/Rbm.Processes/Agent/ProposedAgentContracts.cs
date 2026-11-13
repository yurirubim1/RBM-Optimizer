namespace Rbm.Processes.Agent;

/// <summary>
/// Operações e payloads PROPOSTOS para Rbm.Core.Agent (ainda não existem em AgentOps). Os registros de comando são
/// SUPERCONJUNTOS compatíveis em JSON dos registros do Core (mesmos nomes de propriedades + campos opcionais), então o
/// agente pode desserializar o payload atual diretamente neles.
/// </summary>
public static class ProposedAgentOps
{
    /// <summary>Habilitar/desabilitar tarefa agendada. Payload <see cref="TaskToggleRequest"/> → <see cref="TaskToggleResponse"/>.</summary>
    public const string TaskSetEnabled = "task.setEnabled";
    /// <summary>Iniciar/parar/reiniciar serviço. Payload <see cref="ServiceControlRequest"/> → <see cref="ServiceControlResponse"/>.</summary>
    public const string ServiceControl = "service.control";
}

/// <summary>
/// Superconjunto de <c>ProcessKillRequest(Pid, Tree)</c>: adiciona confirmação explícita da árvore, confirmação do usuário
/// para processos "somente manual" e a hora de criação esperada (FILETIME) contra reutilização de PID.
/// </summary>
public sealed record ProcessKillCommand(int Pid, bool Tree, bool TreeConfirmed = false, bool UserConfirmed = false, long? ExpectedCreateTime = null);

/// <summary>
/// Superconjunto de <c>ProcessPriorityRequest(Pid, Priority, AffinityMask)</c>. <see cref="Priority"/> vazio = só afinidade.
/// </summary>
public sealed record ProcessPriorityCommand(int Pid, string? Priority, long? AffinityMask, bool UserConfirmed = false, long? ExpectedCreateTime = null);

/// <summary>Resposta comum das operações de processo no agente.</summary>
public sealed record ProcessActionResponse(int Pid, string Action, IReadOnlyList<int> Affected, IReadOnlyList<string> Skipped, string Message);

public sealed record TaskToggleRequest(string TaskPath, bool Enabled);
public sealed record TaskToggleResponse(string TaskPath, bool PreviousEnabled, bool CurrentEnabled);

public enum ServiceControlAction { Start, Stop, Restart }
public sealed record ServiceControlRequest(string ServiceName, ServiceControlAction Action);
public sealed record ServiceControlResponse(string ServiceName, ServiceRunState Previous, ServiceRunState Current);

/// <summary>Resposta proposta para <c>startup.setEnabled</c> (hoje sem payload de resposta no Core).</summary>
public sealed record StartupToggleResponse(string Location, string Name, bool PreviousEnabled, bool CurrentEnabled);
