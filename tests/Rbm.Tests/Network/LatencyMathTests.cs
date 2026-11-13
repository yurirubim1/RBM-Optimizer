using Rbm.Network.Latency;
using Xunit;

namespace Rbm.Tests.Network;

public class LatencyMathTests
{
    [Fact]
    public void MeanAbsoluteDifference_ComputesConsecutiveDeltas()
    {
        // |12-10| + |11-12| + |15-11| = 2 + 1 + 4 = 7 → 7/3
        Assert.Equal(7.0 / 3, LatencyMath.MeanAbsoluteDifference(new double[] { 10, 12, 11, 15 }), 10);
    }

    [Fact]
    public void MeanAbsoluteDifference_LessThanTwo_IsNaN()
    {
        Assert.True(double.IsNaN(LatencyMath.MeanAbsoluteDifference(new double[] { 5 })));
        Assert.True(double.IsNaN(LatencyMath.MeanAbsoluteDifference(Array.Empty<double>())));
    }

    [Fact]
    public void Rfc3550Jitter_FollowsSmoothingFormula()
    {
        // D = 2, 1, 4 → J1 = 2/16 = 0.125; J2 = 0.125 + (1-0.125)/16 = 0.1796875; J3 = 0.1796875 + (4-0.1796875)/16
        double j3 = 0.1796875 + (4 - 0.1796875) / 16;
        Assert.Equal(j3, LatencyMath.Rfc3550Jitter(new double[] { 10, 12, 11, 15 }), 12);
    }

    [Fact]
    public void Rfc3550Jitter_ConstantRtt_IsZero()
    {
        Assert.Equal(0, LatencyMath.Rfc3550Jitter(new double[] { 20, 20, 20, 20 }));
    }

    [Fact]
    public void Compute_CountsLossTimeoutsAndConsecutiveRuns()
    {
        var s = LatencyMath.Compute(new double?[] { 10, null, null, 12, 14, null, 16 });
        Assert.Equal(7, s.Sent);
        Assert.Equal(4, s.Received);
        Assert.Equal(3, s.Lost);
        Assert.Equal(3, s.Timeouts);
        Assert.Equal(2, s.MaxConsecutiveLost);
        Assert.Equal(300.0 / 7, s.LossPercent, 10);
        Assert.Equal(10, s.MinMs);
        Assert.Equal(16, s.MaxMs);
        Assert.Equal(13, s.AvgMs, 10);
        Assert.Equal(13, s.MedianMs, 10);
        // Jitter ignora perdas: 10,12,14,16 → |Δ| = 2
        Assert.Equal(2, s.JitterMs, 10);
    }

    [Fact]
    public void Compute_AllLost_HasNaNLatencyAnd100PercentLoss()
    {
        var s = LatencyMath.Compute(new double?[] { null, null, null });
        Assert.Equal(100, s.LossPercent);
        Assert.True(double.IsNaN(s.AvgMs));
        Assert.True(double.IsNaN(s.JitterMs));
        Assert.False(s.HasData);
    }

    [Fact]
    public void Compute_Percentiles_UseLinearInterpolation()
    {
        var rtts = Enumerable.Range(1, 100).Select(i => (double?)i).ToArray();
        var s = LatencyMath.Compute(rtts);
        Assert.Equal(95.05, s.P95Ms, 6);
        Assert.Equal(99.01, s.P99Ms, 6);
        Assert.Equal(0, s.LossPercent);
    }

    [Fact]
    public void Compute_UsesSequenceOrderNotInputOrder()
    {
        var samples = new[]
        {
            new LatencySample("t", "t", 3, default, ProbeStatus.Success, 30, null),
            new LatencySample("t", "t", 1, default, ProbeStatus.Success, 10, null),
            new LatencySample("t", "t", 2, default, ProbeStatus.Success, 20, null),
        };
        Assert.Equal(10, LatencyMath.Compute(samples).JitterMs, 10);
    }

    [Fact]
    public void Accumulator_KeepsWindowAndIgnoresUnknownTargets()
    {
        var t = new LatencyTarget("1.1.1.1", "x");
        var acc = new LatencyAccumulator(new[] { t }, maxSamplesPerTarget: 10);
        for (int i = 1; i <= 25; i++) acc.Add(new LatencySample(t.Id, "x", i, default, ProbeStatus.Success, i, null));
        acc.Add(new LatencySample("icmp:other", "y", 1, default, ProbeStatus.Success, 1, null));
        var snap = Assert.Single(acc.Snapshot());
        Assert.Equal(10, snap.Stats.Sent);
        Assert.Equal(16, snap.Stats.MinMs);
    }

    [Fact]
    public void HistoryRecord_FlattensResult()
    {
        var t = new LatencyTarget("8.8.8.8", "Google", TargetRole.PublicDns);
        var stats = LatencyMath.Compute(new double?[] { 10, 11, null, 12 });
        var r = new LatencyTestResult(DateTimeOffset.Now, DateTimeOffset.Now, new[] { new TargetLatencyResult(t, "8.8.8.8", stats, Array.Empty<LatencySample>(), null) }, false);
        var h = Assert.Single(LatencyHistoryRecord.From(r, "Ethernet", "Ethernet"));
        Assert.Equal("icmp:8.8.8.8", h.TargetId);
        Assert.Equal(25, h.LossPercent, 10);
        Assert.Equal(TargetRole.PublicDns, h.Role);
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("example.com", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("", false)]
    [InlineData("bad host; rm", false)]
    public void IsValidHost(string host, bool ok) => Assert.Equal(ok, Probes.IsValidHost(host));
}
