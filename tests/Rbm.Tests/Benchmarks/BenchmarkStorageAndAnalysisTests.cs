using Rbm.Benchmarks;
using Rbm.Benchmarks.Storage;
using Rbm.Core;
using Xunit;

namespace Rbm.Tests.Benchmarks;

public class BenchmarkStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rbm-bench-tests-" + Guid.NewGuid().ToString("N"));

    private BenchmarkOptions Tiny => new()
    {
        CaptureConditions = false,
        WarmupIterations = 0,
        MeasuredIterations = 1,
        StorageDirectory = _dir,
        StorageFileBytes = 16 * BenchmarkOptions.MiB,
        StorageFreeSpaceMarginBytes = 0,
        StorageSequentialDuration = TimeSpan.FromMilliseconds(100),
        StorageRandomDuration = TimeSpan.FromMilliseconds(100),
    };

    private int LeftoverFiles() => Directory.Exists(_dir) ? Directory.GetFiles(_dir, StorageBenchmark.TempFilePrefix + "*").Length : 0;

    [Fact]
    public async Task Storage_Completes_AndDeletesTempFile()
    {
        var r = await new StorageBenchmark().RunAsync(Tiny with { StorageIncludeWriteTests = true }, null, TestContext.Current.CancellationToken);
        Assert.True(r.Status == BenchmarkStatus.Completed, r.Message);
        Assert.True(r.Samples[0] > 0);
        Assert.NotNull(r.GetMetric("rnd4k.qd32.read.iops"));
        Assert.NotNull(r.GetMetric("rnd4k.qd1.write.lat.p99"));
        Assert.Contains(r.Warnings, w => w.Contains("gravação"));
        Assert.Equal(0, LeftoverFiles());
    }

    [Fact]
    public async Task Storage_WithoutOptIn_HasNoWriteMetrics()
    {
        var r = await new StorageBenchmark().RunAsync(Tiny, null, TestContext.Current.CancellationToken);
        Assert.True(r.Status == BenchmarkStatus.Completed, r.Message);
        Assert.Null(r.GetMetric("seq.write.mbps"));
        Assert.Equal(0, LeftoverFiles());
    }

    [Theory]
    [InlineData(BenchmarkPhase.Preparing)]
    [InlineData(BenchmarkPhase.Measuring)]
    public async Task Storage_Cancelled_LeavesNoTempFiles(BenchmarkPhase cancelAt)
    {
        using var cts = new CancellationTokenSource();
        bool sawFile = false;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == cancelAt && !cts.IsCancellationRequested)
            {
                sawFile |= LeftoverFiles() > 0 || cancelAt == BenchmarkPhase.Preparing;
                cts.Cancel();
            }
        });
        var r = await new StorageBenchmark().RunAsync(Tiny with { MeasuredIterations = 3 }, progress, cts.Token);
        Assert.Equal(BenchmarkStatus.Cancelled, r.Status);
        Assert.Empty(r.Samples);
        Assert.True(sawFile);
        Assert.Equal(0, LeftoverFiles());
    }

    [Fact]
    public async Task Storage_InsufficientFreeSpace_FailsWithoutCreatingFile()
    {
        var r = await new StorageBenchmark().RunAsync(Tiny with { StorageFreeSpaceMarginBytes = long.MaxValue / 4 }, null, TestContext.Current.CancellationToken);
        Assert.Equal(BenchmarkStatus.Failed, r.Status);
        Assert.Contains("Espaço livre insuficiente", r.Message);
        Assert.Equal(0, LeftoverFiles());
    }

    [Fact]
    public void ResolveDirectory_SystemDriveUsesTemp()
    {
        string sys = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(Path.Combine(Path.GetTempPath(), StorageBenchmark.TempFolderName), StorageBenchmark.ResolveDirectory(null));
        Assert.Equal(Path.Combine(Path.GetTempPath(), StorageBenchmark.TempFolderName), StorageBenchmark.ResolveDirectory(sys));
        Assert.Equal(@"Q:\" + StorageBenchmark.TempFolderName, StorageBenchmark.ResolveDirectory("Q:"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

public class BenchmarkAnalysisTests
{
    private static BenchmarkResult Make(double[] samples, bool higherIsBetter = true, string id = "cpu.integer.st",
        Guid? plan = null, Dictionary<string, string>? parameters = null, string cpu = "CPU X")
        => new()
        {
            BenchmarkId = id,
            Name = "teste",
            Status = BenchmarkStatus.Completed,
            Unit = BenchmarkUnits.MegaOpsPerSecond,
            HigherIsBetter = higherIsBetter,
            Samples = samples,
            Summary = Stats.Summarize(samples),
            Parameters = parameters ?? new() { ["threads"] = "1" },
            Conditions = new TestConditions
            {
                PowerPlanGuid = plan ?? Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"),
                PowerPlanName = "Equilibrado",
                OnAcPower = true,
                WindowsBuild = 26200,
                WindowsVersion = "10.0.26200",
                CpuName = cpu,
                LogicalProcessors = 12,
                RuntimeVersion = ".NET 10",
            },
            Metrics = new[] { new BenchmarkMetric { Key = "m", Name = "m", Unit = "µs", HigherIsBetter = false, Samples = samples, Summary = Stats.Summarize(samples) } },
        };

    [Fact]
    public void SmallFluctuation_IsNotCalledAGain()
    {
        var c = BenchmarkComparer.Compare(Make(new[] { 100.0, 101, 99, 100, 100.5 }), Make(new[] { 100.8, 101.2, 100.1, 100.6, 101 }));
        Assert.True(c.Comparable);
        Assert.False(c.Primary!.Significant);
        Assert.False(c.ConfidentChange);
        Assert.Contains("Sem diferença", c.Verdict);
    }

    [Fact]
    public void ClearChange_RespectsDirection()
    {
        var before = new[] { 100.0, 101, 99, 100, 100.5 };
        var after = new[] { 110.0, 111, 109, 110, 110.5 };
        var up = BenchmarkComparer.Compare(Make(before), Make(after));
        Assert.True(up.ConfidentChange);
        Assert.Contains("Melhora", up.Verdict);
        var lat = BenchmarkComparer.Compare(Make(before, higherIsBetter: false), Make(after, higherIsBetter: false));
        Assert.Contains("Piora", lat.Verdict);
        Assert.Single(lat.Metrics);
    }

    [Fact]
    public void ChangedPowerPlan_ProducesWarning_AndNoConfidentChange()
    {
        var c = BenchmarkComparer.Compare(Make(new[] { 100.0, 101, 99, 100 }), Make(new[] { 120.0, 121, 119, 120 }, plan: Guid.NewGuid()));
        Assert.True(c.Comparable);
        Assert.True(c.Primary!.Significant);
        Assert.False(c.ConfidentChange);
        Assert.Contains(c.Warnings, w => w.Contains("Plano de energia"));
    }

    [Fact]
    public void DifferentWorkloadOrMachineOrId_IsNotComparable()
    {
        var s = new[] { 1.0, 2, 3 };
        Assert.False(BenchmarkComparer.Compare(Make(s), Make(s, parameters: new() { ["threads"] = "12" })).Comparable);
        Assert.False(BenchmarkComparer.Compare(Make(s), Make(s, cpu: "Outra CPU")).Comparable);
        Assert.False(BenchmarkComparer.Compare(Make(s), Make(s, id: "cpu.sort.st")).Comparable);
        Assert.False(BenchmarkComparer.Compare(Make(s), Make(s) with { Status = BenchmarkStatus.Cancelled }).Comparable);
    }

    [Fact]
    public void Index_IsGeometricMeanOfOrientedRatios()
    {
        var baseline = new[] { Make(new[] { 100.0, 100 }, id: "a"), Make(new[] { 10.0, 10 }, higherIsBetter: false, id: "b") };
        var current = new[] { Make(new[] { 121.0, 121 }, id: "a"), Make(new[] { 10.0, 10 }, higherIsBetter: false, id: "b") };
        var idx = BenchmarkIndex.Compute(baseline, current);
        Assert.Equal(2, idx.Components.Count);
        Assert.Equal(1.1, idx.Index, 6); // sqrt(1.21 × 1.0)
    }

    [Fact]
    public void Json_RoundTrips_IncludingNaNAndConditions()
    {
        var completed = Make(new[] { 1.5, 2.5, 3.5 }) with { Info = new Dictionary<string, string> { ["x"] = "y" }, Warnings = new[] { "aviso" } };
        var cancelled = completed with { Status = BenchmarkStatus.Cancelled, Samples = Array.Empty<double>(), Summary = StatSummary.Empty, Metrics = Array.Empty<BenchmarkMetric>() };
        foreach (var r in new[] { completed, cancelled })
        {
            string json = BenchmarkJson.Serialize(r);
            var back = BenchmarkJson.Deserialize(json)!;
            Assert.Equal(json, BenchmarkJson.Serialize(back));
            Assert.Equal(r.Status, back.Status);
            Assert.Equal(r.Samples, back.Samples);
            Assert.NotNull(back.Conditions);
            Assert.Equal(r.Conditions, back.Conditions with { HighCpuProcesses = r.Conditions!.HighCpuProcesses });
        }
        Assert.Contains("\"Cancelled\"", BenchmarkJson.Serialize(cancelled));
        Assert.Contains("NaN", BenchmarkJson.Serialize(cancelled));
    }
}
