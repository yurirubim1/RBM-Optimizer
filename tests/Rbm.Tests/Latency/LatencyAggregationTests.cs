using System.Text.Json;
using Rbm.Core.Agent;
using Rbm.Latency;
using Xunit;

namespace Rbm.Tests.Latency;

public class LatencyHistogramTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(49.9, 0)]
    [InlineData(50, 1)]
    [InlineData(99.99, 1)]
    [InlineData(100, 2)]
    [InlineData(250, 3)]
    [InlineData(499.9, 3)]
    [InlineData(500, 4)]
    [InlineData(1000, 5)]
    [InlineData(1999, 5)]
    [InlineData(2000, 6)]
    [InlineData(50000, 6)]
    public void BucketIndex_UsesInclusiveLowerExclusiveUpper(double us, int expected) =>
        Assert.Equal(expected, LatencyHistogram.BucketIndex(us));

    [Fact]
    public void FromSamples_CountsEachBucket()
    {
        var h = LatencyHistogram.FromSamples(new[] { 10.0, 20, 60, 120, 300, 600, 1500, 2500, 9000 });
        Assert.Equal(7, h.Count);
        Assert.Equal(new long[] { 2, 1, 1, 1, 1, 1, 2 }, h.Select(b => b.Count).ToArray());
        Assert.Equal(2000, h[6].LowerUs);
        Assert.Null(h[6].UpperUs);
        Assert.Equal(">2000 µs", h[6].Label);
    }
}

public class DurationDistributionTests
{
    [Fact]
    public void ExactAggregates_AndPercentilesWithinResolution()
    {
        var d = new DurationDistribution();
        for (int i = 1; i <= 1000; i++) d.Add(i - 0.5); // 0.5 .. 999.5
        Assert.Equal(1000, d.Count);
        Assert.Equal(0.5, d.MinUs);
        Assert.Equal(999.5, d.MaxUs);
        Assert.Equal(500, d.Mean, 6);
        Assert.InRange(d.Percentile(50), 499, 501);
        Assert.InRange(d.Percentile(99), 989, 991);
        Assert.Equal(0.5, d.Percentile(0));
        Assert.Equal(999.5, d.Percentile(100));
    }

    [Fact]
    public void PercentileAboveOneMs_HasTenMicrosecondResolution()
    {
        var d = new DurationDistribution();
        for (int i = 0; i < 99; i++) d.Add(10);
        d.Add(1234);
        Assert.InRange(d.Percentile(99), 9, 11);
        Assert.InRange(d.Percentile(99.9), 1230, 1240);
        Assert.Equal(1234, d.MaxUs);
    }

    [Fact]
    public void Merge_CombinesCountsAndExtremes()
    {
        var a = new DurationDistribution(); a.Add(10); a.Add(20);
        var b = new DurationDistribution(); b.Add(5); b.Add(3000);
        a.Merge(b);
        Assert.Equal(4, a.Count);
        Assert.Equal(3035, a.TotalUs);
        Assert.Equal(5, a.MinUs);
        Assert.Equal(3000, a.MaxUs);
        Assert.Equal(new long[] { 3, 0, 0, 0, 0, 0, 1 }, a.CoarseCounts.ToArray());
    }

    [Fact]
    public void Empty_ProducesEmptyStats()
    {
        var s = new DurationDistribution().ToStats();
        Assert.Equal(0, s.Count);
        Assert.Equal(7, s.Histogram.Count);
        Assert.Equal(0, s.P99Us);
    }

    [Fact]
    public void Add_RejectsNegative() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DurationDistribution().Add(-1));
}

public class KernelModuleMapTests
{
    private const ulong NdisBase = 0xFFFFF80010000000;
    private const ulong GpuBase = 0xFFFFF80020000000;

    [Fact]
    public void Resolve_FindsContainingModule_AndRespectsBounds()
    {
        var map = new KernelModuleMap();
        map.Add(NdisBase, 0x100000, @"\SystemRoot\System32\drivers\ndis.sys");
        map.Add(GpuBase, 0x2000000, @"C:\Windows\System32\DriverStore\FileRepository\u0123.inf_amd64\B123\amdkmdag.sys");

        Assert.Equal("ndis.sys", map.Resolve(NdisBase)!.FileName);
        Assert.Equal("ndis.sys", map.Resolve(NdisBase + 0xFFFFF)!.FileName);
        Assert.Null(map.Resolve(NdisBase + 0x100000)); // fim exclusivo
        Assert.Null(map.Resolve(NdisBase - 1));
        Assert.Equal("amdkmdag.sys", map.Resolve(GpuBase + 0x1234)!.FileName);
    }

