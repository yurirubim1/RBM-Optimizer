using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Rbm.Network.Latency;

/// <summary>
/// Resultado bruto de uma sonda. <see cref="RttMs"/>: ICMP Success = RTT do Windows (ms inteiros); TTL excedido = tempo
/// medido localmente (o Windows informa 0 para respostas que não são Success). <see cref="ElapsedMs"/>: sempre o tempo
/// medido localmente com Stopwatch (inclui um pequeno overhead de agendamento, tipicamente &lt; 1 ms).
/// </summary>
public readonly record struct ProbeResult(ProbeStatus Status, double? RttMs, IPAddress? ReplyFrom, string? Detail, double? ElapsedMs = null);

/// <summary>Sondas ICMP e TCP. Não requerem elevação.</summary>
public static class Probes
{
    /// <summary>Valida um host informado pelo usuário (nome DNS, IPv4 ou IPv6).</summary>
    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 253 && Uri.CheckHostName(host.Trim()) != UriHostNameType.Unknown;

    /// <summary>Resolve o host (preferindo IPv4). Retorna null se falhar.</summary>
    public static async Task<IPAddress?> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Envia um eco ICMP (opcionalmente com TTL limitado, para traceroute).</summary>
    public static async Task<ProbeResult> IcmpAsync(Ping ping, IPAddress address, TimeSpan timeout, byte[] payload, int? ttl, CancellationToken ct)
    {
        try
        {
            var opts = ttl is { } t ? new PingOptions(t, dontFragment: false) : null;
            long t0 = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(address, timeout, payload, opts, ct).ConfigureAwait(false);
            double elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => new ProbeResult(ProbeStatus.Success, reply.RoundtripTime, reply.Address, null, elapsed),
                IPStatus.TtlExpired or IPStatus.TimeExceeded => new ProbeResult(ProbeStatus.TtlExpired, elapsed, reply.Address, null, elapsed),
                IPStatus.TimedOut => new ProbeResult(ProbeStatus.Timeout, null, null, null),
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
                    or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or IPStatus.NoResources
                    => new ProbeResult(ProbeStatus.Unreachable, null, ReplyAddress(reply), reply.Status.ToString()),
                _ => new ProbeResult(ProbeStatus.Error, null, ReplyAddress(reply), reply.Status.ToString()),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PingException ex) { return new ProbeResult(ProbeStatus.Error, null, null, ex.InnerException?.Message ?? ex.Message); }
        catch (Exception ex) { return new ProbeResult(ProbeStatus.Error, null, null, ex.Message); }
    }

    private static IPAddress? ReplyAddress(PingReply r)
    {
        try { return r.Address is { } a && !a.Equals(IPAddress.Any) ? a : null; } catch { return null; }
    }

    /// <summary>
    /// Latência de conexão TCP (handshake). Um RST (porta fechada) também é uma resposta válida do host e conta como RTT.
    /// Útil para hosts que descartam ICMP (muitos servidores de jogos).
    /// </summary>
    public static async Task<ProbeResult> TcpConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            sw.Stop();
            try { socket.Shutdown(SocketShutdown.Both); } catch { }
            return new ProbeResult(ProbeStatus.Success, sw.Elapsed.TotalMilliseconds, address, null, sw.Elapsed.TotalMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            sw.Stop();
            return new ProbeResult(ProbeStatus.Success, sw.Elapsed.TotalMilliseconds, address, "Porta fechada (RST): RTT válido.", sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(ProbeStatus.Timeout, null, null, null);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
        {
            return new ProbeResult(ProbeStatus.Unreachable, null, null, ex.SocketErrorCode.ToString());
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            return new ProbeResult(ProbeStatus.Timeout, null, null, null);
        }
        catch (SocketException ex)
        {
            return new ProbeResult(ProbeStatus.Error, null, null, ex.SocketErrorCode.ToString());
        }
    }
}
