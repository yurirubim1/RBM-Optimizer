using Rbm.Benchmarks;
using Rbm.Benchmarks.Framework;
using Rbm.Benchmarks.Responsiveness;
using Xunit;

namespace Rbm.Tests.Benchmarks;

/// <summary>Benchmark falso e determinístico para testar o executor.</summary>
internal sealed class FakeBenchmark(Func<int, double> valueForIteration, Action<int>? onIteration = null, bool fail = false) : IterativeBenchmark
{
    private readonly Func<int, double> _value = valueForIteration;
    private readonly Action<int>? _onIteration = onIteration;
    private readonly bool _fail = fail;
    public int SessionsDisposed;
    public int MeasuredCalls;
    public int? MeasureThreadId;
    public override string Id => "test.fake";
    public override string Name => "Falso";
    public override BenchmarkCategory Category => BenchmarkCategory.Cpu;
    public override string Unit => BenchmarkUnits.MegaOpsPerSecond;
    public override bool HigherIsBetter => true;
    public override string Methodology => "Teste.";
    public override IReadOnlyList<MetricDefinition> SecondaryMetrics => new[] { new MetricDefinition("double", "Dobro", "x", true) };

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(this);

    private sealed class Session(FakeBenchmark b) : BenchmarkSession
    {
        private int _i;
        public override void Prepare(SessionContext ctx) { Parameters["p"] = "1"; if (b._fail) throw new BenchmarkFailureException("falha simulada"); }
        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            b.MeasureThreadId = Environment.CurrentManagedThreadId;
            if (warmup) return new IterationMeasurement(-1);
            int i = _i++;
            Interlocked.Increment(ref b.MeasuredCalls);
            b._onIteration?.Invoke(i);
            ctx.Ct.ThrowIfCancellationRequested();
            double v = b._value(i);
            return new IterationMeasurement(v, new Dictionary<string, double> { ["double"] = 2 * v });
        }
        public override void Dispose() => Interlocked.Increment(ref b.SessionsDisposed);
    }
}

/// <summary>IProgress síncrono (Progress&lt;T&gt; posta de forma assíncrona).</summary>
internal sealed class SyncProgress(Action<BenchmarkProgress> a) : IProgress<BenchmarkProgress>
{
    public void Report(BenchmarkProgress value) => a(value);
}

public class BenchmarkRunnerTests
{
    private static readonly BenchmarkOptions Fast = new() { CaptureConditions = false, WarmupIterations = 1, MeasuredIterations = 4 };

    [Fact]
    public async Task Completed_ProducesSamplesSummaryAndMetrics_OffCallingThread()
    {
        var b = new FakeBenchmark(_ => 100);
        var r = await b.RunAsync(Fast, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, r.Status);
        Assert.Equal(new double[] { 100, 100, 100, 100 }, r.Samples);
        Assert.Equal(4, r.Summary.Count);
        Assert.Equal(100, r.Summary.Mean);
        Assert.Equal(4, r.IterationWallSeconds.Count);
        Assert.Equal(200, r.GetMetric("double")!.Summary.Mean);
        Assert.False(r.UnstableConditions);
        Assert.Equal("1", r.Parameters["p"]);
        Assert.Equal(1, b.SessionsDisposed);
        Assert.NotEqual(Environment.CurrentManagedThreadId, b.MeasureThreadId);
    }

