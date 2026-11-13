using System.Diagnostics;
using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Processes.Native;

namespace Rbm.Processes;

/// <summary>
/// Amostrador de processos de baixo custo: uma syscall (NtQuerySystemInformation) por amostra, deltas de CPU e IO
/// calculados contra a amostra anterior (CPU% normalizada pelo nº de processadores lógicos, como o Gerenciador de Tarefas).
/// Se a chamada nativa falhar, cai para System.Diagnostics.Process (mais lento, sem PID pai e com contadores parciais).
/// Thread-safe (chamadas serializadas).
/// </summary>
public sealed class ProcessSampler : IProcessMonitor, IDisposable
{
    public const string NativeSource = "NtQuerySystemInformation";
    public const string FallbackSource = "System.Diagnostics.Process (fallback)";

    private readonly object _lock = new();
    private readonly ProcessSamplerOptions _options;
    private SystemProcessEnumerator? _enumerator;
    private Dictionary<ProcessKey, RawProcessInfo> _previous = new();
    private long _previousStopwatch;
    private long _previousUtcFileTime;
    private readonly Dictionary<ProcessKey, string?> _paths = new();
    private string? _nativeError;
    private bool _disposed;

    public ProcessSampler(ProcessSamplerOptions? options = null)
    {
        _options = options ?? new ProcessSamplerOptions();
        LogicalProcessors = ProcessNative.LogicalProcessorCount;
    }

    public int LogicalProcessors { get; }

    /// <summary>Última amostra produzida (null antes da primeira).</summary>
    public ProcessSnapshot? Last { get; private set; }

    /// <summary>Motivo da queda para o modo fallback (null se o modo nativo está ativo).</summary>
    public string? NativeError => _nativeError;

    /// <summary>
    /// Coleta todos os processos. Na primeira chamada as taxas (CPU%, IO/s) ficam null — chame novamente após
    /// o intervalo desejado (ex.: 1 s). Processos criados entre duas amostras têm taxa calculada desde a criação.
    /// </summary>
    public ProcessSnapshot Capture()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long t0 = Stopwatch.GetTimestamp();
            IReadOnlyList<RawProcessInfo> raw;
            string source;
            string? warning = null;
            if (_nativeError is null)
            {
                try
                {
                    _enumerator ??= new SystemProcessEnumerator();
                    raw = _enumerator.Enumerate();
                    source = NativeSource;
                }
                catch (Exception ex)
                {
                    _nativeError = ex.Message;
                    _enumerator?.Dispose();
                    _enumerator = null;
                    raw = FallbackEnumerate();
                    source = FallbackSource;
                }
            }
            else
            {
                raw = FallbackEnumerate();
                source = FallbackSource;
            }
            if (source == FallbackSource)
                warning = $"Modo alternativo (System.Diagnostics.Process): {_nativeError}. PID pai indisponível; CPU/IO indisponíveis para processos protegidos.";

            long now = Stopwatch.GetTimestamp();
            long nowUtc = DateTime.UtcNow.ToFileTimeUtc();
            TimeSpan? interval = _previousStopwatch == 0 ? null : Stopwatch.GetElapsedTime(_previousStopwatch, now);

            Func<RawProcessInfo, string?>? pathOf = _options.ResolveImagePaths ? ResolvePath : null;
            var entries = ProcessRateCalculator.Compute(raw, _previous, interval, _previousUtcFileTime, nowUtc, LogicalProcessors, pathOf);

