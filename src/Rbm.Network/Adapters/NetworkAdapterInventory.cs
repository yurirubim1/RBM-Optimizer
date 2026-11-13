using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Rbm.Network.Native;

namespace Rbm.Network.Adapters;

/// <summary>Opções de coleta de adaptadores.</summary>
public sealed record AdapterQueryOptions(bool IncludeWifi = true, bool IncludeCounters = true, bool IncludeDriverSignature = true);

/// <summary>
/// Enumera adaptadores ativos e inativos combinando MSFT_NetAdapter (root\StandardCimv2), System.Net.NetworkInformation,
/// Win32_PnPSignedDriver e a Native Wifi API. Não requer elevação. Seguro para chamar de threads de fundo.
/// </summary>
public static class NetworkAdapterInventory
{
    public static Task<NetworkInventory> EnumerateAsync(AdapterQueryOptions? options = null, CancellationToken ct = default) =>
        EnumerateCoreAsync(options ?? new AdapterQueryOptions(), ct);

    private static async Task<NetworkInventory> EnumerateCoreAsync(AdapterQueryOptions o, CancellationToken ct)
    {
        var warnings = new List<string>();
        var wifiTask = o.IncludeWifi ? WifiInspector.QueryAllAsync(ct) : Task.FromResult(new WifiQueryResult(new Dictionary<string, WifiDetails>(), null));

        // Consultas independentes em paralelo (Win32_PnPSignedDriver é a mais lenta: segundos).
        var aTask = Task.Run(() => Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapter", ct), ct);
        Task<Wmi.QueryResult?> cTask = o.IncludeCounters
            ? Task.Run<Wmi.QueryResult?>(() => Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterStatisticsSettingData", ct), ct)
            : Task.FromResult<Wmi.QueryResult?>(null);
        Task<Wmi.QueryResult?> sTask = o.IncludeDriverSignature
            ? Task.Run<Wmi.QueryResult?>(() => Wmi.Query(Wmi.Cimv2, "SELECT DeviceID, IsSigned, Signer, InfName FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'", ct), ct)
            : Task.FromResult<Wmi.QueryResult?>(null);
        var niById = SafeInterfaces().GroupBy(n => NormGuid(n.Id) ?? n.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int? bestIdx = IpHelper.BestInterfaceIndex(IPAddress.Parse("8.8.8.8"));
        var adapters = await aTask.ConfigureAwait(false);
        Wmi.QueryResult? counters = await cTask.ConfigureAwait(false);
        Wmi.QueryResult? signed = await sTask.ConfigureAwait(false);

        var wifi = await wifiTask.ConfigureAwait(false);
        if (adapters.Error is not null) warnings.Add($"MSFT_NetAdapter indisponível: {adapters.Error}. Usando apenas System.Net.NetworkInformation.");
        if (counters?.Error is not null) warnings.Add($"Estatísticas do driver indisponíveis: {counters.Error}. Usando contadores IP.");
        if (signed?.Error is not null) warnings.Add($"Assinatura de drivers indisponível: {signed.Error}");

        var list = new List<NetworkAdapterInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in adapters.Rows)
        {
            ct.ThrowIfCancellationRequested();
            var guid = NormGuid(r.Str("InterfaceGuid"));
            if (guid is null) continue;
            seen.Add(guid);
            niById.TryGetValue(guid, out var ni);
            list.Add(FromCim(r, guid, ni, counters, signed, wifi, bestIdx));
        }

        // Fallback somente se a consulta CIM falhou: MSFT_NetAdapter já omite, de propósito, as interfaces ocultas
        // (filtros WFP/QoS, WAN Miniport, depurador de kernel), que não são adaptadores reais.
        if (adapters.Error is not null)
        {
            foreach (var (guid, ni) in niById)
            {
                if (seen.Contains(guid) || IsPseudoInterface(ni)) continue;
                list.Add(FromNetworkInterface(guid, ni, wifi, bestIdx));
            }
        }

        if (wifi.Error is not null && list.Any(a => a.Kind == AdapterKind.WiFi))
            warnings.Add($"Wi-Fi: {wifi.Error}");

        var ordered = list
            .OrderByDescending(a => a.IsDefaultRouteInterface)
            .ThenBy(a => a.Status == AdapterStatus.Up ? 0 : 1)
            .ThenBy(a => a.IsVirtual)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new NetworkInventory(DateTimeOffset.Now, ordered, bestIdx, warnings);
    }

    /// <summary>Leitura rápida apenas dos contadores (para calcular variação de erros/descartes entre duas leituras).</summary>
    public static Task<IReadOnlyDictionary<string, AdapterCounters>> ReadCountersAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var res = new Dictionary<string, AdapterCounters>(StringComparer.OrdinalIgnoreCase);
        var visible = Wmi.Query(Wmi.StandardCimv2, "SELECT InterfaceGuid FROM MSFT_NetAdapter", ct);
        var keep = visible.Rows.Select(r => NormGuid(r.Str("InterfaceGuid"))).Where(g => g is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var c = Wmi.Query(Wmi.StandardCimv2, "SELECT * FROM MSFT_NetAdapterStatisticsSettingData", ct);
        foreach (var r in c.Rows)
        {
            var guid = InstanceGuid(r);
            if (guid is not null && (visible.Error is not null || keep.Contains(guid))) res[guid] = CountersFromCim(r);
        }
        foreach (var ni in SafeInterfaces())
        {
            var guid = NormGuid(ni.Id);
            if (guid is null || res.ContainsKey(guid) || IsPseudoInterface(ni)) continue;
            if (visible.Error is null && !keep.Contains(guid)) continue;
            var cn = CountersFromNi(ni);
            if (cn is not null) res[guid] = cn;
        }
        return (IReadOnlyDictionary<string, AdapterCounters>)res;
    }, ct);

    // ------------------------------------------------------------------

    private static NetworkAdapterInfo FromCim(IReadOnlyDictionary<string, object?> r, string guid, NetworkInterface? ni,
        Wmi.QueryResult? counters, Wmi.QueryResult? signed, WifiQueryResult wifi, int? bestIdx)
    {
        var notes = new List<string>();
        bool isVirtual = r.Bool("Virtual") ?? false;
        bool isHw = r.Bool("HardwareInterface") ?? false;
        long? medium = r.Long("NdisPhysicalMedium");
        var kind = ClassifyKind(isVirtual, medium, r.Str("InterfaceDescription"));
        var status = MapStatus(r.Long("State"), r.Long("InterfaceOperationalStatus"), r.Long("MediaConnectState"));
        int? idx = (int?)r.Long("InterfaceIndex");

        long? speed = status == AdapterStatus.Up ? PositiveOrNull(r.Long("Speed")) : null;
        long? rx = status == AdapterStatus.Up ? PositiveOrNull(r.Long("ReceiveLinkSpeed")) : null;
        long? tx = status == AdapterStatus.Up ? PositiveOrNull(r.Long("TransmitLinkSpeed")) : null;
        if (isVirtual && speed is not null) notes.Add("Velocidade de adaptador virtual é nominal (não representa um enlace físico).");

        string? pnp = r.Str("PnPDeviceID");
        var sigRow = signed?.Rows.FirstOrDefault(s => string.Equals(s.Str("DeviceID"), pnp, StringComparison.OrdinalIgnoreCase));
        var driver = new AdapterDriverInfo(
            r.Str("DriverDescription"), r.Str("DriverName"), r.Str("DriverVersionString"), ParseDriverDate(r.Str("DriverDate")),
            r.Str("DriverProvider"), sigRow?.Bool("IsSigned"), sigRow?.Str("Signer"), sigRow?.Str("InfName"));

        AdapterCounters? cnt = null;
        var cRow = counters?.Rows.FirstOrDefault(c => string.Equals(InstanceGuid(c), guid, StringComparison.OrdinalIgnoreCase));
        if (cRow is not null) cnt = CountersFromCim(cRow);
        else if (ni is not null) cnt = CountersFromNi(ni);

        string? mac = FormatMac(r.StrArray("NetworkAddresses").FirstOrDefault() ?? r.Str("PermanentAddress"));

        return new NetworkAdapterInfo
        {
            InterfaceGuid = guid,
            InterfaceIndex = idx,
            Name = r.Str("Name") ?? ni?.Name ?? guid,
            Description = r.Str("InterfaceDescription") ?? ni?.Description ?? "",
            Kind = kind,
            IsVirtual = isVirtual,
            IsHardware = isHw,
            Status = status,
            StatusText = StatusText(status),
            LinkSpeedBps = speed,
            ReceiveLinkSpeedBps = rx,
            TransmitLinkSpeedBps = tx,
            FullDuplex = status == AdapterStatus.Up ? r.Bool("FullDuplex") : null,
            MacAddress = mac,
            PnpDeviceId = pnp,
            MtuBytes = (int?)r.Long("MtuSize"),
            Ip = ni is not null ? ReadIp(ni) : null,
            Driver = driver,
            Counters = cnt,
            Wifi = kind == AdapterKind.WiFi ? WifiFor(guid, wifi) : null,
            IsDefaultRouteInterface = bestIdx is not null && idx == bestIdx,
            Notes = notes,
        };
    }

    private static NetworkAdapterInfo FromNetworkInterface(string guid, NetworkInterface ni, WifiQueryResult wifi, int? bestIdx)
    {
        var kind = ni.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => AdapterKind.Ethernet,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Cellular,
            _ => AdapterKind.Other,
        };
        var status = ni.OperationalStatus switch
        {
            OperationalStatus.Up => AdapterStatus.Up,
            OperationalStatus.Down or OperationalStatus.LowerLayerDown or OperationalStatus.Dormant => AdapterStatus.Disconnected,
            OperationalStatus.NotPresent => AdapterStatus.NotPresent,
            _ => AdapterStatus.Unknown,
        };
        int? idx = null;
        try { idx = ni.GetIPProperties().GetIPv4Properties()?.Index; } catch { }
        return new NetworkAdapterInfo
        {
            InterfaceGuid = guid,
            InterfaceIndex = idx,
            Name = ni.Name,
            Description = ni.Description,
            Kind = kind,
            Status = status,
            StatusText = StatusText(status),
            LinkSpeedBps = status == AdapterStatus.Up && ni.Speed > 0 ? ni.Speed : null,
            MacAddress = FormatMac(SafeMac(ni)),
            Ip = ReadIp(ni),
            Counters = CountersFromNi(ni),
            Wifi = kind == AdapterKind.WiFi ? WifiFor(guid, wifi) : null,
            IsDefaultRouteInterface = bestIdx is not null && idx == bestIdx,
            Notes = new[] { "Dados de driver/enlace indisponíveis (interface fora de MSFT_NetAdapter)." },
        };
    }

