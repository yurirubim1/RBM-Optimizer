namespace Rbm.Network.Latency;

public enum ProbeMethod { Icmp, Tcp }

/// <summary>Papel do alvo no diagnóstico (define a interpretação dos resultados).</summary>
public enum TargetRole { Gateway, IspFirstHop, PublicDns, Custom, PathHop }

public enum ProbeStatus { Success, Timeout, Unreachable, TtlExpired, ResolveFailed, Error }

/// <summary>Alvo de medição. <see cref="Id"/> identifica o alvo de forma estável (método + host + porta).</summary>
public sealed record LatencyTarget(string Host, string Label, TargetRole Role = TargetRole.Custom, ProbeMethod Method = ProbeMethod.Icmp, int TcpPort = 443)
{
    public string Id => Method == ProbeMethod.Tcp ? $"tcp:{Host}:{TcpPort}" : $"icmp:{Host}";
}

/// <summary>
/// Uma amostra. <see cref="RttMs"/> é nulo quando não houve resposta (perda/timeout/erro).
/// ICMP: RTT informado pelo Windows (resolução de 1 ms). TCP: tempo do handshake medido com Stopwatch (sub-ms).
/// </summary>
public sealed record LatencySample(
    string TargetId,
    string Label,
    int Sequence,
    DateTimeOffset At,
    ProbeStatus Status,
    double? RttMs,
    string? RemoteAddress,
    string? Detail = null)
{
    public bool Received => Status == ProbeStatus.Success && RttMs is not null;
}

/// <summary>
/// Estatísticas de um alvo. Valores NaN = sem respostas suficientes.
/// Jitter = média das diferenças absolutas entre RTTs consecutivos recebidos;
/// JitterRfc3550 = estimador suavizado J += (|D| − J)/16 (RFC 3550 §6.4.1) aplicado à sequência de RTTs.
/// </summary>
public sealed record LatencyStats(
    int Sent,
    int Received,
    int Lost,
    int Timeouts,
    int Errors,
    double LossPercent,
    double MinMs,
    double AvgMs,
    double MedianMs,
    double MaxMs,
    double P95Ms,
    double P99Ms,
    double StdDevMs,
    double JitterMs,
    double JitterRfc3550Ms,
    int MaxConsecutiveLost)
{
    public static readonly LatencyStats Empty = new(0, 0, 0, 0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);

    public bool HasData => Received > 0;
}

public sealed record TargetLatencyResult(LatencyTarget Target, string? ResolvedAddress, LatencyStats Stats, IReadOnlyList<LatencySample> Samples, string? Error);

/// <summary>Opções de teste. <see cref="Count"/> = null → contínuo até o cancelamento.</summary>
public sealed record LatencyTestOptions
{
    public required IReadOnlyList<LatencyTarget> Targets { get; init; }
    public int? Count { get; init; } = 20;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Bytes de payload ICMP (padrão do ping.exe = 32).</summary>
    public int PayloadBytes { get; init; } = 32;
    /// <summary>Janela de amostras mantidas por alvo para estatísticas em modo contínuo.</summary>
    public int MaxSamplesKept { get; init; } = 3600;
}

public sealed record LatencyTestResult(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<TargetLatencyResult> Targets,
    bool Cancelled)
{
    public const string ResolutionNote =
        "ICMP: o Windows informa o RTT em milissegundos inteiros (valores abaixo de 1 ms aparecem como 0). " +
        "TCP: mede o tempo de conexão (SYN → SYN/ACK ou RST), com resolução sub-milissegundo.";
}

/// <summary>Registro plano para persistência do histórico (o app grava; este módulo só produz).</summary>
public sealed record LatencyHistoryRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string TargetId,
    string Host,
    string Label,
    TargetRole Role,
    ProbeMethod Method,
    string? ResolvedAddress,
    string? AdapterName,
    string? ConnectionKind,
    int Sent,
    int Received,
    double LossPercent,
    double MinMs,
    double AvgMs,
    double MedianMs,
    double MaxMs,
    double P95Ms,
    double P99Ms,
    double JitterMs,
    double JitterRfc3550Ms)
{
    public static IReadOnlyList<LatencyHistoryRecord> From(LatencyTestResult r, string? adapterName = null, string? connectionKind = null) =>
        r.Targets.Select(t => new LatencyHistoryRecord(r.StartedAt, r.EndedAt, t.Target.Id, t.Target.Host, t.Target.Label, t.Target.Role,
            t.Target.Method, t.ResolvedAddress, adapterName, connectionKind, t.Stats.Sent, t.Stats.Received, t.Stats.LossPercent,
            t.Stats.MinMs, t.Stats.AvgMs, t.Stats.MedianMs, t.Stats.MaxMs, t.Stats.P95Ms, t.Stats.P99Ms, t.Stats.JitterMs, t.Stats.JitterRfc3550Ms)).ToList();
}
