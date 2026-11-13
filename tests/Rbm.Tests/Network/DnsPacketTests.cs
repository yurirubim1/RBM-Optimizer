using Rbm.Network.Dns;
using Xunit;

namespace Rbm.Tests.Network;

public class DnsPacketTests
{
    [Fact]
    public void BuildQuery_EncodesHeaderAndQuestion()
    {
        var q = DnsPacket.BuildQuery(0xBEEF, "www.example.com");
        Assert.Equal(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        var expectedName = new byte[] { 3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };
        Assert.Equal(expectedName, q[12..(12 + expectedName.Length)]);
        Assert.Equal(new byte[] { 0, 1, 0, 1 }, q[^4..]); // QTYPE=A, QCLASS=IN
    }

    [Fact]
    public void BuildQuery_RoundTripsThroughParser()
    {
        var q = DnsPacket.BuildQuery(42, "Example.ORG.");
        Assert.True(DnsPacket.TryParse(q, out var m, out _));
        Assert.Equal(42, m!.Id);
        Assert.False(m.IsResponse);
        Assert.True(m.RecursionDesired);
        Assert.Equal("example.org", Assert.Single(m.Questions).Name, ignoreCase: true);
    }

    [Fact]
    public void EncodeName_ConvertsIdnToPunycode()
    {
        var bytes = DnsPacket.EncodeName("ação.com.br");
        Assert.Contains("xn--", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData("bad name.com")]
    public void EncodeName_RejectsInvalid(string name) => Assert.Throws<ArgumentException>(() => DnsPacket.EncodeName(name));

    [Fact]
    public void EncodeName_RejectsLabelOver63()
    {
        Assert.Throws<ArgumentException>(() => DnsPacket.EncodeName(new string('a', 64) + ".com"));
    }

    private static byte[] Response(ushort id, byte flags2, bool tc, params byte[][] answers)
    {
        var q = DnsPacket.BuildQuery(id, "example.com");
        var list = new List<byte>(q);
        list[2] = (byte)(0x81 | (tc ? 0x02 : 0)); // QR + RD (+TC)
        list[3] = flags2;
        list[7] = (byte)answers.Length;          // ANCOUNT
        foreach (var a in answers) list.AddRange(a);
        return list.ToArray();
    }

    // Nome comprimido apontando para a pergunta (offset 12): C0 0C
    private static byte[] ARecord(byte a, byte b, byte c, byte d) =>
        new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0x0E, 0x10, 0, 4, a, b, c, d };

    [Fact]
    public void Parse_ResponseWithCompressedARecords()
    {
        var r = Response(7, 0x80, false, ARecord(93, 184, 216, 34), ARecord(1, 2, 3, 4));
        Assert.True(DnsPacket.TryParse(r, out var m, out var err), err);
        Assert.True(m!.IsResponse);
        Assert.True(m.RecursionAvailable);
        Assert.Equal(0, m.ResponseCode);
        Assert.Equal(2, m.Answers.Count);
        Assert.Equal("example.com", m.Answers[0].Name);
        Assert.Equal("93.184.216.34", m.Answers[0].Data);
        Assert.Equal(3600u, m.Answers[0].Ttl);
        Assert.True(DnsBenchmark.IsMatchingResponse(m, 7, "example.com"));
        Assert.False(DnsBenchmark.IsMatchingResponse(m, 8, "example.com"));
        Assert.False(DnsBenchmark.IsMatchingResponse(m, 7, "other.com"));

        var res = DnsBenchmark.ToResult("example.com", false, m, 12.5);
        Assert.Equal(DnsQueryOutcome.Answer, res.Outcome);
        Assert.Equal(new[] { "93.184.216.34", "1.2.3.4" }, res.Addresses);
    }

    [Fact]
    public void Parse_CnameChain()
    {
        // CNAME rdata = "www" + ponteiro para example.com (offset 12)
        var cname = new byte[] { 0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6, 3, (byte)'w', (byte)'w', (byte)'w', 0xC0, 0x0C };
        var r = Response(9, 0x80, false, cname);
        Assert.True(DnsPacket.TryParse(r, out var m, out _));
        Assert.Equal("www.example.com", m!.Answers[0].Data);
        Assert.Equal(DnsQueryOutcome.NoData, DnsBenchmark.ToResult("example.com", false, m, 1).Outcome);
    }

    [Fact]
    public void Parse_NxDomainAndServfail()
    {
        Assert.True(DnsPacket.TryParse(Response(1, 0x83, false), out var nx, out _));
        Assert.Equal(DnsQueryOutcome.NxDomain, DnsBenchmark.ToResult("example.com", false, nx!, 1).Outcome);
        Assert.True(DnsPacket.TryParse(Response(1, 0x82, false), out var sf, out _));
        Assert.Equal(DnsQueryOutcome.ServerFailure, DnsBenchmark.ToResult("example.com", false, sf!, 1).Outcome);
    }

    [Fact]
    public void Parse_TruncatedFlag_IsReportedAndCountsAsResponse()
    {
        Assert.True(DnsPacket.TryParse(Response(3, 0x80, true), out var m, out _));
        Assert.True(m!.Truncated);
        var res = DnsBenchmark.ToResult("example.com", false, m, 5);
        Assert.True(res.Truncated);
        Assert.True(res.Responded);
    }

    [Fact]
    public void Parse_RejectsPointerLoop()
    {
        // Ponteiro que aponta para si mesmo (offset 12)
        var data = new byte[] { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1 };
        Assert.False(DnsPacket.TryParse(data, out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Parse_RejectsTruncatedGarbage()
    {
        Assert.False(DnsPacket.TryParse(new byte[] { 1, 2, 3 }, out _, out _));
        var r = Response(7, 0x80, false, ARecord(1, 2, 3, 4));
        Assert.False(DnsPacket.TryParse(r[..^2], out _, out _)); // rdata cortado sem TC
    }

    [Fact]
    public void Summarize_SeparatesFirstAndCachedQueries()
    {
        var ep = new DnsServerEndpoint("1.1.1.1", "Cloudflare", DnsServerSource.Public);
        var results = new[]
        {
            new DnsQueryResult("a", false, DnsQueryOutcome.Answer, 20, "NOERROR", false, new[] { "1.2.3.4" }, null),
            new DnsQueryResult("a", true, DnsQueryOutcome.Answer, 5, "NOERROR", false, new[] { "1.2.3.4" }, null),
            new DnsQueryResult("b", false, DnsQueryOutcome.Timeout, null, "—", false, Array.Empty<string>(), null),
            new DnsQueryResult("b", true, DnsQueryOutcome.Answer, 7, "NOERROR", false, new[] { "1.2.3.4" }, null),
        };
        var s = DnsBenchmark.Summarize(ep, results);
        Assert.Equal(1, s.FirstQuery.Count);
        Assert.Equal(20, s.FirstQuery.Mean);
        Assert.Equal(2, s.CachedQuery.Count);
        Assert.Equal(6, s.CachedQuery.Mean);
        Assert.Equal(1, s.Timeouts);
        Assert.Equal(75, s.SuccessPercent);
    }
}