            // Estado para a próxima amostra.
            var map = new Dictionary<ProcessKey, RawProcessInfo>(raw.Count);
            foreach (var r in raw) map[r.Key] = r;
            _previous = map;
            _previousStopwatch = now;
            _previousUtcFileTime = nowUtc;
            if (_paths.Count > map.Count + 64)
                foreach (var k in _paths.Keys.Where(k => !map.ContainsKey(k)).ToList()) _paths.Remove(k);

            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var snap = new ProcessSnapshot(DateTimeOffset.Now, entries, interval, LogicalProcessors, source, ms, warning);
            Last = snap;
            return snap;
        }
    }

    IReadOnlyList<ProcessSample> IProcessMonitor.Sample() => Capture().ToProcessSamples();

    /// <summary>Esquece a amostra anterior (a próxima não terá taxas).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _previous = new();
            _previousStopwatch = 0;
            _previousUtcFileTime = 0;
        }
    }

    private string? ResolvePath(RawProcessInfo r)
    {
        if (r.Pid is 0 or 4) return null;
        if (_paths.TryGetValue(r.Key, out var p)) return p;
        p = ProcessNative.GetImagePath(r.Pid); // falha (processo protegido) fica em cache como null
        _paths[r.Key] = p;
        return p;
    }

    private static IReadOnlyList<RawProcessInfo> FallbackEnumerate()
    {
        var procs = Process.GetProcesses();
        var list = new List<RawProcessInfo>(procs.Length);
        foreach (var p in procs)
        {
            try
            {
                int pid = p.Id;
                string name = pid == 0 ? "Idle" : pid == 4 ? "System" : p.ProcessName + ".exe";
                long create = 0, kernel = 0, user = 0, rd = 0, wr = 0, ot = 0;
                bool counters = true;
                try { create = p.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch { }
                try { kernel = p.PrivilegedProcessorTime.Ticks; user = p.UserProcessorTime.Ticks; } catch { counters = false; }
                if (counters)
                {
                    using var h = ProcessNative.Open(pid, NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, out _);
                    if (!h.IsInvalid && NativeMethods.GetProcessIoCounters(h, out var io))
                    { rd = (long)io.ReadTransferCount; wr = (long)io.WriteTransferCount; ot = (long)io.OtherTransferCount; }
                    else counters = false;
                }
                int threads = 0; try { threads = p.Threads.Count; } catch { }
                list.Add(new RawProcessInfo(pid, 0, name, create, SafeGet(() => p.SessionId), threads, SafeGet(() => p.HandleCount),
                    SafeGet(() => p.BasePriority), SafeGet(() => p.WorkingSet64), SafeGet(() => p.PrivateMemorySize64),
                    SafeGet(() => p.PeakWorkingSet64), kernel, user, rd, wr, ot, counters));
            }
            catch { /* processo terminou durante a leitura */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    private static T SafeGet<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch { return default; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _enumerator?.Dispose();
        }
    }
}

/// <summary>Cálculo puro de taxas entre duas amostras (testável sem o sistema).</summary>
public static class ProcessRateCalculator
{
    /// <param name="current">Amostra atual.</param>
    /// <param name="previous">Amostra anterior indexada por <see cref="ProcessKey"/> (vazia na primeira).</param>
    /// <param name="interval">Tempo decorrido desde a amostra anterior (null = primeira amostra).</param>
    /// <param name="previousUtcFileTime">Hora (FILETIME UTC) da amostra anterior, para processos criados entre amostras.</param>
    /// <param name="nowUtcFileTime">Hora (FILETIME UTC) da amostra atual.</param>
    /// <param name="logicalProcessors">Nº de processadores lógicos (normalização da CPU%).</param>
    /// <param name="pathOf">Resolução opcional de caminho da imagem.</param>
    public static IReadOnlyList<ProcessEntry> Compute(
        IReadOnlyList<RawProcessInfo> current,
        IReadOnlyDictionary<ProcessKey, RawProcessInfo> previous,
        TimeSpan? interval, long previousUtcFileTime, long nowUtcFileTime, int logicalProcessors,
        Func<RawProcessInfo, string?>? pathOf = null)
    {
        int lp = Math.Max(1, logicalProcessors);
        var result = new ProcessEntry[current.Count];
        for (int i = 0; i < current.Count; i++)
        {
            var c = current[i];
            double? cpu = null, rd = null, wr = null, ot = null;
            if (interval is { } iv && iv.Ticks > 0 && c.CountersAvailable)
            {
                long elapsedTicks = 0;
                long baseCpu = 0, baseRd = 0, baseWr = 0, baseOt = 0;
                if (previous.TryGetValue(c.Key, out var p) && p.CountersAvailable)
                {
                    elapsedTicks = iv.Ticks;
                    baseCpu = p.KernelTime100ns + p.UserTime100ns;
                    baseRd = p.ReadTransferBytes; baseWr = p.WriteTransferBytes; baseOt = p.OtherTransferBytes;
                }
                else if (c.CreateTime > previousUtcFileTime && previousUtcFileTime > 0 && nowUtcFileTime > c.CreateTime)
                {
                    // Processo novo desde a última amostra: taxa média desde a criação (dado real, contadores começam em 0).
                    elapsedTicks = nowUtcFileTime - c.CreateTime;
                }
                if (elapsedTicks > 0)
                {
                    long dCpu = Math.Max(0, c.KernelTime100ns + c.UserTime100ns - baseCpu);
                    cpu = Math.Clamp(dCpu * 100.0 / ((double)elapsedTicks * lp), 0, 100);
                    double secs = elapsedTicks / (double)TimeSpan.TicksPerSecond;
                    rd = Math.Max(0, c.ReadTransferBytes - baseRd) / secs;
                    wr = Math.Max(0, c.WriteTransferBytes - baseWr) / secs;
                    ot = Math.Max(0, c.OtherTransferBytes - baseOt) / secs;
                }
            }
            result[i] = new ProcessEntry(c.Pid, c.ParentPid, c.Name, c.CreateTime, c.SessionId, c.Threads, c.Handles, c.BasePriority,
                c.WorkingSetBytes, c.PrivateBytes, c.PeakWorkingSetBytes,
                TimeSpan.FromTicks(c.KernelTime100ns), TimeSpan.FromTicks(c.UserTime100ns),
                c.ReadTransferBytes, c.WriteTransferBytes, c.OtherTransferBytes,
                cpu, rd, wr, ot, pathOf?.Invoke(c));
        }
        return result;
    }
}
