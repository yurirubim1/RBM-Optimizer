using System.Runtime.InteropServices;
using Rbm.Core;
using Rbm.Processes.Agent;

namespace Rbm.Processes;

public enum ScheduledTaskState { Unknown, Disabled, Queued, Ready, Running }

public enum TaskTriggerKind { Event, Time, Daily, Weekly, Monthly, MonthlyDayOfWeek, Idle, Registration, Boot, Logon, SessionStateChange, Custom, Unknown }

public sealed record ScheduledTaskAction(string Kind, string? Path, string? Arguments, string? WorkingDirectory, string? ClassId, string? ResolvedPath);

public sealed record ScheduledTaskEntry(
    string Path,
    string Name,
    string Folder,
    ScheduledTaskState State,
    bool Enabled,
    DateTime? LastRunTime,
    int? LastResult,
    string LastResultDisplay,
    DateTime? NextRunTime,
    IReadOnlyList<TaskTriggerKind> TriggerKinds,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<ScheduledTaskAction> Actions,
    string? Author,
    bool? Hidden,
    PublisherInfo? Publisher,
    bool IsMicrosoftFolder,
    bool IsProtected,
    AutostartRecommendation Recommendation,
    string? Note);

/// <summary>
/// Tarefas agendadas via COM do Agendador ("Schedule.Service", late binding com <c>dynamic</c>), recursivo a partir
/// de "\", incluindo tarefas ocultas. Habilitar/desabilitar altera só o sinalizador Enabled (nunca exclui).
/// Tarefas de atualização/segurança do Windows (<see cref="ProtectedTaskPrefixes"/>) não podem ser desabilitadas.
/// </summary>
public sealed class ScheduledTaskManager
{
    private const int TASK_ENUM_HIDDEN = 1;
    private static readonly DateTime NeverRun = new(1899, 12, 30);

    /// <summary>Pastas cujas tarefas o RBM Optimizer nunca desabilita (atualização, Defender, licenciamento, TPM…).</summary>
    public static readonly IReadOnlyList<string> ProtectedTaskPrefixes =
    [
        @"\Microsoft\Windows\UpdateOrchestrator\", @"\Microsoft\Windows\WindowsUpdate\", @"\Microsoft\Windows\WaaSMedic\",
        @"\Microsoft\Windows\Windows Defender\", @"\Microsoft\Windows\ExploitGuard\", @"\Microsoft\Windows\TPM\",
        @"\Microsoft\Windows\SoftwareProtectionPlatform\", @"\Microsoft\Windows\BitLocker\", @"\Microsoft\Windows\InstallService\",
        @"\Microsoft\Windows\Servicing\", @"\Microsoft\Windows\AppID\", @"\Microsoft\Windows\Time Synchronization\",
    ];

