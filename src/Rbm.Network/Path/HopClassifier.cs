using System.Net;
using System.Net.Sockets;

namespace Rbm.Network.Path;

/// <summary>Classificação pura de endereços e saltos (testável).</summary>
public static class HopClassifier
{
    public static AddressScope ScopeOf(string? address) =>
        address is not null && IPAddress.TryParse(address, out var ip) ? ScopeOf(ip) : AddressScope.Unknown;

    public static AddressScope ScopeOf(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return AddressScope.Loopback;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 10) return AddressScope.Private;
            if (b[0] == 172 && (b[1] & 0xF0) == 16) return AddressScope.Private;
            if (b[0] == 192 && b[1] == 168) return AddressScope.Private;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return AddressScope.Cgnat;   // 100.64.0.0/10 (RFC 6598)
            if (b[0] == 169 && b[1] == 254) return AddressScope.LinkLocal;
            if (b[0] >= 224 && b[0] <= 239) return AddressScope.Multicast;
            if (b[0] == 0 || b[0] >= 240) return AddressScope.Unknown;
            return AddressScope.Public;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return AddressScope.LinkLocal;
            if (ip.IsIPv6Multicast) return AddressScope.Multicast;
            if ((b[0] & 0xFE) == 0xFC) return AddressScope.Private;              // fc00::/7 (ULA)
            if (ip.IsIPv6SiteLocal) return AddressScope.Private;
            return AddressScope.Public;
        }
        return AddressScope.Unknown;
    }

    public static bool IsNonPublic(AddressScope s) => s is AddressScope.Private or AddressScope.Cgnat or AddressScope.LinkLocal or AddressScope.Loopback;

    private static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
    { "com", "net", "org", "gov", "edu", "co", "ac", "ne", "or", "mil", "nom", "gob" };

    /// <summary>Domínio "registrável" aproximado (ex.: "xe-0-0.core1.vivo.com.br" → "vivo.com.br"). Null se não houver.</summary>
    public static string? RegistrableDomain(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return null;
        var h = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(h, out _)) return null;
        var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2) return null;
        int take = 2;
        if (labels.Length >= 3 && labels[^1].Length == 2 && SecondLevel.Contains(labels[^2])) take = 3;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>
    /// Atribui um segmento a cada salto (mesma ordem de <paramref name="hops"/>, por TTL crescente).
    /// Heurística (sem consulta de ASN):
    /// 1) TTL 1 (sempre o gateway padrão) → Roteador; se o gateway for público (PPPoE direto), → Provedor;
    /// 2) saltos privados/CGNAT após o roteador e antes do primeiro público → Provedor;
    /// 3) os primeiros <paramref name="ispPublicHops"/> saltos públicos → Provedor, estendido enquanto o DNS reverso
    ///    tiver o mesmo domínio do primeiro salto público do provedor;
    /// 4) salto com DNS reverso de ponto de troca de tráfego (IXP, ex.: *.ix.br) e demais → Remoto; o destino alcançado → Destino.
    /// Segmentos nunca "voltam" (monotônicos). Saltos sem resposta herdam o segmento dos vizinhos quando estes coincidem.
    /// </summary>
    public static IReadOnlyList<PathSegment> Classify(IReadOnlyList<(string? Address, string? HostName, bool IsDestination)> hops,
        string? gatewayAddress, int ispPublicHops = 2)
    {
        var seg = new PathSegment[hops.Count];
        var current = PathSegment.Unknown;
        int publicCount = 0;
        string? ispDomain = null;
        // O TTL 1 sempre expira no gateway padrão. Se o gateway for público (PPPoE direto no PC), não há roteador doméstico.
        bool gatewayIsPublic = ScopeOf(gatewayAddress) == AddressScope.Public;

        for (int i = 0; i < hops.Count; i++)
        {
            var (addr, host, isDest) = hops[i];
            if (i == 0 && !isDest)
            {
                seg[0] = current = gatewayIsPublic ? PathSegment.Isp : PathSegment.Router;
                if (gatewayIsPublic && addr is not null) { publicCount++; ispDomain = RegistrableDomain(host); }
                continue;
            }
            if (addr is null) { seg[i] = PathSegment.Unknown; continue; }
            var scope = ScopeOf(addr);
            PathSegment s;

            if (isDest) s = PathSegment.Destination;
            else if (current <= PathSegment.Router && string.Equals(addr, gatewayAddress, StringComparison.OrdinalIgnoreCase)) s = PathSegment.Router;
            else if (scope == AddressScope.Public && IsIxpHost(host))
            {
                publicCount++;
                s = PathSegment.Remote; // ponto de troca de tráfego: o pacote já saiu da rede do provedor
            }
            else if (scope == AddressScope.Public)
            {
                publicCount++;
                string? dom = RegistrableDomain(host);
                if (publicCount <= ispPublicHops && current <= PathSegment.Isp)
                {
                    s = PathSegment.Isp;
                    ispDomain ??= dom;
                }
                else if (ispDomain is not null && dom is not null && dom == ispDomain && current <= PathSegment.Isp) s = PathSegment.Isp;
                else s = PathSegment.Remote;
            }
            else if (IsNonPublic(scope) && publicCount == 0) s = PathSegment.Isp; // CGNAT / rede interna do provedor (ou NAT duplo)
            else s = current == PathSegment.Unknown ? PathSegment.Isp : current; // privado no meio do trânsito (MPLS etc.)

            if (s < current) s = current; // monotônico
            seg[i] = s;
            current = s;
        }

        // Saltos sem resposta: herdam o segmento se vizinhos responsivos concordam.
        for (int i = 0; i < seg.Length; i++)
        {
            if (hops[i].Address is not null || (i == 0 && !hops[0].IsDestination)) continue; // TTL 1 já foi atribuído
            PathSegment prev = PathSegment.Unknown, next = PathSegment.Unknown;
            for (int j = i - 1; j >= 0; j--) if (hops[j].Address is not null) { prev = seg[j]; break; }
            for (int j = i + 1; j < seg.Length; j++) if (hops[j].Address is not null) { next = seg[j]; break; }
            seg[i] = prev == next ? prev : PathSegment.Unknown;
        }
        return seg;
    }

    private static readonly System.Text.RegularExpressions.Regex IxpRx = new(
        @"(^|[.\-])(ix|ixp|decix|de-cix|amsix|ams-ix|linx|nl-ix|franceix|equinix-ix|ptt)([.\-]|$)|\.ix\.br$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>DNS reverso típico de ponto de troca de tráfego (IX.br/PTT, DE-CIX, AMS-IX, LINX...).</summary>
    public static bool IsIxpHost(string? hostName) => !string.IsNullOrWhiteSpace(hostName) && IxpRx.IsMatch(hostName.TrimEnd('.'));

    public static string SegmentLabel(PathSegment s) => s switch
    {
        PathSegment.Router => "Rede local (PC → roteador)",
        PathSegment.Isp => "Provedor (ISP)",
        PathSegment.Remote => "Internet / trânsito",
        PathSegment.Destination => "Servidor de destino",
        _ => "Indeterminado",
    };
}
