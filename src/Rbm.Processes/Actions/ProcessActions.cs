using System.Runtime.InteropServices;
using Rbm.Core;
using Rbm.Processes.Native;
using Rbm.Security;
using static Rbm.Processes.Native.NativeMethods;

namespace Rbm.Processes;

public sealed record PriorityChangeResult(int Pid, string Name, ProcessPriority? Previous, ProcessPriority Current);
public sealed record AffinityChangeResult(int Pid, string Name, ulong Previous, ulong Current);
public sealed record KillOutcome(int RootPid, IReadOnlyList<int> Terminated, IReadOnlyList<string> Skipped, IReadOnlyList<string> Failed);

/// <summary>Regras de proteção aplicadas a QUALQUER ação sobre processos (UI e agente).</summary>
public static class ProcessGuard
{
    /// <summary>
    /// Null = permitido. Processos "somente manual" (explorer, dwm…) exigem <paramref name="userConfirmed"/>;
    /// processos críticos (csrss, lsass, svchost…) são sempre recusados; PIDs 0/4 também. Com
    /// <paramref name="isTermination"/>, o próprio processo (UI ou agente) também é recusado.
    /// </summary>
    public static OperationResult? Check(int pid, string name, string actionPt, bool userConfirmed, bool isTermination = true)
    {
        if (pid is 0 or 4)
            return OperationResult.Fail($"Não é possível {actionPt} um pseudo-processo do kernel.", ProcessErrorCodes.Protected);
        if (isTermination && pid == Environment.ProcessId)
            return OperationResult.Fail("O RBM Optimizer não pode encerrar o próprio processo.", ProcessErrorCodes.Protected);
        string bare = Path.GetFileNameWithoutExtension(name);
        if (Protection.ManualOnlyProcesses.Contains(bare))
            return userConfirmed ? null : OperationResult.Fail(
                $"\"{name}\" afeta a sessão (pode reiniciar a interface ou o áudio). Confirme explicitamente para {actionPt}.",
                ProcessErrorCodes.ConfirmationRequired);
        if (Protection.IsCriticalProcess(name))
            return OperationResult.Fail($"\"{name}\" é um processo crítico do Windows; {actionPt} pode travar o sistema. Ação bloqueada.",
                ProcessErrorCodes.Protected);
        return null;
    }

    /// <summary>Usado por automações (regras): nunca toca processos críticos nem "somente manual".</summary>
    public static bool IsAutomationAllowed(int pid, string name) =>
        pid > 4 && pid != Environment.ProcessId
        && !Protection.IsCriticalProcess(name)
        && !Protection.ManualOnlyProcesses.Contains(Path.GetFileNameWithoutExtension(name));

    /// <summary>Máscara de afinidade válida: não-zero e contida na máscara do sistema.</summary>
    public static bool ValidateAffinityMask(ulong mask, ulong systemMask, out string? error)
    {
        if (mask == 0) { error = "Máscara de afinidade vazia: selecione ao menos um processador lógico."; return false; }
        if ((mask & ~systemMask) != 0)
        {
            error = $"Máscara 0x{mask:X} inclui processadores inexistentes (sistema: 0x{systemMask:X}).";
            return false;
        }
        error = null;
        return true;
    }
}

/// <summary>
/// Ações sobre processos com o token ATUAL (tentativa sem elevação). Falhas de acesso retornam
/// <see cref="ProcessErrorCodes.AccessDenied"/> com sugestão de usar o agente. As variantes elevadas
/// (<see cref="ElevatedProcessOperations"/>) usam exatamente a mesma validação.
/// </summary>
public sealed class ProcessActions
{
    private readonly bool _elevatedContext;

    /// <param name="elevatedContext">true quando executado dentro do RBM.Agent (ajusta as mensagens de erro).</param>
    public ProcessActions(bool elevatedContext = false) => _elevatedContext = elevatedContext;

    /// <summary>Encerra um processo. <paramref name="expectedCreateTime"/> (FILETIME) protege contra PID reutilizado.</summary>
    public OperationResult Kill(int pid, long? expectedCreateTime = null, bool userConfirmed = false)
    {
        var target = Resolve(pid, expectedCreateTime, out var fail);
        if (target is null) return fail!;
        if (ProcessGuard.Check(pid, target.Value.Name, "encerrar", userConfirmed) is { } denied) return denied;
        return TerminateOne(target.Value);
    }

