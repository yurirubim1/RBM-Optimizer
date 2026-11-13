using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Processes.Native;
using Microsoft.Extensions.Logging;

namespace Rbm.Processes;

/// <summary>Registro de uma ação do motor de regras.</summary>
public sealed record RuleActionLog(DateTimeOffset Timestamp, int Pid, string ProcessName, string RuleId, string Action, bool Success, string Message);

public sealed record ProcessRuleWatcherOptions
{
    /// <summary>Intervalo da varredura (mínimo 500 ms). A varredura é uma única syscall + comparação de nomes.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Aplica também aos processos que já estavam abertos quando o observador iniciou.</summary>
    public bool ApplyToExistingOnStart { get; init; } = true;
    public int MaxLogEntries { get; init; } = 500;
}

/// <summary>
/// Observador que aplica <see cref="ProcessRule"/>s UMA vez a cada processo novo correspondente (rastreia PID + hora de
/// criação). Nunca toca processos críticos/"somente manual". Roda sem elevação; se receber
/// <c>elevatedFallback</c>, usa-o quando o acesso é negado (ex.: jogo executando como administrador).
/// </summary>
public sealed class ProcessRuleWatcher : IDisposable
{
    private readonly Func<IReadOnlyList<ProcessRule>> _rulesProvider;
    private readonly IReadOnlyList<CoreInfo>? _cores;
    private readonly ProcessRuleWatcherOptions _options;
    private readonly ILogger? _logger;
    private readonly Func<ProcessPriorityRequest, CancellationToken, Task<OperationResult>>? _elevatedFallback;
    private readonly ProcessActions _actions = new();
    private readonly SystemProcessEnumerator _enumerator = new(256 * 1024);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly HashSet<ProcessKey> _evaluated = new();
    private readonly HashSet<ProcessKey> _applied = new();
    private readonly LinkedList<RuleActionLog> _log = new();
    private readonly object _logLock = new();
    private string _rulesFingerprint = "";
    private bool _firstScan = true;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <param name="rulesProvider">Fonte das regras (lida a cada varredura; alterações reavaliam processos já vistos).</param>
    /// <param name="cores">Topologia (Rbm.Core.Hardware.CpuInfo.Cores) para presets de afinidade.</param>
    /// <param name="elevatedFallback">Opcional: envia ao agente (op process.setPriority) quando o acesso é negado.</param>
    public ProcessRuleWatcher(Func<IReadOnlyList<ProcessRule>> rulesProvider, IReadOnlyList<CoreInfo>? cores,
        ProcessRuleWatcherOptions? options = null, ILogger? logger = null,
        Func<ProcessPriorityRequest, CancellationToken, Task<OperationResult>>? elevatedFallback = null)
    {
        _rulesProvider = rulesProvider;
        _cores = cores;
        _options = options ?? new ProcessRuleWatcherOptions();
        _logger = logger;
        _elevatedFallback = elevatedFallback;
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public event EventHandler<RuleActionLog>? ActionLogged;

    public IReadOnlyList<RuleActionLog> RecentActions
    {
        get { lock (_logLock) return _log.ToList(); }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var interval = _options.Interval < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : _options.Interval;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                try { await ScanOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger?.LogWarning(ex, "Falha na varredura de regras de processos"); }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }, ct).ContinueWith(_ => { }, TaskScheduler.Default); // engole o cancelamento
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null || loop is null) return;
        cts.Cancel();
        await loop.ConfigureAwait(false);
        cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();

    /// <summary>Uma varredura: aplica regras aos processos ainda não avaliados. Retorna nº de ações realizadas.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rules = _rulesProvider().Where(r => r.Enabled && ProcessRuleValidator.Validate(r).Count == 0).ToList();
            string fp = Fingerprint(rules);
            if (fp != _rulesFingerprint) { _evaluated.Clear(); _rulesFingerprint = fp; } // regras mudaram: reavalia (sem reaplicar)

            var procs = _enumerator.Enumerate();
            var alive = new HashSet<ProcessKey>(procs.Count);
            foreach (var p in procs) alive.Add(p.Key);
            _evaluated.IntersectWith(alive);
            _applied.IntersectWith(alive);

            if (_firstScan && !_options.ApplyToExistingOnStart)
            {
                _firstScan = false;
                _evaluated.UnionWith(alive);
                return 0;
            }
            _firstScan = false;
            if (rules.Count == 0) { _evaluated.UnionWith(alive); return 0; }

            ulong systemMask = ProcessNative.SystemAffinityMask;
            int actions = 0;
            foreach (var p in procs)
            {
                ct.ThrowIfCancellationRequested();
                if (!_evaluated.Add(p.Key) || _applied.Contains(p.Key)) continue;
                if (!ProcessGuard.IsAutomationAllowed(p.Pid, p.Name)) continue;
                if (!ProcessRuleMatcher.CandidatesByName(rules, p.Name).Any()) continue; // filtro barato por nome

                string? path = ProcessNative.GetImagePath(p.Pid);
                var rule = ProcessRuleMatcher.Match(rules, p.Pid, p.Name, path);
                if (rule is null) continue;
                _applied.Add(p.Key);
                actions += await ApplyAsync(rule, p, systemMask, ct).ConfigureAwait(false);
            }
            return actions;
        }
        finally { _scanLock.Release(); }
    }

    private async Task<int> ApplyAsync(ProcessRule rule, RawProcessInfo p, ulong systemMask, CancellationToken ct)
    {
        int count = 0;
        ulong? mask = null;
        if (rule.Affinity != AffinityPreset.None)
        {
            var comp = AffinityPresets.Compute(rule.Affinity, _cores, systemMask, rule.AffinityMask);
            if (comp.Mask is null) Log(p, rule, "afinidade", false, comp.Note);
            else mask = comp.Mask;
        }

        if (rule.Priority is { } prio)
        {
            var r = _actions.SetPriority(p.Pid, prio, p.CreateTime);
            if (!r.Success && r.ErrorCode == ProcessErrorCodes.AccessDenied && _elevatedFallback is not null)
            {
                var er = await _elevatedFallback(new ProcessPriorityRequest(p.Pid, prio.ToString(), mask is { } m ? unchecked((long)m) : null), ct).ConfigureAwait(false);
                Log(p, rule, "prioridade+afinidade (agente)", er.Success, er.Message);
                return er.Success ? 1 : 0;
            }
            Log(p, rule, "prioridade", r.Success, r.Message);
            if (r.Success) count++;
        }
        if (mask is { } am)
        {
            var r = _actions.SetAffinity(p.Pid, am, p.CreateTime);
            if (!r.Success && r.ErrorCode == ProcessErrorCodes.AccessDenied && _elevatedFallback is not null && rule.Priority is null
                && ReadPriority(p.Pid) is { } current && current != ProcessPriority.RealTime)
            {
                // Só afinidade: o contrato do agente exige prioridade, então reenvia a prioridade ATUAL (sem alterá-la).
                var er = await _elevatedFallback(new ProcessPriorityRequest(p.Pid, current.ToString(), unchecked((long)am)), ct).ConfigureAwait(false);
                Log(p, rule, "afinidade (agente)", er.Success, er.Message);
                return er.Success ? count + 1 : count;
            }
            Log(p, rule, "afinidade", r.Success, r.Message);
            if (r.Success) count++;
        }
        return count;
    }

    private static ProcessPriority? ReadPriority(int pid)
    {
        using var h = ProcessNative.Open(pid, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, out _);
        if (h.IsInvalid) return null;
        uint pc = NativeMethods.GetPriorityClass(h);
        return pc == 0 ? null : ProcessPriorityMap.FromWin32(pc);
    }

    private void Log(RawProcessInfo p, ProcessRule rule, string action, bool success, string message)
    {
        var entry = new RuleActionLog(DateTimeOffset.Now, p.Pid, p.Name, rule.Id, action, success, message);
        lock (_logLock)
        {
            _log.AddLast(entry);
            while (_log.Count > Math.Max(10, _options.MaxLogEntries)) _log.RemoveFirst();
        }
        if (success) _logger?.LogInformation("Regra {Rule} aplicada a {Name} ({Pid}): {Action} — {Message}", rule.Id, p.Name, p.Pid, action, message);
        else _logger?.LogWarning("Regra {Rule} falhou em {Name} ({Pid}): {Action} — {Message}", rule.Id, p.Name, p.Pid, action, message);
        try { ActionLogged?.Invoke(this, entry); } catch { }
    }

    private static string Fingerprint(IReadOnlyList<ProcessRule> rules) =>
        string.Join("|", rules.Select(r => $"{r.Id}:{r.ExecutableName}:{r.Priority}:{r.Affinity}:{r.AffinityMask}:{string.Join(",", r.Exceptions)}"));

    public void Dispose()
    {
        if (_disposed) return;
        try { Stop(); } catch { }
        _disposed = true;
        _enumerator.Dispose();
        _scanLock.Dispose();
    }
}
