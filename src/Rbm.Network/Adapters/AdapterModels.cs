namespace Rbm.Network.Adapters;

/// <summary>Tipo de adaptador (derivado de NdisPhysicalMedium + flags Virtual/HardwareInterface).</summary>
public enum AdapterKind { Unknown, Ethernet, WiFi, Cellular, Bluetooth, Virtual, Other }

/// <summary>Estado operacional resumido.</summary>
public enum AdapterStatus { Unknown, Up, Disconnected, Disabled, NotPresent }

/// <summary>Driver do adaptador. Campos nulos = não informado pelo sistema.</summary>
public sealed record AdapterDriverInfo(
    string? Description,
    string? FileName,
    string? Version,
    DateTime? Date,
    string? Provider,
    bool? IsSigned,
    string? Signer,
    string? InfName)
{
    public static readonly AdapterDriverInfo Unknown = new(null, null, null, null, null, null, null, null);

    /// <summary>Idade do driver em dias (pela data declarada no INF), se conhecida.</summary>
    public int? AgeDays(DateTime now) => Date is { } d ? (int)Math.Max(0, (now.Date - d.Date).TotalDays) : null;
}

/// <summary>Configuração IP de uma interface (endereços com prefixo, ex.: "192.168.0.10/24").</summary>
public sealed record AdapterIpConfig(
    IReadOnlyList<string> IPv4Addresses,
    IReadOnlyList<string> IPv6Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    bool? DhcpEnabled,
    IReadOnlyList<string> DhcpServers,
    int? MtuIPv4,
    int? MtuIPv6,
    string? DnsSuffix);

/// <summary>Contadores cumulativos do adaptador (desde a inicialização do driver). Compare duas leituras com <see cref="AdapterCounterDelta"/>.</summary>
public sealed record AdapterCounters(
    DateTimeOffset At,
    ulong? BytesReceived,
    ulong? BytesSent,
    ulong? UnicastPacketsReceived,
    ulong? UnicastPacketsSent,
    ulong? ReceivedErrors,
    ulong? ReceivedDiscards,
    ulong? OutboundErrors,
    ulong? OutboundDiscards,
    string Source);

/// <summary>Variação de contadores entre duas leituras.</summary>
public sealed record AdapterCounterDelta(
    TimeSpan Interval,
    long ReceivedErrors,
    long ReceivedDiscards,
    long OutboundErrors,
    long OutboundDiscards,
    long PacketsReceived,
    long PacketsSent,
    bool CounterReset)
{
    public long TotalErrorsAndDiscards => ReceivedErrors + ReceivedDiscards + OutboundErrors + OutboundDiscards;

    /// <summary>Calcula a diferença. Se algum contador diminuiu (driver reiniciado), marca <see cref="CounterReset"/> e zera.</summary>
    public static AdapterCounterDelta Compute(AdapterCounters before, AdapterCounters after)
    {
        bool reset = false;
        long D(ulong? a, ulong? b)
        {
            if (a is null || b is null) return 0;
            if (b.Value < a.Value) { reset = true; return 0; }
            return (long)Math.Min(b.Value - a.Value, long.MaxValue);
        }
        var d = new AdapterCounterDelta(after.At - before.At,
            D(before.ReceivedErrors, after.ReceivedErrors), D(before.ReceivedDiscards, after.ReceivedDiscards),
            D(before.OutboundErrors, after.OutboundErrors), D(before.OutboundDiscards, after.OutboundDiscards),
            D(before.UnicastPacketsReceived, after.UnicastPacketsReceived), D(before.UnicastPacketsSent, after.UnicastPacketsSent),
            false);
        return reset ? d with { CounterReset = true } : d;
    }
}

/// <summary>Detalhes da conexão Wi-Fi atual. <see cref="Available"/> = false traz o motivo em <see cref="UnavailableReason"/>.</summary>
public sealed record WifiDetails(
    bool Available,
    string? UnavailableReason,
    string? State,
    string? Ssid,
    string? Bssid,
    int? SignalQualityPercent,
    int? RssiDbm,
    int? Channel,
    double? FrequencyMHz,
    string? Band,
    string? PhyType,
    double? RxRateMbps,
    double? TxRateMbps,
    string? Authentication,
    string? Cipher,
    string? ProfileName,
    string Source)
{
    public static WifiDetails Unavailable(string reason, string source) =>
        new(false, reason, null, null, null, null, null, null, null, null, null, null, null, null, null, null, source);

    public bool IsConnected => Available && Ssid is not null;
}

/// <summary>Adaptador de rede com dados reais coletados (nunca estimados).</summary>
public sealed record NetworkAdapterInfo
{
    public required string InterfaceGuid { get; init; }
    public int? InterfaceIndex { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public AdapterKind Kind { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsHardware { get; init; }
    public AdapterStatus Status { get; init; }
    /// <summary>Estado em pt-BR para exibição.</summary>
    public string StatusText { get; init; } = "";
    /// <summary>Velocidade negociada do link (bps). Em adaptadores virtuais o valor é nominal e não significa nada.</summary>
    public long? LinkSpeedBps { get; init; }
    public long? ReceiveLinkSpeedBps { get; init; }
    public long? TransmitLinkSpeedBps { get; init; }
    public bool? FullDuplex { get; init; }
    public string? MacAddress { get; init; }
    public string? PnpDeviceId { get; init; }
    public int? MtuBytes { get; init; }
    public AdapterIpConfig? Ip { get; init; }
    public AdapterDriverInfo Driver { get; init; } = AdapterDriverInfo.Unknown;
    public AdapterCounters? Counters { get; init; }
    public WifiDetails? Wifi { get; init; }
    /// <summary>Interface usada pela rota padrão IPv4 (GetBestInterface para 8.8.8.8).</summary>
    public bool IsDefaultRouteInterface { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public string LinkSpeedText => LinkSpeedBps is { } s ? FormatSpeed(s) : "—";

    public static string FormatSpeed(long bps) => bps switch
    {
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.##} Gbps",
        >= 1_000_000 => $"{bps / 1_000_000.0:0.##} Mbps",
        >= 1_000 => $"{bps / 1_000.0:0.##} Kbps",
        _ => $"{bps} bps",
    };
}

/// <summary>Resultado da enumeração de adaptadores.</summary>
public sealed record NetworkInventory(
    DateTimeOffset At,
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    int? DefaultRouteInterfaceIndex,
    IReadOnlyList<string> Warnings)
{
    public NetworkAdapterInfo? DefaultAdapter => Adapters.FirstOrDefault(a => a.IsDefaultRouteInterface);
}