    /// <summary>
    /// Encerra o processo e todos os descendentes (folhas primeiro). Exige <paramref name="confirmTree"/> = true.
    /// Descendentes críticos/"somente manual" são pulados e listados em <see cref="KillOutcome.Skipped"/>.
    /// </summary>
    public OperationResult<KillOutcome> KillTree(int pid, bool confirmTree, long? expectedCreateTime = null, bool userConfirmed = false)
    {
        if (!confirmTree)
            return OperationResult<KillOutcome>.Fail("Encerrar a árvore exige confirmação explícita (todos os processos filhos serão encerrados).",
                ProcessErrorCodes.ConfirmationRequired);
        IReadOnlyList<RawProcessInfo> all;
        try { all = SystemProcessEnumerator.Snapshot(); }
        catch (Exception ex) { return OperationResult<KillOutcome>.Fail("Falha ao enumerar processos: " + ex.Message, ProcessErrorCodes.Failed); }

        RawProcessInfo? root = all.FirstOrDefault(p => p.Pid == pid) is { Pid: > 0 } r ? r : null;
        if (root is null) return OperationResult<KillOutcome>.Fail("O processo não existe mais.", ProcessErrorCodes.NotFound);
        if (expectedCreateTime is { } e && e != 0 && root.Value.CreateTime != e)
            return OperationResult<KillOutcome>.Fail("O PID foi reutilizado por outro processo; atualize a lista.", ProcessErrorCodes.PidReused);
        if (ProcessGuard.Check(pid, root.Value.Name, "encerrar", userConfirmed) is { } denied)
            return OperationResult<KillOutcome>.Fail(denied.Message, denied.ErrorCode);

        var terminated = new List<int>();
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (var d in ProcessTreeBuilder.GetDescendants(all, root.Value.Pid, root.Value.CreateTime))
        {
            if (!ProcessGuard.IsAutomationAllowed(d.Pid, d.Name)) { skipped.Add($"{d.Name} ({d.Pid}): protegido"); continue; }
            var res = TerminateOne(d);
            if (res.Success) terminated.Add(d.Pid);
            else if (res.ErrorCode == ProcessErrorCodes.NotFound) continue; // já terminou junto com o pai
            else failed.Add($"{d.Name} ({d.Pid}): {res.Message}");
        }
        var rootRes = TerminateOne(root.Value);
        if (rootRes.Success) terminated.Add(root.Value.Pid);
        else if (rootRes.ErrorCode != ProcessErrorCodes.NotFound) failed.Add($"{root.Value.Name} ({root.Value.Pid}): {rootRes.Message}");

        var outcome = new KillOutcome(pid, terminated, skipped, failed);
        string msg = $"{terminated.Count} processo(s) encerrado(s)" +
            (skipped.Count > 0 ? $", {skipped.Count} protegido(s) mantido(s)" : "") +
            (failed.Count > 0 ? $", {failed.Count} falha(s)" : "") + ".";
        bool ok = failed.Count == 0;
        if (ok) return new OperationResult<KillOutcome>(true, outcome, msg);
        // Nada encerrado: propaga o código da raiz (ex.: AccessDenied → UI oferece o agente).
        string code = terminated.Count == 0 ? rootRes.ErrorCode ?? ProcessErrorCodes.Failed : ProcessErrorCodes.Failed;
        return new OperationResult<KillOutcome>(false, outcome, terminated.Count == 0 ? $"{msg} {failed[0]}" : msg, code);
    }

    /// <summary>Define a classe de prioridade (Idle..High). Tempo real é sempre recusado.</summary>
    public OperationResult<PriorityChangeResult> SetPriority(int pid, ProcessPriority priority, long? expectedCreateTime = null, bool userConfirmed = false)
    {
        if (!ProcessPriorityMap.IsAllowedToSet(priority, out var perr))
            return OperationResult<PriorityChangeResult>.Fail(perr!, ProcessErrorCodes.InvalidArgument);
        var target = Resolve(pid, expectedCreateTime, out var fail);
        if (target is null) return OperationResult<PriorityChangeResult>.Fail(fail!.Message, fail.ErrorCode);
        if (ProcessGuard.Check(pid, target.Value.Name, "alterar a prioridade de", userConfirmed, isTermination: false) is { } denied)
            return OperationResult<PriorityChangeResult>.Fail(denied.Message, denied.ErrorCode);

        using var h = OpenVerified(target.Value, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, out var openFail);
        if (h is null) return OperationResult<PriorityChangeResult>.Fail(openFail!.Message, openFail.ErrorCode);
        uint prevRaw = GetPriorityClass(h);
        var previous = prevRaw != 0 ? ProcessPriorityMap.FromWin32(prevRaw) : null;
        if (!SetPriorityClass(h, ProcessPriorityMap.ToWin32(priority)))
        {
            var f = Win32Messages.Fail("Não foi possível alterar a prioridade", Marshal.GetLastPInvokeError(), _elevatedContext);
            return OperationResult<PriorityChangeResult>.Fail(f.Message, f.ErrorCode);
        }
        return OperationResult<PriorityChangeResult>.Ok(new PriorityChangeResult(pid, target.Value.Name, previous, priority),
            $"Prioridade de {target.Value.Name} ({pid}) alterada para {ProcessPriorityMap.Display(priority)}.");
    }