    [Fact]
    public void ExactRange_WinsOverApproximate()
    {
        var map = new KernelModuleMap();
        map.AddApproximateFromBases(new[] { (NdisBase, "ndis.sys"), (GpuBase, "amdkmdag.sys") });
        // aproximada: ndis vai até GpuBase; exata termina antes
        Assert.True(map.Resolve(NdisBase + 0x500000)!.Approximate);
        map.Add(NdisBase, 0x100000, @"\SystemRoot\System32\drivers\ndis.sys");
        var r = map.Resolve(NdisBase + 0x10)!;
        Assert.False(r.Approximate);
        Assert.Null(map.Resolve(NdisBase + 0x500000)); // exata substituiu a aproximada de mesma base
        // aproximada não sobrescreve exata
        map.Add(NdisBase, 0x900000, "ndis.sys", approximate: true);
        Assert.False(map.Resolve(NdisBase + 0x10)!.Approximate);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\tcpip.sys", "tcpip.sys")]
    [InlineData(@"\??\C:\Windows\system32\drivers\HDAudBus.sys", "HDAudBus.sys")]
    [InlineData("ACPI.sys", "ACPI.sys")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FileNameOf_HandlesKernelPaths(string? path, string expected) =>
        Assert.Equal(expected, KernelModuleMap.FileNameOf(path));

    [Fact]
    public void IsKernelAddress_UpperCanonicalHalfOnly()
    {
        Assert.True(KernelModuleMap.IsKernelAddress(0xFFFFF80000000000));
        Assert.False(KernelModuleMap.IsKernelAddress(0x00007FF600000000));
        Assert.False(KernelModuleMap.IsKernelAddress(0));
    }
}

public class LatencyAggregatorTests
{
    private const ulong NdisBase = 0xFFFFF80010000000;
    private const ulong GpuBase = 0xFFFFF80020000000;
    private const ulong AcpiBase = 0xFFFFF80030000000;
    private const ulong NdisDpc = NdisBase + 0x1000;
    private const ulong NdisIsr = NdisBase + 0x2000;
    private const ulong GpuDpc = GpuBase + 0x5000;
    private const ulong AcpiDpc = AcpiBase + 0x10;
    private const ulong UnknownRoutine = 0xFFFFF800DEAD0000;

    private static KernelModuleMap Map()
    {
        var m = new KernelModuleMap();
        m.Add(NdisBase, 0x100000, @"\SystemRoot\System32\drivers\ndis.sys");
        m.Add(GpuBase, 0x2000000, @"\SystemRoot\System32\DriverStore\FileRepository\x\nvlddmkm.sys");
        m.Add(AcpiBase, 0x100000, @"\SystemRoot\System32\drivers\ACPI.sys");
        return m;
    }

    private static LatencyReportContext Ctx(double seconds = 10, long lost = 0) =>
        new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), (int)seconds, seconds, "RBM-Latency-Kernel", 4, lost);

    /// <summary>Carga "boa": muitos DPCs/ISRs curtos ao longo de 10 s.</summary>
    private static LatencyAggregator BaseLoad()
    {
        var a = new LatencyAggregator(topN: 5);
        for (int s = 0; s < 10; s++)
        {
            for (int i = 0; i < 100; i++)
            {
                double t = s * 1000 + i * 10;
                a.Add(new KernelLatencySample(LatencyEventKind.Dpc, t, 20 + i % 10, i % 4, NdisDpc));
                a.Add(new KernelLatencySample(LatencyEventKind.Isr, t + 1, 3, 0, NdisIsr));
                a.Add(new KernelLatencySample(LatencyEventKind.Dpc, t + 2, 40, 1, GpuDpc));
            }
        }
        return a;
    }

    [Fact]
    public void Aggregates_PerModule_SeparatingDpcAndIsr()
    {
        var r = BaseLoad().Build(Map(), Ctx());
        var ndis = r.Modules.Single(m => m.Module == "ndis.sys");
        Assert.Equal(1000, ndis.Dpc.Count);
        Assert.Equal(1000, ndis.Isr.Count);
        Assert.Equal(29, ndis.Dpc.MaxUs);
        Assert.Equal(3, ndis.Isr.MaxUs);
        Assert.Equal(24.5, ndis.Dpc.MeanUs, 6);
        Assert.Equal(DriverHints.Network, ndis.Category);

        var gpu = r.Modules.Single(m => m.Module == "nvlddmkm.sys");
        Assert.Equal(1000, gpu.Dpc.Count);
        Assert.Equal(0, gpu.Isr.Count);
        Assert.Equal(DriverHints.Graphics, gpu.Category);

        Assert.Equal(2000, r.DpcOverall.Count);
        Assert.Equal(1000, r.IsrOverall.Count);
        Assert.Equal(0, r.UnresolvedPercent);
        Assert.Equal(LatencyVerdict.Good, r.Verdict);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void PerCpu_And_Timeline_AreComputed()
    {
        var a = BaseLoad();
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 3500, 800, 2, GpuDpc));
        var r = a.Build(Map(), Ctx());

