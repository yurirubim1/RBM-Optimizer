using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Rbm.Network.Native;

/// <summary>Consultas de roteamento via iphlpapi (sem elevação).</summary>
public static partial class IpHelper
{
    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    /// <summary>Índice da interface que o Windows usaria para alcançar <paramref name="destination"/> (IPv4).</summary>
    public static int? BestInterfaceIndex(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork) return null;
#pragma warning disable CS0618 // Address (uint em ordem de rede) é exatamente o que a API espera.
        uint addr = (uint)destination.Address;
#pragma warning restore CS0618
        return GetBestInterface(addr, out uint idx) == 0 ? (int)idx : null;
    }

    /// <summary>
    /// Gateway padrão IPv4 da interface da rota padrão. Retorna null se não houver rota padrão.
    /// </summary>
    public static (IPAddress Gateway, NetworkInterface Interface)? DefaultGatewayIPv4()
    {
        int? best = BestInterfaceIndex(IPAddress.Parse("8.8.8.8"));
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return null; }

        (IPAddress, NetworkInterface)? fallback = null;
        foreach (var ni in all)
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            var gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is null) continue;
            int? idx = null;
            try { idx = props.GetIPv4Properties()?.Index; } catch { }
            if (best is not null && idx == best) return (gw, ni);
            fallback ??= (gw, ni);
        }
        return fallback;
    }
}
