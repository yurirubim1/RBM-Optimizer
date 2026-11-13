using Rbm.Network.Latency;
using Rbm.Network.Path;
using Xunit;

namespace Rbm.Tests.Network;

public class PathTests
{
    [Theory]
    [InlineData("192.168.1.1", AddressScope.Private)]
    [InlineData("10.0.0.1", AddressScope.Private)]
    [InlineData("172.16.5.1", AddressScope.Private)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("100.64.0.1", AddressScope.Cgnat)]
    [InlineData("100.127.255.254", AddressScope.Cgnat)]
    [InlineData("100.128.0.1", AddressScope.Public)]
    [InlineData("169.254.10.10", AddressScope.LinkLocal)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("fd00::1", AddressScope.Private)]
    [InlineData("fe80::1", AddressScope.LinkLocal)]
    [InlineData("2001:4860:4860::8888", AddressScope.Public)]
    [InlineData("not-an-ip", AddressScope.Unknown)]
    public void ScopeOf(string ip, AddressScope expected) => Assert.Equal(expected, HopClassifier.ScopeOf(ip));

    [Theory]
    [InlineData("xe-0-0-1.core1.spo.vivo.com.br", "vivo.com.br")]
    [InlineData("ae1.cr1.gru.example.net", "example.net")]
    [InlineData("host.co.uk", "host.co.uk")]
    [InlineData("1.2.3.4", null)]
    [InlineData(null, null)]
    public void RegistrableDomain(string? host, string? expected) => Assert.Equal(expected, HopClassifier.RegistrableDomain(host));

    [Fact]
    public void Classify_TypicalHomeCgnatPath()
    {
        var hops = new (string?, string?, bool)[]
        {
            ("192.168.0.1", null, false),               // roteador
            ("100.64.12.1", null, false),               // CGNAT do provedor
            (null, null, false),                        // sem resposta entre saltos do provedor
            ("200.100.1.1", "a.isp.com.br", false),     // 1º público
            ("200.100.2.1", "b.isp.com.br", false),     // 2º público
            ("200.100.3.1", "c.isp.com.br", false),     // mesmo domínio → provedor
            ("72.14.1.1", "transit.net", false),        // trânsito
            ("1.1.1.1", null, true),                    // destino
        };
        var seg = HopClassifier.Classify(hops, "192.168.0.1", ispPublicHops: 2);
        Assert.Equal(new[]
        {
            PathSegment.Router, PathSegment.Isp, PathSegment.Isp, PathSegment.Isp, PathSegment.Isp, PathSegment.Isp,
            PathSegment.Remote, PathSegment.Destination,
        }, seg);
    }

    [Theory]
    [InlineData("as13335.saopaulo.sp.ix.br", true)]
    [InlineData("decix-fra.example.net", true)]
    [InlineData("matrix.example.net", false)]
    [InlineData("de-cix.fra.example.net", true)]
    [InlineData("ae1.cr1.gru.example.net", false)]
    [InlineData("six.example.com", false)]
    [InlineData(null, false)]
    public void IsIxpHost(string? host, bool expected) => Assert.Equal(expected, HopClassifier.IsIxpHost(host));

    [Fact]
    public void Classify_IxpHopEndsIspSegment()
    {
        // Caminho real observado: roteador → CGNAT → privado do provedor → IX.br → Cloudflare.
        var seg = HopClassifier.Classify(new (string?, string?, bool)[]
        {
            ("192.168.7.1", null, false), ("100.66.0.1", null, false), ("172.29.100.29", null, false),
            ("187.16.219.111", "as13335.saopaulo.sp.ix.br", false), ("172.68.16.115", null, false), ("1.1.1.1", "one.one.one.one", true),
        }, "192.168.7.1");
        Assert.Equal(new[] { PathSegment.Router, PathSegment.Isp, PathSegment.Isp, PathSegment.Remote, PathSegment.Remote, PathSegment.Destination }, seg);
    }

    [Fact]
    public void Classify_FirstHopIsRouterEvenWithoutReply()
    {
        var seg = HopClassifier.Classify(new (string?, string?, bool)[] { (null, null, false), ("8.8.4.4", null, false), ("8.8.8.8", null, true) }, "192.168.1.1");
        Assert.Equal(PathSegment.Router, seg[0]);
        Assert.Equal(PathSegment.Isp, seg[1]);
        Assert.Equal(PathSegment.Destination, seg[2]);
    }

    [Fact]
    public void Classify_PublicGateway_NoHomeRouter()
    {
        var seg = HopClassifier.Classify(new (string?, string?, bool)[] { ("201.1.1.1", null, false), ("201.1.2.1", null, false), ("9.9.9.9", null, true) }, "201.1.1.1", 1);
        Assert.Equal(PathSegment.Isp, seg[0]);
        Assert.Equal(PathSegment.Remote, seg[1]);
    }

    [Fact]
    public void Classify_IsMonotonic_PrivateInTransitStaysRemote()
    {
        var seg = HopClassifier.Classify(new (string?, string?, bool)[]
        {
            ("192.168.0.1", null, false), ("187.1.1.1", null, false), ("72.1.1.1", null, false), ("10.10.10.10", null, false), ("1.1.1.1", null, true),
        }, "192.168.0.1", ispPublicHops: 1);
        Assert.Equal(PathSegment.Remote, seg[3]);
    }