    public static bool IsProtectedTask(string path) => ProtectedTaskPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Caminho de tarefa aceitável vindo do canal: absoluto ("\..."), sem "..", "/", controle; até 512 caracteres.</summary>
    public static OperationResult ValidateTaskPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || !path.StartsWith('\\') || path.EndsWith('\\') || path.Length < 2
            || path.Contains("..") || path.Contains('/') || path.Contains(@"\\") || path.Any(char.IsControl))
            return OperationResult.Fail("Caminho de tarefa inválido (use o caminho completo, ex.: \\Pasta\\Tarefa).", ProcessErrorCodes.InvalidArgument);
        return OperationResult.Ok();
    }

    // ---------------- Enumeração ----------------

    public async Task<OperationResult<IReadOnlyList<ScheduledTaskEntry>>> EnumerateAsync(bool verifySignatures = true, CancellationToken ct = default)
    {
        List<RawTask> raw;
        try { raw = await Task.Run(() => EnumerateRaw(ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return OperationResult<IReadOnlyList<ScheduledTaskEntry>>.Fail("Agendador de Tarefas indisponível: " + ex.Message, ProcessErrorCodes.Unavailable); }

        var paths = raw.Select(r => r.Actions.FirstOrDefault(a => a.ResolvedPath is not null)?.ResolvedPath).Where(p => p is not null).Cast<string>();
        var meta = await FileMetadataCache.GetManyAsync(paths, verifySignatures, 2, ct).ConfigureAwait(false);

        var list = new List<ScheduledTaskEntry>(raw.Count);
        foreach (var r in raw)
        {
            var main = r.Actions.FirstOrDefault(a => a.ResolvedPath is not null);
            PublisherInfo? pub = main?.ResolvedPath is { } p && meta.TryGetValue(p, out var m) ? m : null;
            bool prot = IsProtectedTask(r.Path);
            bool exists = main?.ResolvedPath is { } rp && (pub?.Exists ?? File.Exists(rp));
            var cmd = main is null ? null : $"{main.Path} {main.Arguments}".Trim();
            var rec = main is null
                ? new AutostartRecommendation(r.IsMicrosoftFolder ? AutostartRecommendationKind.Keep : AutostartRecommendationKind.Unknown,
                    r.IsMicrosoftFolder ? "Tarefa do Windows sem executável próprio (ação COM/sistema)." : "Ação sem executável identificável.")
                : AutostartAdvisor.Recommend(new AutostartFacts(AutostartKind.ScheduledTask, r.Name, cmd, main.ResolvedPath, exists, pub, prot));
            list.Add(new ScheduledTaskEntry(r.Path, r.Name, r.Folder, r.State, r.Enabled, r.LastRun, r.LastResult, DescribeResult(r.LastResult),
                r.NextRun, r.TriggerKinds, r.Triggers, r.Actions, r.Author, r.Hidden, pub, r.IsMicrosoftFolder, prot, rec, r.Note));
        }
        return OperationResult<IReadOnlyList<ScheduledTaskEntry>>.Ok(list, $"{list.Count} tarefa(s).");
    }

    private sealed record RawTask(string Path, string Name, string Folder, ScheduledTaskState State, bool Enabled, DateTime? LastRun, int? LastResult,
        DateTime? NextRun, IReadOnlyList<TaskTriggerKind> TriggerKinds, IReadOnlyList<string> Triggers, IReadOnlyList<ScheduledTaskAction> Actions,
        string? Author, bool? Hidden, bool IsMicrosoftFolder, string? Note);

    private static List<RawTask> EnumerateRaw(CancellationToken ct)
    {
        dynamic service = CreateService();
        try
        {
            var list = new List<RawTask>();
            var stack = new Stack<dynamic>();
            stack.Push(service.GetFolder(@"\"));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                dynamic folder = stack.Pop();
                string folderPath = (string)folder.Path;
                try
                {
                    dynamic tasks = folder.GetTasks(TASK_ENUM_HIDDEN);
                    int count = (int)tasks.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        try { list.Add(ReadTask(tasks.Item(i), folderPath)); }
                        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    }
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { /* pasta inacessível sem elevação */ }
                try
                {
                    dynamic subs = folder.GetFolders(0);
                    int n = (int)subs.Count;
                    for (int i = 1; i <= n; i++) stack.Push(subs.Item(i));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
            }
            return list;
        }
        finally { Release(service); }
    }

    private static RawTask ReadTask(dynamic task, string folderPath)
    {
        string path = (string)task.Path;
        string name = (string)task.Name;
        var state = (int)task.State switch { 1 => ScheduledTaskState.Disabled, 2 => ScheduledTaskState.Queued, 3 => ScheduledTaskState.Ready, 4 => ScheduledTaskState.Running, _ => ScheduledTaskState.Unknown };
        bool enabled = (bool)task.Enabled;
        DateTime? lastRun = null, nextRun = null;
        int? lastResult = null;
        try { var d = (DateTime)task.LastRunTime; if (d > NeverRun.AddDays(1)) lastRun = d; } catch { }
        try { var d = (DateTime)task.NextRunTime; if (d > NeverRun.AddDays(1)) nextRun = d; } catch { }
        try { lastResult = (int)task.LastTaskResult; } catch { }

        var kinds = new List<TaskTriggerKind>();
        var triggers = new List<string>();
        var actions = new List<ScheduledTaskAction>();
        string? author = null, note = null;
        bool? hidden = null;
        try
        {
            dynamic def = task.Definition;
            try { author = ServiceManager.LoadIndirect((string?)def.RegistrationInfo.Author); } catch { }
            try { hidden = (bool)def.Settings.Hidden; } catch { }
            dynamic trig = def.Triggers;
            int tc = (int)trig.Count;
            for (int i = 1; i <= tc; i++)
            {
                dynamic t = trig.Item(i);
                var kind = MapTrigger((int)t.Type);
                kinds.Add(kind);
                string text = DescribeTrigger(kind, t);
                bool tEnabled = true;
                try { tEnabled = (bool)t.Enabled; } catch { }
                triggers.Add(tEnabled ? text : text + " (gatilho desativado)");
            }
            dynamic acts = def.Actions;
            int ac = (int)acts.Count;
            for (int i = 1; i <= ac; i++)
            {
                dynamic a = acts.Item(i);
                int type = (int)a.Type;
                if (type == 0)
                {
                    string? exe = (string?)a.Path, args = (string?)a.Arguments, wd = (string?)a.WorkingDirectory;
                    string? resolved = string.IsNullOrWhiteSpace(exe) ? null : CommandLineParser.ResolveExecutable(exe.Trim().Trim('"'));
                    // rundll32/regsvr32: usa a DLL hospedada.
                    if (resolved is not null)
                    {
                        var parsed = CommandLineParser.Parse($"\"{resolved}\" {args}");
                        resolved = parsed.EffectivePath ?? resolved;
                    }
                    actions.Add(new ScheduledTaskAction("Exec", exe, args, wd, null, resolved));
                }
                else if (type == 5)
                {
                    string? clsid = (string?)a.ClassId;
                    actions.Add(new ScheduledTaskAction("ComHandler", null, (string?)a.Data, null, clsid, ComRegistry.ResolveInprocServer(clsid)));
                }
                else actions.Add(new ScheduledTaskAction(type == 6 ? "Email" : type == 7 ? "Message" : $"Tipo {type}", null, null, null, null, null));
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            note = "Definição da tarefa inacessível sem elevação (gatilhos/ações indisponíveis).";
        }
        bool ms = folderPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || folderPath.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase);
        return new RawTask(path, name, folderPath, state, enabled, lastRun, lastResult, nextRun, kinds, triggers, actions, author, hidden, ms, note);
    }

    public static TaskTriggerKind MapTrigger(int t) => t switch
    {
        0 => TaskTriggerKind.Event, 1 => TaskTriggerKind.Time, 2 => TaskTriggerKind.Daily, 3 => TaskTriggerKind.Weekly,
        4 => TaskTriggerKind.Monthly, 5 => TaskTriggerKind.MonthlyDayOfWeek, 6 => TaskTriggerKind.Idle, 7 => TaskTriggerKind.Registration,
        8 => TaskTriggerKind.Boot, 9 => TaskTriggerKind.Logon, 11 => TaskTriggerKind.SessionStateChange, 12 => TaskTriggerKind.Custom,
        _ => TaskTriggerKind.Unknown,
    };

    private static string DescribeTrigger(TaskTriggerKind kind, dynamic t)
    {
        string start = "";
        try { string s = (string)t.StartBoundary; if (!string.IsNullOrEmpty(s)) start = $" (a partir de {s.Replace('T', ' ')})"; } catch { }
        return kind switch
        {
            TaskTriggerKind.Boot => "Na inicialização do sistema",
            TaskTriggerKind.Logon => "No logon",
            TaskTriggerKind.Idle => "Quando o computador fica ocioso",
            TaskTriggerKind.Time => "Uma vez" + start,
            TaskTriggerKind.Daily => "Diariamente" + start,
            TaskTriggerKind.Weekly => "Semanalmente" + start,
            TaskTriggerKind.Monthly or TaskTriggerKind.MonthlyDayOfWeek => "Mensalmente" + start,
            TaskTriggerKind.Event => "Em um evento do sistema",
            TaskTriggerKind.Registration => "Ao criar/modificar a tarefa",
            TaskTriggerKind.SessionStateChange => "Em mudança de sessão (bloqueio/desbloqueio/conexão)",
            TaskTriggerKind.Custom => "Gatilho personalizado",
            _ => "Gatilho desconhecido",
        };
    }

    public static string DescribeResult(int? r) => r switch
    {
        null => "—",
        0 => "Concluída com sucesso",
        0x41300 => "Pronta",
        0x41301 => "Em execução",
        0x41302 => "Desabilitada",
        0x41303 => "Ainda não executada",
        0x41306 => "Encerrada pelo usuário",
        unchecked((int)0x8004131F) => "Já havia uma instância em execução",
        unchecked((int)0x800710E0) => "Recusada pelo operador/condições",
        { } v => $"Código 0x{v:X8}",
    };

    // ---------------- Habilitar/desabilitar ----------------

    /// <summary>Tentativa com o token atual (funciona para tarefas do próprio usuário). AccessDenied → use o agente.</summary>
    public Task<OperationResult<TaskToggleResponse>> SetEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default) =>
        Task.Run(() => SetEnabledCore(new TaskToggleRequest(taskPath, enabled), elevated: false), ct);

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (op proposta <c>task.setEnabled</c>). Só altera o sinalizador Enabled
    /// (reversível, nunca exclui). Desabilitar tarefas protegidas é recusado.
    /// </summary>
    public static OperationResult<TaskToggleResponse> SetEnabledElevated(TaskToggleRequest request) => SetEnabledCore(request, elevated: true);

    private static OperationResult<TaskToggleResponse> SetEnabledCore(TaskToggleRequest? request, bool elevated)
    {
        if (request is null) return OperationResult<TaskToggleResponse>.Fail("Requisição vazia.", ProcessErrorCodes.InvalidArgument);
        var v = ValidateTaskPath(request.TaskPath);
        if (!v.Success) return OperationResult<TaskToggleResponse>.Fail(v.Message, v.ErrorCode);
        if (!request.Enabled && IsProtectedTask(request.TaskPath))
            return OperationResult<TaskToggleResponse>.Fail("Tarefa de atualização/segurança do Windows: o RBM Optimizer não a desabilita.", ProcessErrorCodes.Protected);

        int split = request.TaskPath.LastIndexOf('\\');
        string folderPath = split == 0 ? @"\" : request.TaskPath[..split];
        string name = request.TaskPath[(split + 1)..];
        dynamic? service = null;
        try
        {
            service = CreateService();
            dynamic task = service.GetFolder(folderPath).GetTask(name);
            bool previous = (bool)task.Enabled;
            if (previous != request.Enabled) task.Enabled = request.Enabled;
            bool current = (bool)task.Enabled;
            return OperationResult<TaskToggleResponse>.Ok(new TaskToggleResponse(request.TaskPath, previous, current),
                previous == request.Enabled
                    ? "Nenhuma alteração necessária."
                    : $"Tarefa {(current ? "habilitada" : "desabilitada")} (reversível).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException c && c.HResult == unchecked((int)0x80070005)))
        {
            return OperationResult<TaskToggleResponse>.Fail(Win32Messages.Describe(Win32Messages.ERROR_ACCESS_DENIED, elevated), ProcessErrorCodes.AccessDenied);
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException || (ex is COMException c2 && (c2.HResult == unchecked((int)0x80070002) || c2.HResult == unchecked((int)0x80070003))))
        {
            return OperationResult<TaskToggleResponse>.Fail("Tarefa não encontrada.", ProcessErrorCodes.NotFound);
        }
        catch (Exception ex)
        {
            return OperationResult<TaskToggleResponse>.Fail("Falha ao alterar a tarefa: " + ex.Message, ProcessErrorCodes.Failed);
        }
        finally { if (service is not null) Release(service); }
    }

    private static dynamic CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    private static void Release(object o)
    {
        try { if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
    }
}