    private static WifiDetails WifiFor(string guid, WifiQueryResult wifi) =>
        wifi.ByInterfaceGuid.TryGetValue(guid, out var w) ? w
        : WifiDetails.Unavailable(wifi.Error ?? "Interface não encontrada na API de Wi-Fi.", "wlanapi");

    internal static AdapterKind ClassifyKind(bool isVirtual, long? ndisPhysicalMedium, string? description)
    {
        if (ndisPhysicalMedium == 9 && !isVirtual) return AdapterKind.WiFi;
        if (isVirtual) return AdapterKind.Virtual;
        return ndisPhysicalMedium switch
        {
            9 or 1 => AdapterKind.WiFi,
            14 => AdapterKind.Ethernet,
            8 or 12 => AdapterKind.Cellular,
            10 => AdapterKind.Bluetooth,
            0 => AdapterKind.Virtual,
            null => AdapterKind.Unknown,
            _ => AdapterKind.Other,
        };
    }

    internal static AdapterStatus MapStatus(long? state, long? operStatus, long? mediaConnect)
    {
        if (state == 3) return AdapterStatus.Disabled;
        if (operStatus == 6) return AdapterStatus.NotPresent;
        if (operStatus == 1 && mediaConnect != 2) return AdapterStatus.Up;
        if (mediaConnect == 2 || operStatus is 2 or 7 or 5) return AdapterStatus.Disconnected;
        return AdapterStatus.Unknown;
    }

