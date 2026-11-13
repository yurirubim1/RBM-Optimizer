using Rbm.Core;
using Rbm.Core.Agent;

namespace Rbm.Processes.Agent;

/// <summary>
/// Pontos de entrada para o RBM.Agent (elevado). TODOS os métodos: "Requer elevação — executado via RBM.Agent".
/// Os payloads vêm da UI (não confiável): cada método revalida tudo (PID &gt; 4, hora de criação, listas de proteção,
/// prioridade na allow-list, máscara dentro do sistema, local/nome de inicialização existentes, nome de serviço,
/// caminho de tarefa). Nenhum método exclui entradas; nenhum aplica tempo real.
/// <list type="bullet">
/// <item><c>process.setPriority</c> → <see cref="ProcessSetPriority(ProcessPriorityCommand)"/></item>
/// <item><c>process.kill</c> → <see cref="ProcessKill(ProcessKillCommand)"/></item>
/// <item><c>service.setStart</c> → <see cref="ServiceSetStart"/></item>
/// <item><c>startup.setEnabled</c> → <see cref="StartupSetEnabled"/></item>
/// <item><c>service.control</c> (proposta) → <see cref="ServiceControlAsync"/></item>
/// <item><c>task.setEnabled</c> (proposta) → <see cref="TaskSetEnabled"/></item>
/// </list>
/// </summary>
public static class ProcessesAgentHandlers
{
    private static readonly ProcessActions Actions = new(elevatedContext: true);

    /// <summary>Requer elevação — executado via RBM.Agent. Contrato atual do Core (sem confirmação de usuário).</summary>
    public static OperationResult<ProcessActionResponse> ProcessSetPriority(ProcessPriorityRequest request) =>
        request is null ? Invalid<ProcessActionResponse>() : ProcessSetPriority(new ProcessPriorityCommand(request.Pid, request.Priority, request.AffinityMask));

    /// <summary>Requer elevação — executado via RBM.Agent. Prioridade (Idle..High) e/ou afinidade.</summary>
    public static OperationResult<ProcessActionResponse> ProcessSetPriority(ProcessPriorityCommand command)
    {
        if (command is null) return Invalid<ProcessActionResponse>();
        if (command.Pid <= 4) return OperationResult<ProcessActionResponse>.Fail("PID inválido ou protegido.", ProcessErrorCodes.InvalidArgument);
        bool hasPriority = !string.IsNullOrWhiteSpace(command.Priority);
        if (!hasPriority && command.AffinityMask is null)
            return OperationResult<ProcessActionResponse>.Fail("Nada a alterar (prioridade e afinidade vazias).", ProcessErrorCodes.InvalidArgument);

        ProcessPriority prio = ProcessPriority.Normal;
        if (hasPriority && !ProcessPriorityMap.TryParseAllowed(command.Priority, out prio, out var perr))
            return OperationResult<ProcessActionResponse>.Fail(perr!, ProcessErrorCodes.InvalidArgument);
        ulong? mask = command.AffinityMask is { } m ? unchecked((ulong)m) : null;
        if (mask is { } mm && !ProcessGuard.ValidateAffinityMask(mm, Native.ProcessNative.SystemAffinityMask, out var merr))
            return OperationResult<ProcessActionResponse>.Fail(merr!, ProcessErrorCodes.InvalidArgument);

        var messages = new List<string>();
        if (hasPriority)
        {
            var r = Actions.SetPriority(command.Pid, prio, command.ExpectedCreateTime, command.UserConfirmed);
            if (!r.Success) return OperationResult<ProcessActionResponse>.Fail(r.Message, r.ErrorCode);
            messages.Add(r.Message);
        }
        if (mask is { } am)
        {
            var r = Actions.SetAffinity(command.Pid, am, command.ExpectedCreateTime, command.UserConfirmed);
            if (!r.Success) return OperationResult<ProcessActionResponse>.Fail(string.Join(" ", messages.Append(r.Message)), r.ErrorCode);
            messages.Add(r.Message);
        }
        string msg = string.Join(" ", messages);
        return OperationResult<ProcessActionResponse>.Ok(new ProcessActionResponse(command.Pid, "setPriority", [command.Pid], [], msg), msg);
    }

    /// <summary>
    /// Requer elevação — executado via RBM.Agent. Contrato atual do Core: <c>Tree = true</c> é tratado como confirmação
    /// explícita da árvore; processos "somente manual" são recusados (o contrato não traz confirmação do usuário).
    /// </summary>
    public static OperationResult<ProcessActionResponse> ProcessKill(ProcessKillRequest request) =>
        request is null ? Invalid<ProcessActionResponse>() : ProcessKill(new ProcessKillCommand(request.Pid, request.Tree, TreeConfirmed: request.Tree));

    /// <summary>Requer elevação — executado via RBM.Agent. Nunca encerra processos críticos.</summary>
    public static OperationResult<ProcessActionResponse> ProcessKill(ProcessKillCommand command)
    {
        if (command is null) return Invalid<ProcessActionResponse>();
        if (command.Pid <= 4) return OperationResult<ProcessActionResponse>.Fail("PID inválido ou protegido.", ProcessErrorCodes.InvalidArgument);
        if (command.Tree)
        {
            var r = Actions.KillTree(command.Pid, command.TreeConfirmed, command.ExpectedCreateTime, command.UserConfirmed);
            var o = r.Value;
            var resp = new ProcessActionResponse(command.Pid, "killTree", o?.Terminated ?? [], o?.Skipped ?? [], r.Message);
            return new OperationResult<ProcessActionResponse>(r.Success, o is null ? null : resp, r.Message, r.ErrorCode);
        }
        var k = Actions.Kill(command.Pid, command.ExpectedCreateTime, command.UserConfirmed);
        return k.Success
            ? OperationResult<ProcessActionResponse>.Ok(new ProcessActionResponse(command.Pid, "kill", [command.Pid], [], k.Message), k.Message)
            : OperationResult<ProcessActionResponse>.Fail(k.Message, k.ErrorCode);
    }

    /// <summary>Requer elevação — executado via RBM.Agent. Recusa serviços protegidos e modos Boot/Sistema; retorna o modo anterior.</summary>
    public static OperationResult<ServiceStartResponse> ServiceSetStart(ServiceStartRequest request) => ServiceManager.SetStartModeElevated(request);

    /// <summary>Requer elevação — executado via RBM.Agent (op proposta <c>service.control</c>). Parar serviços protegidos é recusado.</summary>
    public static Task<OperationResult<ServiceControlResponse>> ServiceControlAsync(ServiceControlRequest request, CancellationToken ct = default) =>
        ServiceManager.ControlElevatedAsync(request, null, ct);

    /// <summary>Requer elevação — executado via RBM.Agent. Somente locais de máquina; via StartupApproved (reversível).</summary>
    public static OperationResult<StartupToggleResponse> StartupSetEnabled(StartupToggleRequest request) => StartupManager.SetEnabledElevated(request);

    /// <summary>Requer elevação — executado via RBM.Agent (op proposta <c>task.setEnabled</c>). Nunca exclui tarefas.</summary>
    public static OperationResult<TaskToggleResponse> TaskSetEnabled(TaskToggleRequest request) => ScheduledTaskManager.SetEnabledElevated(request);

    private static OperationResult<T> Invalid<T>() => OperationResult<T>.Fail("Requisição vazia.", ProcessErrorCodes.InvalidArgument);
}
