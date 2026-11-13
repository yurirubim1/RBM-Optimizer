using Rbm.Network.Native;
using Rbm.Network.Path;

namespace Rbm.Network.Latency;

public sealed record DefaultTargetsResult(IReadOnlyList<LatencyTarget> Targets, string? GatewayAddress, string? IspFirstHopAddress, IReadOnlyList<string> NotesPt);

/// <summary>Monta o conjunto padrão de alvos: gateway, primeiro salto do provedor (descoberto), DNS públicos anycast e hosts do usuário.</summary>
public static class DefaultTargets
{
    public static readonly IReadOnlyList<LatencyTarget> PublicDns = new[]
    {
        new LatencyTarget("1.1.1.1", "Cloudflare DNS (1.1.1.1)", TargetRole.PublicDns),
        new LatencyTarget("8.8.8.8", "Google DNS (8.8.8.8)", TargetRole.PublicDns),
        new LatencyTarget("9.9.9.9", "Quad9 DNS (9.9.9.9)", TargetRole.PublicDns),
    };

    /// <param name="customTargets">Alvos do usuário (ex.: servidores de jogos). Hosts inválidos são ignorados com nota.</param>
    /// <param name="discoverIspHop">Executa um traceroute curto (até 12 saltos, 1 sonda) para achar o primeiro salto do provedor.</param>
    public static async Task<DefaultTargetsResult> DiscoverAsync(IEnumerable<LatencyTarget>? customTargets = null, bool discoverIspHop = true, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var targets = new List<LatencyTarget>();

        var gw = IpHelper.DefaultGatewayIPv4();
        string? gwAddr = gw?.Gateway.ToString();
        if (gwAddr is not null) targets.Add(new LatencyTarget(gwAddr, $"Roteador / gateway ({gwAddr})", TargetRole.Gateway));
        else notes.Add("Gateway padrão IPv4 não encontrado (sem rota padrão ou sem conexão).");

        string? ispAddr = null;
        if (discoverIspHop)
        {
            var trace = await PathAnalyzer.TraceAsync("1.1.1.1", new TracerouteOptions
            {
                MaxHops = 12, ProbesPerHop = 1, PerHopTimeout = TimeSpan.FromMilliseconds(900), ResolveHostNames = false,
            }, null, ct).ConfigureAwait(false);
            if (trace.Error is null)
            {
                var segs = HopClassifier.Classify(trace.Hops.Select(h => (h.Address, h.HostName, h.IsDestination)).ToList(), gwAddr);
                for (int i = 0; i < trace.Hops.Count; i++)
                    if (segs[i] == PathSegment.Isp && trace.Hops[i].Address is { } a) { ispAddr = a; break; }
            }
            if (ispAddr is not null) targets.Add(new LatencyTarget(ispAddr, $"Primeiro salto do provedor ({ispAddr})", TargetRole.IspFirstHop));
            else notes.Add("Primeiro salto do provedor não identificado (roteadores podem não responder a ICMP).");
        }

        targets.AddRange(PublicDns);

        foreach (var t in customTargets ?? Enumerable.Empty<LatencyTarget>())
        {
            if (!Probes.IsValidHost(t.Host)) { notes.Add($"Host ignorado (inválido): {t.Host}"); continue; }
            if (targets.Any(x => x.Id == t.Id)) continue;
            targets.Add(t with { Role = TargetRole.Custom });
        }
        return new DefaultTargetsResult(targets, gwAddr, ispAddr, notes);
    }
}