    public static string StatusText(AdapterStatus s) => s switch
    {
        AdapterStatus.Up => "Conectado",
        AdapterStatus.Disconnected => "Desconectado (cabo/rede ausente)",
        AdapterStatus.Disabled => "Desabilitado",
        AdapterStatus.NotPresent => "Não presente",
        _ => "Desconhecido",
    };

    private static AdapterIpConfig? ReadIp(NetworkInterface ni)
    {
        try
        {
            var p = ni.GetIPProperties();
            var v4 = new List<string>(); var v6 = new List<string>();
            foreach (var u in p.UnicastAddresses)
            {
                var s = $"{u.Address}/{u.PrefixLength}";
                if (u.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(s);
                else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(s);
            }
            var gws = p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Select(a => a.ToString()).ToList();
            // fec0:0:0:ffff::1..3 são padrões obsoletos (site-local) que o Windows lista quando não há DNS IPv6 real.
            var dns = p.DnsAddresses.Where(a => !IsDeprecatedSiteLocalDns(a)).Select(a => a.ToString()).ToList();
            IPv4InterfaceProperties? p4 = null; IPv6InterfaceProperties? p6 = null;
            try { p4 = p.GetIPv4Properties(); } catch { }
            try { p6 = p.GetIPv6Properties(); } catch { }
            var dhcp = p.DhcpServerAddresses.Select(a => a.ToString()).ToList();
            return new AdapterIpConfig(v4, v6, gws, dns, p4?.IsDhcpEnabled, dhcp, p4?.Mtu, p6?.Mtu, string.IsNullOrEmpty(p.DnsSuffix) ? null : p.DnsSuffix);
        }
        catch { return null; }
    }

    internal static bool IsDeprecatedSiteLocalDns(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var b = a.GetAddressBytes();
        return b[0] == 0xfe && (b[1] & 0xc0) == 0xc0;
    }

    private static AdapterCounters CountersFromCim(IReadOnlyDictionary<string, object?> r) => new(
        DateTimeOffset.Now, r.ULong("ReceivedBytes"), r.ULong("SentBytes"), r.ULong("ReceivedUnicastPackets"), r.ULong("SentUnicastPackets"),
        r.ULong("ReceivedPacketErrors"), r.ULong("ReceivedDiscardedPackets"), r.ULong("OutboundPacketErrors"), r.ULong("OutboundDiscardedPackets"),
        "MSFT_NetAdapterStatisticsSettingData");

    private static AdapterCounters? CountersFromNi(NetworkInterface ni)
    {
        try
        {
            var s = ni.GetIPStatistics();
            return new AdapterCounters(DateTimeOffset.Now, (ulong)s.BytesReceived, (ulong)s.BytesSent, (ulong)s.UnicastPacketsReceived,
                (ulong)s.UnicastPacketsSent, (ulong)s.IncomingPacketsWithErrors, (ulong)s.IncomingPacketsDiscarded,
                (ulong)s.OutgoingPacketsWithErrors, (ulong)s.OutgoingPacketsDiscarded, "IPInterfaceStatistics");
        }
        catch { return null; }
    }

    private static string? InstanceGuid(IReadOnlyDictionary<string, object?> r)
    {
        var id = r.Str("InstanceID") ?? r.Str("InterfaceGuid");
        if (id is null) return null;
        int sep = id.IndexOf("::", StringComparison.Ordinal);
        return NormGuid(sep > 0 ? id[..sep] : id);
    }

    internal static string? NormGuid(string? g) => g is not null && Guid.TryParse(g, out var x) ? x.ToString("B").ToUpperInvariant() : null;

    private static long? PositiveOrNull(long? v) => v is > 0 and < long.MaxValue ? v : null;

    internal static DateTime? ParseDriverDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string[] fmts = { "yyyy-MM-dd", "yyyy-M-d", "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy", "yyyyMMdd" };
        if (DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    private static string? SafeMac(NetworkInterface ni)
    {
        try { return ni.GetPhysicalAddress().ToString(); } catch { return null; }
    }

    internal static string? FormatMac(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var clean = new string(hex.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (clean.Length != 12) return hex;
        return string.Join("-", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
    }

    /// <summary>Loopback, túneis, WAN Miniport e instâncias de filtro NDIS ("…-0000") não são adaptadores reais.</summary>
    internal static bool IsPseudoInterface(NetworkInterface ni) =>
        ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || IsPseudoDescription(ni.Description);

    internal static bool IsPseudoDescription(string? description) =>
        description is not null &&
        (description.EndsWith("-0000", StringComparison.Ordinal) || description.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase)
         || description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces(); } catch { return Array.Empty<NetworkInterface>(); }
    }
}
