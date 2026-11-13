using System.Globalization;
using Rbm.Benchmarks.Framework;

namespace Rbm.Benchmarks.Cpu;

/// <summary>
/// Benchmark de CPU single-thread (ST) ou multi-thread (MT) para uma carga determinística.
/// IDs: cpu.{integer|float|compression|sort|sha256}.{st|mt}.
/// </summary>
public sealed class CpuBenchmark : IterativeBenchmark
{
    private readonly Func<CpuWorkload> _factory;
    private readonly CpuWorkload _proto;

    internal CpuBenchmark(Func<CpuWorkload> factory, bool multiThread)
    {
        _factory = factory;
        _proto = factory();
        MultiThread = multiThread;
    }

    public bool MultiThread { get; }
    public string WorkloadKey => _proto.Key;
    public override string Id => $"cpu.{_proto.Key}.{(MultiThread ? "mt" : "st")}";
    public override string Name => $"CPU {(MultiThread ? "multi-thread" : "single-thread")} — {_proto.Name}";
    public override BenchmarkCategory Category => BenchmarkCategory.Cpu;
    public override string Unit => _proto.Unit;
    public override bool HigherIsBetter => true;
    public override string Methodology => _proto.Methodology + " " + (MultiThread
        ? "MULTI-THREAD: a mesma carga é executada em N threads dedicadas (padrão N = Environment.ProcessorCount; prioridade Normal), " +
          "cada uma com o mesmo número de unidades da versão ST (particionamento estático, dados de saída por thread). " +
          "Vazão = trabalho total / tempo de parede do início simultâneo até a última thread terminar."
        : "SINGLE-THREAD: executado em uma thread dedicada de prioridade Normal (sem afinidade fixa).") +
        " Aquecimento descartado antes das medições (compilação em camadas do JIT; métodos críticos marcados com AggressiveOptimization). " +
        "Qualquer checksum divergente invalida a execução.";

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(_factory(), MultiThread ? options.EffectiveThreads : 1, options);

    /// <summary>Todas as combinações (5 cargas × ST/MT), ST antes de MT para cada carga.</summary>
    public static IReadOnlyList<CpuBenchmark> CreateAll()
    {
        Func<CpuWorkload>[] factories =
        {
            () => new IntegerHashWorkload(),
            () => new MandelbrotWorkload(),
            () => new DeflateWorkload(),
            () => new SortWorkload(),
            () => new Sha256Workload(),
        };
        var list = new List<CpuBenchmark>();
        foreach (var f in factories) { list.Add(new CpuBenchmark(f, false)); list.Add(new CpuBenchmark(f, true)); }
        return list;
    }

    private sealed class Session(CpuWorkload workload, int threads, BenchmarkOptions options) : BenchmarkSession
    {
        private WorkerGroup? _group;
        private object[] _states = Array.Empty<object>();
        private int _units;

        public override void Prepare(SessionContext ctx)
        {
            ctx.Detail("Gerando dados determinísticos e checksum de referência...");
            workload.Prepare();
            ctx.Ct.ThrowIfCancellationRequested();
            _units = options.Scale(workload.UnitsPerIteration);
            _states = new object[threads];
            for (int i = 0; i < threads; i++) _states[i] = workload.CreateThreadState();
            _group = new WorkerGroup(threads, "RBMBench CPU");

            foreach (var (k, v) in workload.Parameters) Parameters[k] = v;
            Parameters["workload"] = workload.Key;
            Parameters["threads"] = threads.ToString(CultureInfo.InvariantCulture);
            Parameters["unitsPerThread"] = _units.ToString(CultureInfo.InvariantCulture);
            Info["referenceChecksum"] = workload.ReferenceChecksum.ToString("X16", CultureInfo.InvariantCulture);
            if (workload is DeflateWorkload d)
                Info["compressionRatio"] = d.CompressionRatio.ToString("0.000", CultureInfo.InvariantCulture);
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            int mismatches = 0;
            var ct = ctx.Ct;
            ulong reference = workload.ReferenceChecksum;
            double seconds = _group!.Run(idx =>
            {
                var st = _states[idx];
                for (int u = 0; u < _units; u++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (workload.RunUnit(st) != reference) Interlocked.Increment(ref mismatches);
                }
            });
            if (mismatches > 0)
                throw new BenchmarkFailureException(
                    $"Checksum divergente em {mismatches} unidade(s) de trabalho — resultado descartado. " +
                    "Cálculos idênticos produziram resultados diferentes, o que pode indicar instabilidade de hardware " +
                    "(overclock/undervolt, memória, temperatura).");
            double amount = workload.AmountPerUnit * _units * threads;
            return new IterationMeasurement(amount / seconds);
        }

        public override void Dispose() => _group?.Dispose();
    }
}

/// <summary>
/// Escalonamento MT/ST de uma carga: <c>Ratio</c> = média MT / média ST (x);
/// <c>EfficiencyPercent</c> = Ratio / threads × 100 (100% = escalonamento linear por thread lógica;
/// com SMT, valores ~50-70% são normais porque duas threads compartilham um núcleo).
/// </summary>
public sealed record CpuScaling(
    string WorkloadKey, string Name, string Unit,
    double SingleThreadMean, double MultiThreadMean, int Threads,
    double Ratio, double EfficiencyPercent);

/// <summary>Resultado da suíte de CPU: resultados individuais + escalonamento MT/ST por carga.</summary>
public sealed record CpuSuiteResult(BenchmarkStatus Status, IReadOnlyList<BenchmarkResult> Results, IReadOnlyList<CpuScaling> Scaling);

public static class CpuSuite
{
    /// <summary>Calcula o escalonamento a partir de dois resultados concluídos da mesma carga (ST e MT).</summary>
    public static CpuScaling? ComputeScaling(BenchmarkResult st, BenchmarkResult mt)
    {
        if (st.Status != BenchmarkStatus.Completed || mt.Status != BenchmarkStatus.Completed) return null;
        if (!st.BenchmarkId.EndsWith(".st", StringComparison.Ordinal) || !mt.BenchmarkId.EndsWith(".mt", StringComparison.Ordinal)) return null;
        string key = st.BenchmarkId[4..^3];
        if (mt.BenchmarkId[4..^3] != key || !(st.Summary.Mean > 0)) return null;
        int threads = mt.Parameters.TryGetValue("threads", out var t) && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        double ratio = mt.Summary.Mean / st.Summary.Mean;
        return new CpuScaling(key, st.Name.Split(" — ").Last(), st.Unit, st.Summary.Mean, mt.Summary.Mean, threads, ratio,
            threads > 0 ? ratio / threads * 100.0 : double.NaN);
    }

    /// <summary>Executa as 10 combinações em sequência (ST então MT por carga) e calcula o escalonamento.</summary>
    public static async Task<CpuSuiteResult> RunAsync(BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>();
        var scaling = new List<CpuScaling>();
        var all = CpuBenchmark.CreateAll();
        for (int i = 0; i < all.Count; i++)
        {
            var r = await all[i].RunAsync(options, progress, ct).ConfigureAwait(false);
            results.Add(r);
            if (r.Status == BenchmarkStatus.Cancelled) return new CpuSuiteResult(BenchmarkStatus.Cancelled, results, scaling);
            if (all[i].MultiThread && results.Count >= 2 && ComputeScaling(results[^2], r) is { } s) scaling.Add(s);
        }
        var status = results.All(r => r.Status == BenchmarkStatus.Completed) ? BenchmarkStatus.Completed : BenchmarkStatus.Failed;
        return new CpuSuiteResult(status, results, scaling);
    }
}