    /// <summary>Define a máscara de afinidade (não-zero e contida na máscara do sistema).</summary>
    public OperationResult<AffinityChangeResult> SetAffinity(int pid, ulong mask, long? expectedCreateTime = null, bool userConfirmed = false)
    {
        if (!ProcessGuard.ValidateAffinityMask(mask, ProcessNative.SystemAffinityMask, out var merr))
            return OperationResult<AffinityChangeResult>.Fail(merr!, ProcessErrorCodes.InvalidArgument);
        var target = Resolve(pid, expectedCreateTime, out var fail);
        if (target is null) return OperationResult<AffinityChangeResult>.Fail(fail!.Message, fail.ErrorCode);
        if (ProcessGuard.Check(pid, target.Value.Name, "alterar a afinidade de", userConfirmed, isTermination: false) is { } denied)
            return OperationResult<AffinityChangeResult>.Fail(denied.Message, denied.ErrorCode);

        using var h = OpenVerified(target.Value, PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, out var openFail);
        if (h is null) return OperationResult<AffinityChangeResult>.Fail(openFail!.Message, openFail.ErrorCode);
        var cur = ProcessNative.GetAffinity(h);
        if (cur is { } c && !ProcessGuard.ValidateAffinityMask(mask, c.systemMask, out merr))
            return OperationResult<AffinityChangeResult>.Fail(merr!, ProcessErrorCodes.InvalidArgument);
        if (!SetProcessAffinityMask(h, (nuint)mask))
        {
            var f = Win32Messages.Fail("Não foi possível alterar a afinidade", Marshal.GetLastPInvokeError(), _elevatedContext);
            return OperationResult<AffinityChangeResult>.Fail(f.Message, f.ErrorCode);
        }
        return OperationResult<AffinityChangeResult>.Ok(new AffinityChangeResult(pid, target.Value.Name, cur?.processMask ?? 0, mask),
            $"Afinidade de {target.Value.Name} ({pid}) definida para 0x{mask:X}.");
    }

    // ---- internos ----

    private static RawProcessInfo? Resolve(int pid, long? expectedCreateTime, out OperationResult? fail)
    {
        fail = null;
        if (pid <= 0) { fail = OperationResult.Fail("PID inválido.", ProcessErrorCodes.InvalidArgument); return null; }
        RawProcessInfo? info;
        try { info = SystemProcessEnumerator.Find(pid); }
        catch (Exception ex) { fail = OperationResult.Fail("Falha ao enumerar processos: " + ex.Message, ProcessErrorCodes.Failed); return null; }
        if (info is null) { fail = OperationResult.Fail("O processo não existe mais.", ProcessErrorCodes.NotFound); return null; }
        if (expectedCreateTime is { } e && e != 0 && info.Value.CreateTime != e)
        {
            fail = OperationResult.Fail("O PID foi reutilizado por outro processo; atualize a lista.", ProcessErrorCodes.PidReused);
            return null;
        }
        return info;
    }

    /// <summary>Abre o processo e confirma que a hora de criação ainda confere (corrida com reutilização de PID).</summary>
    private ProcessHandle? OpenVerified(RawProcessInfo target, uint access, out OperationResult? fail)
    {
        fail = null;
        var h = ProcessNative.Open(target.Pid, access, out int err);
        if (h.IsInvalid)
        {
            h.Dispose();
            fail = err == Win32Messages.ERROR_INVALID_PARAMETER
                ? OperationResult.Fail("O processo não existe mais.", ProcessErrorCodes.NotFound)
                : Win32Messages.Fail($"Não foi possível abrir {target.Name} ({target.Pid})", err, _elevatedContext);
            return null;
        }
        if (ProcessNative.GetCreateTime(h) is { } ct && target.CreateTime != 0 && ct != target.CreateTime)
        {
            h.Dispose();
            fail = OperationResult.Fail("O PID foi reutilizado por outro processo; atualize a lista.", ProcessErrorCodes.PidReused);
            return null;
        }
        return h;
    }

    private OperationResult TerminateOne(RawProcessInfo target)
    {
        using var h = OpenVerified(target, PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, out var fail);
        if (h is null) return fail!;
        if (!TerminateProcess(h, 1))
        {
            int err = Marshal.GetLastPInvokeError();
            return Win32Messages.Fail($"Não foi possível encerrar {target.Name} ({target.Pid})", err, _elevatedContext);
        }
        _ = WaitForSingleObject(h, 3000);
        return OperationResult.Ok($"{target.Name} ({target.Pid}) encerrado.");
    }
}
