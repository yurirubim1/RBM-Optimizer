using Rbm.Network.Latency;

namespace Rbm.Network.Path;

/// <summary>Escopo de um endereço IP.</summary>
public enum AddressScope { Unknown, Public, Private, Cgnat, LinkLocal, Loopback, Multicast }

/// <summary>Segmento do caminho para a separação "local / roteador / provedor / servidor remoto".</summary>
public enum PathSegment { Unknown = 0, Router = 1, Isp = 2, Remote = 3, Destination = 4 }

/// <summary>Um salto do traceroute. <see cref="RttsMs"/> tem uma entrada por sonda (null = sem resposta).</summary>
public sealed record TraceHop(int Ttl, string? Address, IReadOnlyList<string> Addresses, string? HostName, IReadOnlyList<double?> RttsMs, bool IsDestination)
{
    public bool Responded => Address is not null;
}

public sealed record TracerouteResult(string Target, string? DestinationAddress, bool Reached, IReadOnlyList<TraceHop> Hops, DateTimeOffset At, string? Error);

public sealed record TracerouteOptions
{
    public int MaxHops { get; init; } = 30;
    public int ProbesPerHop { get; init; } = 3;
    public TimeSpan PerHopTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public bool ResolveHostNames { get; init; } = true;
    public TimeSpan HostNameTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int PayloadBytes { get; init; } = 32;
}

/// <summary>Como interpretar perda/atraso observados em um salto.</summary>
public enum HopInterpretation
{
    /// <summary>Sem anomalia.</summary>
    Normal,
    /// <summary>O roteador não responde a ICMP/TTL excedido, mas saltos seguintes respondem — normal, não é problema.</summary>
    NoIcmpResponse,
    /// <summary>Perda só neste salto, que não se propaga: o roteador limita/despriorizou respostas ICMP. Não é perda real.</summary>
    IcmpRateLimitedLoss,
    /// <summary>Primeiro salto de uma perda que persiste até o fim: origem provável de perda real.</summary>
    RealLossOrigin,
    /// <summary>Perda herdada de um salto anterior (já contabilizada na origem).</summary>
    RealLossPropagated,
    /// <summary>Atraso só neste salto (saltos seguintes são mais rápidos): resposta lenta do plano de controle do roteador.</summary>
    ControlPlaneDelay,
}

public sealed record HopMeasurement(int Ttl, string? Address, string? HostName, LatencyStats Stats, bool IsDestination);

public sealed record AnalyzedHop(HopMeasurement Hop, AddressScope Scope, PathSegment Segment, HopInterpretation Interpretation, string NotePt);

/// <summary>
/// Resumo de um segmento. <see cref="MedianRttMs"/> é o RTT do salto representativo (último salto confiável do segmento);
/// <see cref="IncrementalMs"/> é quanto o segmento acrescenta em relação ao anterior.
/// </summary>
public sealed record SegmentSummary(
    PathSegment Segment,
    string LabelPt,
    int FromTtl,
    int ToTtl,
    int? RepresentativeTtl,
    double MedianRttMs,
    double IncrementalMs,
    double JitterMs,
    double LossPercent,
    bool LossIsReal,
    string NotePt);

public sealed record PathAnalysis(
    string Target,
    string? DestinationAddress,
    bool Reached,
    int Rounds,
    IReadOnlyList<AnalyzedHop> Hops,
    IReadOnlyList<SegmentSummary> Segments,
    PathSegment? RealLossOrigin,
    double EndToEndLossPercent,
    string VerdictPt,
    IReadOnlyList<string> NotesPt);

public sealed record PathAnalysisOptions
{
    public TracerouteOptions Trace { get; init; } = new();
    /// <summary>Rodadas de medição por salto após o traceroute (estilo mtr).</summary>
    public int Rounds { get; init; } = 20;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Quantos saltos públicos iniciais são atribuídos ao provedor (heurística sem ASN).</summary>
    public int IspPublicHops { get; init; } = 2;
    /// <summary>Perda mínima (%) considerada relevante.</summary>
    public double LossThresholdPercent { get; init; } = 2.0;
}
