using System.Net;
using System.Net.NetworkInformation;
using Rbm.Network.Latency;
using Rbm.Network.Native;

namespace Rbm.Network.Path;

/// <summary>
/// Traceroute por ICMP com TTL crescente (System.Net.NetworkInformation.Ping) e medição contínua por salto (estilo mtr),
/// seguida da segmentação "rede local / provedor / internet / destino". Sem elevação.
/// </summary>
public static class PathAnalyzer
{
    /// <summary>Traceroute: cada rodada envia sondas para todos os TTLs em paralelo; após achar o destino, limita-se a ele.</summary>
    public static async Task<TracerouteResult> TraceAsync(string host, TracerouteOptions? options = null, IProgress<TraceHop>? progress = null, CancellationToken ct = default)
    {
        var o = options ?? new TracerouteOptions();
        if (!Probes.IsValidHost(host)) return new TracerouteResult(host, null, false, Array.Empty<TraceHop>(), DateTimeOffset.Now, "Host inválido.");
        if (o.MaxHops is < 1 or > 64 || o.ProbesPerHop is < 1 or > 10)
            return new TracerouteResult(host, null, false, Array.Empty<TraceHop>(), DateTimeOffset.Now, "Opções inválidas (MaxHops 1–64, ProbesPerHop 1–10).");

        var dest = await Probes.ResolveAsync(host, ct).ConfigureAwait(false);
        if (dest is null) return new TracerouteResult(host, null, false, Array.Empty<TraceHop>(), DateTimeOffset.Now, "Falha ao resolver o nome.");

        var payload = new byte[Math.Clamp(o.PayloadBytes, 0, 1472)];
        var results = new ProbeResult?[o.MaxHops + 1, o.ProbesPerHop];
        int limit = o.MaxHops;

        for (int round = 0; round < o.ProbesPerHop; round++)
        {
            int roundLimit = limit;
            var tasks = Enumerable.Range(1, roundLimit).Select(async ttl =>
            {
                using var ping = new Ping();
                var r = await Probes.IcmpAsync(ping, dest, o.PerHopTimeout, payload, ttl, ct).ConfigureAwait(false);
                results[ttl, round] = r;
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            for (int ttl = 1; ttl <= roundLimit; ttl++)
                if (results[ttl, round] is { Status: ProbeStatus.Success }) { limit = Math.Min(limit, ttl); break; }
        }

        int destTtl = Enumerable.Range(1, o.MaxHops).FirstOrDefault(t => Enumerable.Range(0, o.ProbesPerHop).Any(r => results[t, r] is { Status: ProbeStatus.Success }));
        bool reached = destTtl > 0;
        int lastResponsive = Enumerable.Range(1, o.MaxHops).LastOrDefault(t => Enumerable.Range(0, o.ProbesPerHop).Any(r => results[t, r]?.ReplyFrom is not null));
        int count = reached ? destTtl : Math.Min(o.MaxHops, Math.Max(lastResponsive + 2, 1));

        var hops = new List<TraceHop>(count);
        for (int ttl = 1; ttl <= count; ttl++)
        {
            var probes = Enumerable.Range(0, o.ProbesPerHop).Select(r => results[ttl, r]).ToList();
            var answered = probes.Where(p => p is { Status: ProbeStatus.Success or ProbeStatus.TtlExpired } && p.Value.ReplyFrom is not null).Select(p => p!.Value).ToList();
            var addrs = answered.Select(p => p.ReplyFrom!.ToString()).Distinct().ToList();
            string? main = answered.GroupBy(p => p.ReplyFrom!.ToString()).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var rtts = probes.Select(p => p is { Status: ProbeStatus.Success or ProbeStatus.TtlExpired } ? p.Value.ElapsedMs : null).ToList();
            hops.Add(new TraceHop(ttl, main, addrs, null, rtts, reached && ttl == destTtl));
        }

        if (o.ResolveHostNames)
            hops = (await Task.WhenAll(hops.Select(async h => h.Address is null ? h : h with { HostName = await ReverseAsync(h.Address, o.HostNameTimeout, ct).ConfigureAwait(false) })).ConfigureAwait(false)).ToList();

        foreach (var h in hops) progress?.Report(h);
        return new TracerouteResult(host, dest.ToString(), reached, hops, DateTimeOffset.Now, null);
    }

    private static async Task<string?> ReverseAsync(string address, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var e = await System.Net.Dns.GetHostEntryAsync(address, cts.Token).ConfigureAwait(false);
            return string.IsNullOrEmpty(e.HostName) || e.HostName == address ? null : e.HostName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Traceroute + <see cref="PathAnalysisOptions.Rounds"/> rodadas de sondas com TTL limitado para cada salto,
    /// medindo perda/latência/jitter por salto e por segmento. <paramref name="progress"/> recebe a análise parcial a cada rodada.
    /// Cancelamento devolve a análise com as rodadas concluídas.
    /// </summary>
    public static async Task<PathAnalysis> AnalyzeAsync(string host, PathAnalysisOptions? options = null, IProgress<PathAnalysis>? progress = null, CancellationToken ct = default)
    {
        var o = options ?? new PathAnalysisOptions();
        var trace = await TraceAsync(host, o.Trace, null, ct).ConfigureAwait(false);
        string? gateway = IpHelper.DefaultGatewayIPv4()?.Gateway.ToString();
        if (trace.Error is not null || trace.DestinationAddress is null || trace.Hops.Count == 0)
            return new PathAnalysis(host, trace.DestinationAddress, false, 0, Array.Empty<AnalyzedHop>(), Array.Empty<SegmentSummary>(), null, double.NaN,
                trace.Error ?? "Nenhum salto encontrado.", Array.Empty<string>());

        var dest = IPAddress.Parse(trace.DestinationAddress);
        int n = trace.Hops.Count;
        var samples = Enumerable.Range(0, n).Select(_ => new List<LatencySample>()).ToArray();
        var addrCount = Enumerable.Range(0, n).Select(_ => new Dictionary<string, int>()).ToArray();
        var payload = new byte[Math.Clamp(o.Trace.PayloadBytes, 0, 1472)];
        int rounds = Math.Clamp(o.Rounds, 1, 10_000);
        int done = 0;

        PathAnalysis Build() => PathInterpreter.Interpret(host, trace.DestinationAddress, trace.Reached, done,
            Enumerable.Range(0, n).Select(i =>
            {
                var th = trace.Hops[i];
                string? addr = addrCount[i].Count > 0 ? addrCount[i].OrderByDescending(kv => kv.Value).First().Key : th.Address;
                string? name = addr == th.Address ? th.HostName : null;
                List<LatencySample> copy;
                lock (samples[i]) copy = samples[i].ToList();
                return new HopMeasurement(th.Ttl, addr, name, LatencyMath.Compute(copy), th.IsDestination);
            }).ToList(), gateway, o.IspPublicHops, o.LossThresholdPercent);

        var next = DateTimeOffset.UtcNow;
        try
        {
            for (int round = 1; round <= rounds; round++)
            {
                next += o.Interval;
                int r = round;
                await Task.WhenAll(Enumerable.Range(0, n).Select(async i =>
                {
                    using var ping = new Ping();
                    var th = trace.Hops[i];
                    var res = await Probes.IcmpAsync(ping, dest, o.Trace.PerHopTimeout, payload, th.Ttl, ct).ConfigureAwait(false);
                    bool ok = res.Status is ProbeStatus.Success or ProbeStatus.TtlExpired && res.ReplyFrom is not null;
                    var s = new LatencySample($"hop:{th.Ttl}", $"Salto {th.Ttl}", r, DateTimeOffset.Now,
                        ok ? ProbeStatus.Success : res.Status == ProbeStatus.TtlExpired ? ProbeStatus.Error : res.Status,
                        ok ? res.ElapsedMs : null, res.ReplyFrom?.ToString(), res.Detail);
                    lock (samples[i])
                    {
                        samples[i].Add(s);
                        if (ok) { var k = res.ReplyFrom!.ToString(); addrCount[i][k] = addrCount[i].GetValueOrDefault(k) + 1; }
                    }
                })).ConfigureAwait(false);
                done = round;
                progress?.Report(Build());
                var wait = next - DateTimeOffset.UtcNow;
                if (round < rounds && wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                else if (wait <= TimeSpan.Zero) next = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return Build();
    }
}