        Assert.Equal(10, r.Timeline.Count);
        Assert.Equal(800, r.Timeline[3].MaxDpcUs);
        Assert.Equal(40, r.Timeline[2].MaxDpcUs);
        Assert.Equal(3, r.Timeline[5].MaxIsrUs);
        Assert.Equal(201, r.Timeline[3].DpcCount);

        var cpu0 = r.PerCpu.Single(c => c.Cpu == 0);
        Assert.Equal(1000, cpu0.IsrCount);   // todas as ISRs no CPU 0
        Assert.Equal(250, cpu0.DpcCount);    // i % 4 == 0
        var cpu2 = r.PerCpu.Single(c => c.Cpu == 2);
        Assert.Equal(800, cpu2.DpcMaxUs);
        // tempo em DPC+ISR / tempo de parede (10 s) × 100
        Assert.Equal((cpu0.DpcTotalUs + cpu0.IsrTotalUs) / 10e6 * 100, cpu0.PercentTime, 9);
    }

    [Fact]
    public void TopExecutions_AreLongestFirst_WithResolvedModule()
    {
        var a = BaseLoad();
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 1234.5, 1500, 3, AcpiDpc));
        a.Add(new KernelLatencySample(LatencyEventKind.Isr, 2000, 700, 0, NdisIsr));
        var r = a.Build(Map(), Ctx());
        Assert.Equal(5, r.TopExecutions.Count);
        Assert.Equal(1500, r.TopExecutions[0].DurationUs);
        Assert.Equal("ACPI.sys", r.TopExecutions[0].Module);
        Assert.Equal(3, r.TopExecutions[0].Cpu);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 1, 234, TimeSpan.Zero).AddTicks(5000), r.TopExecutions[0].Timestamp);
        Assert.Equal(LatencyEventKind.Isr, r.TopExecutions[1].Kind);
        Assert.Equal("ndis.sys", r.TopExecutions[1].Module);
        Assert.True(r.TopExecutions.Zip(r.TopExecutions.Skip(1)).All(p => p.First.DurationUs >= p.Second.DurationUs));
    }

    [Fact]
    public void InvalidSamples_AreDiscarded_AndUnknownRoutinesReported()
    {
        var a = BaseLoad();
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 100, -5, 0, NdisDpc));
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 100, double.NaN, 0, NdisDpc));
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 100, 2_000_000, 0, NdisDpc));
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 100, 30000, 0, UnknownRoutine)); // 30 ms em módulo desconhecido
        var r = a.Build(Map(), Ctx());
        Assert.Equal(3, r.InvalidSamples);
        var unk = r.Modules.Single(m => m.Module == LatencyAggregator.UnknownModule);
        Assert.Equal(1, unk.Dpc.Count);
        Assert.True(r.UnresolvedPercent > 0);
        Assert.Contains(r.Warnings, w => w.Contains("descartada"));
        Assert.Contains(r.Findings, f => f.Title.Contains("não identificado"));
    }

    [Fact]
    public void Verdict_Attention_ForIsolatedSpikeAboveOneMs()
    {
        var a = BaseLoad();
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 4200, 1300, 1, GpuDpc));
        var r = a.Build(Map(), Ctx());
        Assert.Equal(LatencyVerdict.Attention, r.Verdict);
        Assert.StartsWith("Atenção", r.VerdictText);
        var f = Assert.Single(r.Findings, x => x.RuleId == "latency.dpcisr.module");
        Assert.Contains("nvlddmkm.sys", f.Title);
        Assert.True(f.CorrelationOnly);
        Assert.Contains("Associação, não causa confirmada", f.ProbableCause);
    }

    [Fact]
    public void Verdict_Attention_ForRecurringElevatedSpikes()
    {
        var a = BaseLoad();
        foreach (var s in new[] { 1, 4, 7 })
            a.Add(new KernelLatencySample(LatencyEventKind.Dpc, s * 1000 + 500, 600, 0, NdisDpc));
        var r = a.Build(Map(), Ctx());
        Assert.Equal(LatencyVerdict.Attention, r.Verdict);
    }

    [Fact]
    public void Verdict_Problematic_ForSustainedSevereSpikes_NamesDriver()
    {
        var a = BaseLoad();
        foreach (var s in new[] { 2, 5, 8 })
            a.Add(new KernelLatencySample(LatencyEventKind.Dpc, s * 1000 + 100, 2600, 3, AcpiDpc));
        var r = a.Build(Map(), Ctx());
        Assert.Equal(LatencyVerdict.Problematic, r.Verdict);
        Assert.Contains(r.Findings, f => f.RuleId == "latency.dpcisr.overall" && f.Severity == Rbm.Core.Severity.High);
        var mod = Assert.Single(r.Findings, f => f.RuleId == "latency.dpcisr.module");
        Assert.Contains("ACPI.sys", mod.Title);
        Assert.Equal(Rbm.Core.Severity.High, mod.Severity);
        Assert.Equal(3, r.Modules.Single(m => m.Module == "ACPI.sys").Dpc.CountAtOrAbove(2000));
        Assert.Equal("ACPI.sys", r.Modules[0].Module); // ordenado pelo maior pico
    }

    [Fact]
    public void Verdict_Inconclusive_WithoutEvents()
    {
        var r = new LatencyAggregator().Build(Map(), Ctx());
        Assert.Equal(LatencyVerdict.Inconclusive, r.Verdict);
        Assert.Equal(10, r.Timeline.Count);
    }

    [Fact]
    public void EventsLost_AddsWarningAndInfoFinding()
    {
        var r = BaseLoad().Build(Map(), Ctx(lost: 42));
        Assert.Equal(42, r.EventsLost);
        Assert.Contains(r.Warnings, w => w.Contains("42"));
        Assert.Contains(r.Findings, f => f.RuleId == "latency.capture.eventsLost");
    }

    [Fact]
    public void Report_DisclaimsInputLag()
    {
        var r = BaseLoad().Build(Map(), Ctx());
        Assert.Contains(r.Limitations, l => l.Contains("NÃO é a latência de entrada") && l.Contains("LDAT"));
    }

    [Fact]
    public void Report_RoundTripsThroughAgentJson()
    {
        var a = BaseLoad();
        a.Add(new KernelLatencySample(LatencyEventKind.Dpc, 4200, 1300, 1, GpuDpc));
        a.AddHardFault(1234, 2.5);
        var r = a.Build(Map(), Ctx() with { ProcessNames = new Dictionary<int, string> { [1234] = "game.exe" } });
        var json = JsonSerializer.Serialize(r, AgentJson.Options);
        var back = JsonSerializer.Deserialize<LatencyReport>(json, AgentJson.Options)!;
        Assert.Equal(r.Verdict, back.Verdict);
        Assert.Equal(r.Modules.Count, back.Modules.Count);
        Assert.Equal(r.DpcOverall.MaxUs, back.DpcOverall.MaxUs);
        Assert.Equal(r.Findings.Count, back.Findings.Count);
        Assert.Equal(r.TopExecutions[0].Routine, back.TopExecutions[0].Routine);
        Assert.Equal("game.exe", back.HardFaults.Single().ProcessName);
        Assert.Contains("\"Attention\"", json); // enums como texto
    }
}

