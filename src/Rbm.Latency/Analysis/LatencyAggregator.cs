namespace Rbm.Latency;

/// <summary>Uma execução de DPC/ISR já convertida para unidades do relatório.</summary>
/// <param name="OffsetMs">Instante do FIM da execução em ms desde o início da captura (o evento ETW é emitido ao término).</param>
/// <param name="DurationUs">Duração = timestamp do evento − InitialTime (µs).</param>
/// <param name="Routine">Endereço da rotina DPC/ISR (kernel).</param>
public readonly record struct KernelLatencySample(LatencyEventKind Kind, double OffsetMs, double DurationUs, int Cpu, ulong Routine);

/// <summary>Metadados da captura usados para montar o relatório.</summary>
public sealed record LatencyReportContext(
    DateTimeOffset StartedAt,
    int RequestedSeconds,
    double DurationSeconds,
    string SessionName,
    int LogicalProcessors,
    long EventsLost = 0,
    IReadOnlyDictionary<int, string>? ProcessNames = null,
    LatencyThresholds? Thresholds = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Agregador puro (sem ETW) de execuções DPC/ISR: estatísticas gerais, por módulo, por CPU, linha do tempo por segundo
/// e top N execuções. Memória constante por rotina distinta (histograma fino), independente da duração da captura.
/// Não é thread-safe: alimente a partir de uma única thread (a thread de processamento ETW).
/// </summary>
public sealed class LatencyAggregator
{
    public const string UnknownModule = "(desconhecido)";
    /// <summary>Durações acima disto (1 s) são tratadas como amostras inválidas (relógio inconsistente).</summary>
    public const double MaxPlausibleUs = 1_000_000;
    /// <summary>Limite da linha do tempo (1 h) para proteger contra timestamps espúrios.</summary>
    public const int MaxTimelineSeconds = 3600;

    private readonly int _topN;
    private readonly Dictionary<(LatencyEventKind, ulong), DurationDistribution> _byRoutine = new();
    private readonly DurationDistribution _dpc = new();
    private readonly DurationDistribution _isr = new();
    private readonly Dictionary<int, CpuAcc> _cpus = new();
    private readonly List<SecondAcc> _timeline = new();
    private readonly PriorityQueue<KernelLatencySample, double> _top = new();
    private readonly Dictionary<int, (long Count, double TotalMs)> _hardFaults = new();

    public LatencyAggregator(int topN = 50)
    {
        _topN = Math.Clamp(topN, 1, 500);
    }

    public long DpcCount => _dpc.Count;
    public long IsrCount => _isr.Count;
    public long InvalidSamples { get; private set; }

    public void Add(in KernelLatencySample s)
    {
        if (!double.IsFinite(s.DurationUs) || s.DurationUs < 0 || s.DurationUs > MaxPlausibleUs || !double.IsFinite(s.OffsetMs))
        {
            InvalidSamples++;
            return;
        }
        var key = (s.Kind, s.Routine);
        if (!_byRoutine.TryGetValue(key, out var dist)) _byRoutine[key] = dist = new DurationDistribution();
        dist.Add(s.DurationUs);

        bool isDpc = s.Kind == LatencyEventKind.Dpc;
        (isDpc ? _dpc : _isr).Add(s.DurationUs);

        int cpu = Math.Max(0, s.Cpu);
        if (!_cpus.TryGetValue(cpu, out var c)) _cpus[cpu] = c = new CpuAcc();
        c.Add(isDpc, s.DurationUs);

        int sec = (int)Math.Clamp(Math.Floor(Math.Max(0, s.OffsetMs) / 1000.0), 0, MaxTimelineSeconds - 1);
        while (_timeline.Count <= sec) _timeline.Add(new SecondAcc());
        _timeline[sec].Add(isDpc, s.DurationUs);

        if (_top.Count < _topN) _top.Enqueue(s, s.DurationUs);
        else if (_top.TryPeek(out _, out var minDur) && s.DurationUs > minDur) _top.EnqueueDequeue(s, s.DurationUs);
    }

    /// <summary>Registra um page fault "hard" (opcional).</summary>
    public void AddHardFault(int processId, double elapsedMs)
    {
        _hardFaults.TryGetValue(processId, out var v);
        _hardFaults[processId] = (v.Count + 1, v.TotalMs + (double.IsFinite(elapsedMs) && elapsedMs > 0 ? elapsedMs : 0));
    }

    /// <summary>Monta o relatório final resolvendo endereços de rotina para módulos.</summary>
    public LatencyReport Build(KernelModuleMap modules, LatencyReportContext ctx)
    {
        var thresholds = ctx.Thresholds ?? LatencyThresholds.Default;
        int cpus = Math.Max(1, ctx.LogicalProcessors);
        double captureUs = Math.Max(1e-9, ctx.DurationSeconds) * 1e6;

        // --- por módulo ---
        var perModule = new Dictionary<string, ModuleAcc>(StringComparer.OrdinalIgnoreCase);
        var routineModule = new Dictionary<ulong, string>();
        double unresolvedUs = 0, totalUs = 0;
        foreach (var ((kind, routine), dist) in _byRoutine)
        {
            var range = modules.Resolve(routine);
            string name = range?.FileName ?? UnknownModule;
            routineModule[routine] = name;
            if (!perModule.TryGetValue(name, out var acc)) perModule[name] = acc = new ModuleAcc(range?.ImagePath);
            (kind == LatencyEventKind.Dpc ? acc.Dpc : acc.Isr).Merge(dist);
            totalUs += dist.TotalUs;
            if (range is null) unresolvedUs += dist.TotalUs;
        }
        var moduleStats = perModule
            .Select(kv => new ModuleLatencyStats(
                kv.Key,
                kv.Value.ImagePath,
                kv.Key == UnknownModule ? "" : DriverHints.Categorize(kv.Key),
                kv.Value.Dpc.ToStats(),
                kv.Value.Isr.ToStats(),
                Math.Max(kv.Value.Dpc.MaxUs, kv.Value.Isr.MaxUs),
                (kv.Value.Dpc.TotalUs + kv.Value.Isr.TotalUs) / (captureUs * cpus) * 100.0))
            .OrderByDescending(m => m.HighestUs)
            .ThenByDescending(m => m.Dpc.TotalUs + m.Isr.TotalUs)
            .ToList();

        // --- por CPU ---
        var cpuStats = _cpus.OrderBy(k => k.Key)
            .Select(k => new CpuLatencyStats(k.Key, k.Value.DpcCount, k.Value.DpcTotal, k.Value.DpcMax, k.Value.IsrCount, k.Value.IsrTotal, k.Value.IsrMax,
                (k.Value.DpcTotal + k.Value.IsrTotal) / captureUs * 100.0))
            .ToList();

        // --- linha do tempo (inclui segundos sem eventos até a duração da captura) ---
        int seconds = Math.Max(_timeline.Count, (int)Math.Ceiling(ctx.DurationSeconds));
        seconds = Math.Min(seconds, MaxTimelineSeconds);
        var timeline = new List<LatencyTimelinePoint>(seconds);
        for (int i = 0; i < seconds; i++)
        {
            var a = i < _timeline.Count ? _timeline[i] : null;
            timeline.Add(a is null
                ? new LatencyTimelinePoint(i, 0, 0, 0, 0, 0, 0)
                : new LatencyTimelinePoint(i, a.DpcMax, a.IsrMax, a.DpcCount, a.IsrCount, a.DpcTotal, a.IsrTotal));
        }

        // --- top N ---
        var top = _top.UnorderedItems
            .Select(x => x.Element)
            .OrderByDescending(s => s.DurationUs)
            .Select(s => new LatencyExecution(s.Kind, routineModule.GetValueOrDefault(s.Routine, UnknownModule), s.Routine, s.DurationUs, s.OffsetMs,
                ctx.StartedAt.AddMilliseconds(s.OffsetMs), s.Cpu))
            .ToList();

        // --- hard faults ---
        var hardFaults = _hardFaults
            .Select(kv => new HardFaultProcessStats(kv.Key,
                ctx.ProcessNames is not null && ctx.ProcessNames.TryGetValue(kv.Key, out var n) ? n : $"PID {kv.Key}",
                kv.Value.Count, kv.Value.TotalMs))
            .OrderByDescending(h => h.Count)
            .Take(30)
            .ToList();

        var dpcStats = _dpc.ToStats();
        var isrStats = _isr.ToStats();
        double unresolvedPct = totalUs > 0 ? unresolvedUs / totalUs * 100.0 : 0;

        var warnings = new List<string>(ctx.Warnings ?? Array.Empty<string>());
        if (ctx.EventsLost > 0)
            warnings.Add($"{ctx.EventsLost} evento(s) ETW perdido(s) (buffer cheio). Estatísticas podem estar subestimadas; os máximos observados continuam válidos como limite inferior.");
        if (InvalidSamples > 0)
            warnings.Add($"{InvalidSamples} amostra(s) descartada(s) por duração inválida (negativa ou > 1 s).");
        if (unresolvedPct > 5)
            warnings.Add($"{unresolvedPct:0.#}% do tempo DPC/ISR não pôde ser atribuído a um módulo (rundown de imagens incompleto).");

        var assessment = LatencyAssessment.Assess(dpcStats, isrStats, timeline, moduleStats, cpuStats, thresholds, ctx.EventsLost, ctx.DurationSeconds);

        return new LatencyReport(
            ctx.StartedAt, ctx.RequestedSeconds, ctx.DurationSeconds, ctx.SessionName, cpus,
            dpcStats, isrStats, moduleStats, cpuStats, timeline, top, hardFaults,
            assessment.Verdict, assessment.Text, assessment.Findings,
            ctx.EventsLost, InvalidSamples, unresolvedPct, warnings, LatencyTexts.CaptureLimitations, thresholds);
    }

    private sealed class ModuleAcc(string? imagePath)
    {
        public string? ImagePath { get; } = imagePath;
        public DurationDistribution Dpc { get; } = new();
        public DurationDistribution Isr { get; } = new();
    }

    private sealed class CpuAcc
    {
        public long DpcCount, IsrCount;
        public double DpcTotal, IsrTotal, DpcMax, IsrMax;
        public void Add(bool dpc, double us)
        {
            if (dpc) { DpcCount++; DpcTotal += us; if (us > DpcMax) DpcMax = us; }
            else { IsrCount++; IsrTotal += us; if (us > IsrMax) IsrMax = us; }
        }
    }

    private sealed class SecondAcc
    {
        public int DpcCount, IsrCount;
        public double DpcTotal, IsrTotal, DpcMax, IsrMax;
        public void Add(bool dpc, double us)
        {
            if (dpc) { DpcCount++; DpcTotal += us; if (us > DpcMax) DpcMax = us; }
            else { IsrCount++; IsrTotal += us; if (us > IsrMax) IsrMax = us; }
        }
    }
}