    private static HopMeasurement Hop(int ttl, string? addr, double lossPct, double medianMs, bool dest = false, int n = 100)
    {
        int lost = (int)Math.Round(n * lossPct / 100);
        var rtts = Enumerable.Range(0, n).Select(i => i < lost ? (double?)null : medianMs).ToArray();
        if (addr is null) rtts = Enumerable.Repeat<double?>(null, n).ToArray();
        return new HopMeasurement(ttl, addr, null, LatencyMath.Compute(rtts), dest);
    }

    [Fact]
    public void Interpret_IntermediateLossThatDoesNotPropagate_IsIcmpRateLimiting()
    {
        var hops = new[]
        {
            Hop(1, "192.168.0.1", 0, 1),
            Hop(2, "100.64.0.1", 0, 8),
            Hop(3, "200.1.1.1", 40, 10),  // perda só aqui
            Hop(4, "200.1.2.1", 0, 12),
            Hop(5, "1.1.1.1", 0, 15, dest: true),
        };
        var a = PathInterpreter.Interpret("1.1.1.1", "1.1.1.1", true, 100, hops, "192.168.0.1");
        Assert.Equal(HopInterpretation.IcmpRateLimitedLoss, a.Hops[2].Interpretation);
        Assert.Null(a.RealLossOrigin);
        Assert.Contains("Sem perda real", a.VerdictPt);
    }

    [Fact]
    public void Interpret_LossStartingAtIspAndPersisting_IsRealWithIspOrigin()
    {
        var hops = new[]
        {
            Hop(1, "192.168.0.1", 0, 1),
            Hop(2, "100.64.0.1", 6, 8),
            Hop(3, "200.1.1.1", 7, 10),
            Hop(4, "72.1.1.1", 6, 20),
            Hop(5, "1.1.1.1", 6, 22, dest: true),
        };
        var a = PathInterpreter.Interpret("1.1.1.1", "1.1.1.1", true, 100, hops, "192.168.0.1");
        Assert.Equal(PathSegment.Isp, a.RealLossOrigin);
        Assert.Equal(HopInterpretation.RealLossOrigin, a.Hops[1].Interpretation);
        Assert.Equal(HopInterpretation.RealLossPropagated, a.Hops[4].Interpretation);
        Assert.Contains("provedor", a.VerdictPt);
        var ispSeg = a.Segments.Single(s => s.Segment == PathSegment.Isp);
        Assert.True(ispSeg.LossIsReal);
    }

    [Fact]
    public void Interpret_LossAtRouter_IsLocalOrigin()
    {
        var hops = new[] { Hop(1, "192.168.0.1", 10, 3), Hop(2, "100.64.0.1", 10, 9), Hop(3, "8.8.8.8", 11, 14, dest: true) };
        var a = PathInterpreter.Interpret("8.8.8.8", "8.8.8.8", true, 100, hops, "192.168.0.1");
        Assert.Equal(PathSegment.Router, a.RealLossOrigin);
        Assert.Contains("roteador", a.VerdictPt);
    }

    [Fact]
    public void Interpret_SilentHopAndSlowControlPlane_AreNotProblems()
    {
        var hops = new[]
        {
            Hop(1, "192.168.0.1", 0, 1),
            Hop(2, null, 100, double.NaN),
            Hop(3, "200.1.1.1", 0, 80),   // lento só aqui
            Hop(4, "200.1.2.1", 0, 12),
            Hop(5, "1.1.1.1", 0, 13, dest: true),
        };
        var a = PathInterpreter.Interpret("1.1.1.1", "1.1.1.1", true, 100, hops, "192.168.0.1");
        Assert.Equal(HopInterpretation.NoIcmpResponse, a.Hops[1].Interpretation);
        Assert.Equal(HopInterpretation.ControlPlaneDelay, a.Hops[2].Interpretation);
        Assert.Null(a.RealLossOrigin);
    }

    [Fact]
    public void Interpret_SegmentsHaveIncrementalLatency()
    {
        var hops = new[]
        {
            Hop(1, "192.168.0.1", 0, 1),
            Hop(2, "200.1.1.1", 0, 9),
            Hop(3, "72.1.1.1", 0, 25),
            Hop(4, "1.1.1.1", 0, 30, dest: true),
        };
        var a = PathInterpreter.Interpret("1.1.1.1", "1.1.1.1", true, 100, hops, "192.168.0.1", ispPublicHops: 1);
        var router = a.Segments.Single(s => s.Segment == PathSegment.Router);
        var isp = a.Segments.Single(s => s.Segment == PathSegment.Isp);
        var remote = a.Segments.Single(s => s.Segment == PathSegment.Remote);
        var dest = a.Segments.Single(s => s.Segment == PathSegment.Destination);
        Assert.Equal(1, router.IncrementalMs, 6);
        Assert.Equal(8, isp.IncrementalMs, 6);
        Assert.Equal(16, remote.IncrementalMs, 6);
        Assert.Equal(5, dest.IncrementalMs, 6);
    }
}