public class LatencyCaptureValidationTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    [InlineData(0)]
    [InlineData(-10)]
    public void Capture_RejectsOutOfRangeSeconds(int s) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LatencyAgentHandlers.ValidateCapture(new LatencyCaptureRequest(s)));

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(300)]
    public void Capture_AcceptsValidSeconds(int s) =>
        Assert.Equal(s, LatencyAgentHandlers.ValidateCapture(new LatencyCaptureRequest(s)).Seconds);

    [Fact]
    public void Capture_RejectsNullPayload() =>
        Assert.Throws<ArgumentNullException>(() => LatencyAgentHandlers.ValidateCapture(null));

    [Fact]
    public void Capture_RejectsInconsistentThresholds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DpcIsrCaptureService.Validate(new DpcIsrCaptureOptions(Thresholds: new LatencyThresholds(ElevatedUs: 2000, HighUs: 1000))));

    [Theory]
    [InlineData("RBM-Latency-Kernel", true)]
    [InlineData("Rbm-Latency-Kernel", true)]
    [InlineData("NT Kernel Logger", false)]
    [InlineData("Circular Kernel Context Logger", false)]
    [InlineData("RBM-LatencyX", false)]
    [InlineData("", false)]
    public void OnlyOwnSessionsAreConsideredStale(string name, bool expected) =>
        Assert.Equal(expected, DpcIsrCaptureService.IsOwnSessionName(name));

    [Fact]
    public async Task TimerRequests_RejectsOutOfRangeSeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LatencyAgentHandlers.TimerRequestsAsync(new TimerRequestsCaptureRequest(5), ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LatencyAgentHandlers.TimerRequestsAsync(new TimerRequestsCaptureRequest(121), ct));
    }
}
