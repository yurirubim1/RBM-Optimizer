using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Gaming.Correlation;
using Rbm.Gaming.Frames;
using Xunit;

namespace Rbm.Tests.Gaming;

public class StutterCorrelatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 20, 0, 0, TimeSpan.FromHours(-3));

    /// <summary>60 amostras de 1 s (t = 1..60 s); a amostra em t cobre a janela (t−1, t].</summary>
    private static List<TelemetrySnapshot> Telemetry(Func<int, TelemetrySnapshot, TelemetrySnapshot>? mutate = null)
    {
        var list = new List<TelemetrySnapshot>();
        for (int s = 1; s <= 60; s++)
        {
            var snap = new TelemetrySnapshot
            {
                Timestamp = T0.AddSeconds(s),
                CpuTotalPercent = 50,
                MemTotalBytes = 16L << 30,
                MemAvailableBytes = 8L << 30,
                HardFaultsPerSec = 10,
                DpcTimePercent = 0.5,
                CpuEffectiveClockMHz = Reading<double>.Of(4200),
                Gpus = [new GpuTelemetry { Name = "RX 6600", Utilization3DPercent = 99, DedicatedTotalBytes = 8L << 30, DedicatedUsedBytes = 4L << 30 }],
                Disks = [new DiskTelemetry { Instance = "0 C:", ActivePercent = 5 }],
            };
            list.Add(mutate?.Invoke(s, snap) ?? snap);
        }
        return list;
    }

    private static StutterSummary Stutters(params double[] timesMs)
    {
        var events = timesMs.Select((t, i) => new StutterEvent(i, t, 50, 10)).ToList();
        var clusters = timesMs.Select(t => new StutterCluster(t, t, 1, 50)).ToList();
        return new StutterSummary(events.Count, events.Count, events, clusters, StutterOptions.Default);
    }

    [Fact]
    public void Condition_only_present_during_stutters_is_flagged_as_temporal_association()
    {
        var tel = Telemetry((s, snap) => { if (s is 10 or 20 or 30) snap.CpuTotalPercent = 95; return snap; });
        var r = StutterCorrelator.Correlate(T0, 60_000, Stutters(9_500, 19_500, 29_500), tel);

        Assert.Equal(60, r.TelemetryWindows);
        Assert.Equal(3, r.StutterWindows);
        Assert.Equal(57, r.BaselineWindows);
        var cpu = r.Associations.Single(a => a.Condition == TelemetryCondition.CpuTotalHigh);
        Assert.Equal(3, cpu.StutterWindowsWithCondition);
        Assert.Equal(0, cpu.BaselineWindowsWithCondition);
        Assert.Null(cpu.Lift);
        Assert.True(cpu.Notable);
        Assert.Contains("nunca ocorreu fora dos stutters", cpu.TextPt);
        Assert.DoesNotContain("causa:", r.SummaryPt, StringComparison.OrdinalIgnoreCase);

        // GPU a 99% o tempo todo: presente nos stutters, mas lift 1 → não relevante
        var gpu = r.Associations.Single(a => a.Condition == TelemetryCondition.GpuUtilHigh);
        Assert.Equal(1.0, gpu.Lift!.Value, 9);
        Assert.False(gpu.Notable);

        Assert.All(r.Clusters, c => Assert.Contains(TelemetryCondition.CpuTotalHigh, c.ActiveConditions));
    }

    [Fact]
    public void Lift_is_ratio_of_stutter_rate_to_baseline_rate()
    {
        // Disco > 90% nas 3 janelas com stutter e em 6 das 57 sem stutter → lift = 1 / (6/57) = 9,5
        var hot = new HashSet<int> { 10, 20, 30, 40, 41, 42, 50, 51, 52 };
        var tel = Telemetry((s, snap) => { if (hot.Contains(s)) snap.Disks[0].ActivePercent = 100; return snap; });
        var r = StutterCorrelator.Correlate(T0, 60_000, Stutters(9_500, 19_500, 29_500), tel);
        var disk = r.Associations.Single(a => a.Condition == TelemetryCondition.DiskActiveHigh);
        Assert.Equal(1.0, disk.StutterRate!.Value, 9);
        Assert.Equal(6.0 / 57, disk.BaselineRate!.Value, 9);
        Assert.Equal(9.5, disk.Lift!.Value, 9);
        Assert.True(disk.Notable);
        Assert.Contains("lift 9,5×", disk.TextPt.Replace('.', ','));
    }

    [Fact]
    public void Clock_drop_uses_session_median_and_unavailable_sensors_are_skipped()
    {
        var tel = Telemetry((s, snap) => { if (s == 15) snap.CpuEffectiveClockMHz = Reading<double>.Of(2000); return snap; });
        var r = StutterCorrelator.Correlate(T0, 60_000, Stutters(14_200), tel);
        var clk = r.Associations.Single(a => a.Condition == TelemetryCondition.CpuClockDrop);
        Assert.Equal(1, clk.StutterWindowsWithCondition);
        Assert.False(clk.Notable); // apenas 1 janela: abaixo do mínimo
        Assert.DoesNotContain(r.Associations, a => a.Condition == TelemetryCondition.CpuTempHigh); // sem sensor de temperatura
    }

    [Fact]
    public void Handles_missing_or_non_overlapping_telemetry()
    {
        Assert.Contains("Sem telemetria", StutterCorrelator.Correlate(T0, 10_000, Stutters(5_000), []).SummaryPt);
        var later = Telemetry().Select(s => new TelemetrySnapshot { Timestamp = s.Timestamp.AddHours(1) }).ToList();
        Assert.Equal(0, StutterCorrelator.Correlate(T0, 10_000, Stutters(5_000), later).TelemetryWindows);
        var none = StutterCorrelator.Correlate(T0, 60_000, Stutters(), Telemetry());
        Assert.Contains("Nenhum stutter", none.SummaryPt);
    }

    [Fact]
    public void Evaluate_reports_unavailable_as_null()
    {
        var o = CorrelationOptions.Default;
        var s = new TelemetrySnapshot { MemTotalBytes = 0 };
        Assert.Null(StutterCorrelator.Evaluate(TelemetryCondition.RamAvailableLow, s, o, null));
        Assert.Null(StutterCorrelator.Evaluate(TelemetryCondition.VramNearFull, s, o, null));
        Assert.Null(StutterCorrelator.Evaluate(TelemetryCondition.CpuClockDrop, s, o, 4000));
        s.MemTotalBytes = 100; s.MemAvailableBytes = 5;
        Assert.True(StutterCorrelator.Evaluate(TelemetryCondition.RamAvailableLow, s, o, null));
        s.Gpus.Add(new GpuTelemetry { DedicatedTotalBytes = 100, DedicatedUsedBytes = 96, HotspotC = Reading<double>.Of(106) });
        Assert.True(StutterCorrelator.Evaluate(TelemetryCondition.VramNearFull, s, o, null));
        Assert.True(StutterCorrelator.Evaluate(TelemetryCondition.GpuTempHigh, s, o, null));
    }
}