    [Fact]
    public async Task HighCoefficientOfVariation_IsFlaggedUnstable()
    {
        var b = new FakeBenchmark(i => i % 2 == 0 ? 100 : 130);
        var r = await b.RunAsync(Fast, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, r.Status);
        Assert.True(r.UnstableConditions);
        Assert.Contains(r.Warnings, w => w.Contains("instáveis"));
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledWithoutPartialScore_AndDisposesSession()
    {
        using var cts = new CancellationTokenSource();
        var b = new FakeBenchmark(_ => 100, i => { if (i == 1) cts.Cancel(); });
        var r = await b.RunAsync(Fast, null, cts.Token);
        Assert.Equal(BenchmarkStatus.Cancelled, r.Status);
        Assert.True(r.Cancelled);
        Assert.Empty(r.Samples);
        Assert.Empty(r.Metrics);
        Assert.Equal(0, r.Summary.Count);
        Assert.Equal(1, b.SessionsDisposed);
        Assert.Equal(2, b.MeasuredCalls);
    }

    [Fact]
    public async Task Failure_IsReportedAsFailedResult_NotException()
    {
        var b = new FakeBenchmark(_ => 1, fail: true);
        var r = await b.RunAsync(Fast, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Failed, r.Status);
        Assert.Equal("falha simulada", r.Message);
        Assert.Empty(r.Samples);
        Assert.Equal(1, b.SessionsDisposed);
    }

    [Fact]
    public async Task InvalidOptions_Fail()
    {
        var r = await new FakeBenchmark(_ => 1).RunAsync(Fast with { MeasuredIterations = 0 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Failed, r.Status);
    }

    [Fact]
    public async Task Progress_ReachesOneAndCoversPhases()
    {
        var phases = new List<BenchmarkPhase>();
        double last = 0;
        var p = new SyncProgress(x => { lock (phases) { phases.Add(x.Phase); last = x.Fraction; } });
        await new FakeBenchmark(_ => 1).RunAsync(Fast, p, TestContext.Current.CancellationToken);
        Assert.Contains(BenchmarkPhase.Warmup, phases);
        Assert.Contains(BenchmarkPhase.Measuring, phases);
        Assert.Equal(1.0, last);
    }

    [Fact]
    public void Conditions_CaptureReadsSystemState()
    {
        var c = ConditionsProbe.Capture(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.NotNull(c.BackgroundCpuPercent);
        Assert.InRange(c.BackgroundCpuPercent!.Value, 0, 100);
        Assert.True(c.WindowsBuild > 0);
        Assert.Equal(Environment.ProcessorCount, c.LogicalProcessors);
        Assert.NotNull(c.PowerPlanGuid);
        Assert.NotNull(c.TimerResolutionCurrentMs);
    }

    [Fact]
    public void Conditions_CaptureIsCancellable()
    {
        using var cts = new CancellationTokenSource(50);
        Assert.ThrowsAny<OperationCanceledException>(() => ConditionsProbe.Capture(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task Catalog_HasUniqueIds_AndGpuIsHonestlyNotImplemented()
    {
        var all = BenchmarkCatalog.CreateAll();
        Assert.Equal(all.Count, all.Select(b => b.Id).Distinct().Count());
        Assert.Equal(20, all.Count);
        Assert.All(all, b => Assert.False(string.IsNullOrWhiteSpace(b.Methodology)));
        var gpu = BenchmarkCatalog.Get("gpu.external")!;
        Assert.False(gpu.IsImplemented);
        var r = await gpu.RunAsync(new BenchmarkOptions(), null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.NotImplemented, r.Status);
        Assert.Empty(r.Samples);
    }

    [Fact]
    public async Task CpuBenchmark_EndToEnd_TinyScale()
    {
        var b = BenchmarkCatalog.Get("cpu.integer.mt")!;
        var r = await b.RunAsync(new BenchmarkOptions { CaptureConditions = false, WarmupIterations = 0, MeasuredIterations = 2, WorkScale = 0.001, ThreadCount = 2 },
            null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, r.Status);
        Assert.All(r.Samples, s => Assert.True(s > 0));
        Assert.Equal("2", r.Parameters["threads"]);
        Assert.True(r.Info.ContainsKey("referenceChecksum"));
    }

    [Fact]
    public async Task MemoryLatency_EndToEnd_TinyBuffer()
    {
        var r = await BenchmarkCatalog.Get("mem.latency.cache")!.RunAsync(
            new BenchmarkOptions { CaptureConditions = false, WarmupIterations = 0, MeasuredIterations = 2, WorkScale = 0.01, CacheLatencyBufferBytes = 64 * 1024 },
            null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, r.Status);
        Assert.All(r.Samples, s => Assert.InRange(s, 0.01, 1000));
    }

    [Fact]
    public async Task Jitter_And_Sleep_RunAtTinyScale()
    {
        var o = new BenchmarkOptions { CaptureConditions = false, WarmupIterations = 0, MeasuredIterations = 2, JitterDurationPerIteration = TimeSpan.FromMilliseconds(50), SleepSamplesPerIteration = 3 };
        var j = await new JitterBenchmark().RunAsync(o, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, j.Status);
        Assert.NotNull(j.GetMetric("jitter.lostPercent"));
        var s = await new SleepPrecisionBenchmark().RunAsync(o, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Completed, s.Status);
        Assert.All(s.Samples, v => Assert.True(v >= 300, $"Mediana de Sleep(1) implausível: {v} µs"));
    }
}
