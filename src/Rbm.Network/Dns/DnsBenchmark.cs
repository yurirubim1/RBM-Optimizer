using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Rbm.Core;

namespace Rbm.Network.Dns;

public enum DnsServerSource { Configured, Public, Custom, SystemResolver }

public sealed record DnsServerEndpoint(string Address, string Label, DnsServerSource Source);

public enum DnsQueryOutcome { Answer, NoData, NxDomain, ServerFailure, Refused, OtherError, Timeout, Malformed, NetworkError }

/// <summary>Resultado de uma consulta. <see cref="CachedQuery"/> = repetição imediata (deve vir do cache do resolvedor).</summary>
public sealed record DnsQueryResult(
    string Name,
    bool CachedQuery,
    DnsQueryOutcome Outcome,
    double? ElapsedMs,
    string RCode,
    bool Truncated,
    IReadOnlyList<string> Addresses,
    string? Detail)
{
    /// <summary>O servidor respondeu de forma útil (tempo entra nas estatísticas).</summary>
    public bool Responded => Outcome is DnsQueryOutcome.Answer or DnsQueryOutcome.NoData or DnsQueryOutcome.NxDomain;
}

/// <summary>
/// Resultado por servidor. <see cref="FirstQuery"/> = primeira consulta de cada nome (pode já estar no cache do resolvedor
/// se o domínio for popular); <see cref="CachedQuery"/> = repetição imediata (tempo de ida e volta + cache).
/// </summary>
public sealed record DnsServerBenchmark(
    DnsServerEndpoint Server,
    StatSummary FirstQuery,
    StatSummary CachedQuery,
    int Queries,
    int Responded,
    int Timeouts,
    int Failures,
    double SuccessPercent,
    IReadOnlyList<DnsQueryResult> Results);

public sealed record DnsBenchmarkResult(
    DateTimeOffset At,
    IReadOnlyList<DnsServerBenchmark> Servers,
    DnsServerBenchmark? SystemResolver,
    IReadOnlyList<string> NotesPt)
{
    /// <summary>Servidores ordenados pela mediana da primeira consulta (somente os que responderam ≥ 80%).</summary>
    public IReadOnlyList<DnsServerBenchmark> Ranking => Servers
        .Where(s => s.SuccessPercent >= 80 && s.FirstQuery.Count > 0)
        .OrderBy(s => s.FirstQuery.Median).ToList();
}

public sealed record DnsBenchmarkOptions
{
    public IReadOnlyList<string> Names { get; init; } = DnsBenchmark.DefaultNames;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public bool IncludeConfigured { get; init; } = true;
    public bool IncludePublic { get; init; } = true;
    public bool IncludeSystemResolver { get; init; } = true;
    /// <summary>IPs de servidores adicionais (somente IP literal).</summary>
    public IReadOnlyList<string> CustomServers { get; init; } = Array.Empty<string>();
    public int MaxParallelServers { get; init; } = 4;
}

/// <summary>
/// Mede o tempo de resolução DNS por servidor usando consultas UDP montadas manualmente (ID aleatório, registro A).
/// Não altera nenhuma configuração de DNS — apenas compara. Sem elevação.
/// </summary>
public static class DnsBenchmark
{
    public static readonly IReadOnlyList<string> DefaultNames = new[]
    {
        "google.com", "youtube.com", "wikipedia.org", "microsoft.com", "amazon.com", "netflix.com", "cloudflare.com",
        "github.com", "discord.com", "steampowered.com", "riotgames.com", "epicgames.com", "globo.com", "mercadolivre.com.br",
    };

    public static readonly IReadOnlyList<DnsServerEndpoint> PublicResolvers = new[]
    {
        new DnsServerEndpoint("1.1.1.1", "Cloudflare", DnsServerSource.Public),
        new DnsServerEndpoint("8.8.8.8", "Google", DnsServerSource.Public),
        new DnsServerEndpoint("9.9.9.9", "Quad9", DnsServerSource.Public),
        new DnsServerEndpoint("208.67.222.222", "OpenDNS", DnsServerSource.Public),
    };

    public static async Task<DnsBenchmarkResult> RunAsync(DnsBenchmarkOptions? options = null, IProgress<DnsQueryResult>? progress = null, CancellationToken ct = default)
    {
        var o = options ?? new DnsBenchmarkOptions();
        var notes = new List<string>
        {
            "A primeira consulta de cada nome pode já estar no cache do resolvedor (domínios populares quase sempre estão nos grandes resolvedores públicos); a repetição mede o cache.",
            "Os tempos incluem a latência de rede até o servidor. Nenhuma configuração de DNS foi alterada.",
        };
        var names = o.Names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        foreach (var n in names.ToList())
        {
            try { DnsPacket.EncodeName(n); }
            catch (ArgumentException) { names.Remove(n); notes.Add($"Nome ignorado (inválido): {n}"); }
        }
        if (names.Count == 0) throw new ArgumentException("Nenhum nome válido para consultar.", nameof(options));

        var servers = new List<DnsServerEndpoint>();
        if (o.IncludeConfigured) servers.AddRange(ConfiguredServers());
        if (o.IncludePublic) servers.AddRange(PublicResolvers);
        foreach (var s in o.CustomServers)
        {
            if (IPAddress.TryParse(s?.Trim(), out var ip)) servers.Add(new DnsServerEndpoint(ip.ToString(), $"Personalizado ({ip})", DnsServerSource.Custom));
            else notes.Add($"Servidor ignorado (precisa ser um IP): {s}");
        }
        servers = servers.GroupBy(s => s.Address, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (o.IncludeConfigured && !servers.Any(s => s.Source == DnsServerSource.Configured))
            notes.Add("Nenhum servidor DNS configurado encontrado nas interfaces ativas.");

        using var gate = new SemaphoreSlim(Math.Clamp(o.MaxParallelServers, 1, 16));
        var tasks = servers.Select(async s =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await BenchServerAsync(s, names, o.Timeout, progress, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        DnsServerBenchmark? sys = null;
        if (o.IncludeSystemResolver)
        {
            sys = await BenchSystemResolverAsync(names, o.Timeout, progress, ct).ConfigureAwait(false);
            notes.Add("Resolvedor do sistema: inclui o cache do serviço Cliente DNS do Windows; a repetição normalmente vem do cache local (~0 ms).");
        }
        if (results.Any(r => r.Results.Any(q => q.Truncated)))
            notes.Add("Algumas respostas vieram truncadas (bit TC): o tempo foi medido até a resposta UDP; a repetição via TCP não foi feita.");
        return new DnsBenchmarkResult(DateTimeOffset.Now, results, sys, notes);
    }

    /// <summary>Servidores DNS das interfaces ativas com gateway (exclui os padrões obsoletos fec0::/10).</summary>
    public static IReadOnlyList<DnsServerEndpoint> ConfiguredServers()
    {
        var list = new List<DnsServerEndpoint>();
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
        foreach (var ni in all)
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties p;
            try { p = ni.GetIPProperties(); } catch { continue; }
            if (!p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any))) continue;
            foreach (var d in p.DnsAddresses)
            {
                if (Adapters.NetworkAdapterInventory.IsDeprecatedSiteLocalDns(d)) continue;
                var a = d.ToString();
                if (!list.Any(x => x.Address == a)) list.Add(new DnsServerEndpoint(a, $"Configurado ({ni.Name})", DnsServerSource.Configured));
            }
        }
        return list;
    }

    private static async Task<DnsServerBenchmark> BenchServerAsync(DnsServerEndpoint server, IReadOnlyList<string> names, TimeSpan timeout,
        IProgress<DnsQueryResult>? progress, CancellationToken ct)
    {
        var ip = IPAddress.Parse(server.Address);
        var results = new List<DnsQueryResult>(names.Count * 2);
        foreach (var name in names)
        {
            var first = await QueryUdpAsync(ip, name, false, timeout, ct).ConfigureAwait(false);
            results.Add(first); progress?.Report(first);
            var cached = await QueryUdpAsync(ip, name, true, timeout, ct).ConfigureAwait(false);
            results.Add(cached); progress?.Report(cached);
        }
        return Summarize(server, results);
    }

    internal static DnsServerBenchmark Summarize(DnsServerEndpoint server, IReadOnlyList<DnsQueryResult> results)
    {
        var firstTimes = results.Where(r => !r.CachedQuery && r.Responded && r.ElapsedMs is not null).Select(r => r.ElapsedMs!.Value).ToList();
        var cachedTimes = results.Where(r => r.CachedQuery && r.Responded && r.ElapsedMs is not null).Select(r => r.ElapsedMs!.Value).ToList();
        int responded = results.Count(r => r.Responded);
        int timeouts = results.Count(r => r.Outcome == DnsQueryOutcome.Timeout);
        return new DnsServerBenchmark(server, Stats.Summarize(firstTimes), Stats.Summarize(cachedTimes), results.Count, responded, timeouts,
            results.Count - responded - timeouts, results.Count == 0 ? 0 : responded * 100.0 / results.Count, results);
    }

    /// <summary>Uma consulta A via UDP/53 com ID aleatório. Ignora pacotes que não correspondem (ID/pergunta).</summary>
    public static async Task<DnsQueryResult> QueryUdpAsync(IPAddress server, string name, bool cachedQuery, TimeSpan timeout, CancellationToken ct)
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        byte[] query = DnsPacket.BuildQuery(id, name, DnsType.A);
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var buffer = new byte[4096];
        string? lastError = null;
        var sw = new Stopwatch();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(server, 53), cts.Token).ConfigureAwait(false); // socket conectado: só aceita pacotes deste servidor
            sw.Start();
            await socket.SendAsync(query, SocketFlags.None, cts.Token).ConfigureAwait(false);
            while (true)
            {
                int n = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false);
                double elapsed = sw.Elapsed.TotalMilliseconds;
                if (!DnsPacket.TryParse(buffer.AsSpan(0, n), out var msg, out var err) || msg is null) { lastError = err; continue; }
                if (!IsMatchingResponse(msg, id, name)) continue;
                return ToResult(name, cachedQuery, msg, elapsed);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DnsQueryResult(name, cachedQuery, lastError is null ? DnsQueryOutcome.Timeout : DnsQueryOutcome.Malformed, null, "—", false,
                Array.Empty<string>(), lastError ?? $"Sem resposta em {timeout.TotalMilliseconds:0} ms.");
        }
        catch (SocketException ex)
        {
            string detail = ex.SocketErrorCode == SocketError.ConnectionReset ? "Porta 53 inacessível (ICMP port unreachable)." : ex.SocketErrorCode.ToString();
            return new DnsQueryResult(name, cachedQuery, DnsQueryOutcome.NetworkError, null, "—", false, Array.Empty<string>(), detail);
        }
    }

    /// <summary>Confere ID, bit QR e a pergunta ecoada (proteção contra respostas trocadas/forjadas).</summary>
    public static bool IsMatchingResponse(DnsMessage msg, ushort id, string name)
    {
        if (msg.Id != id || !msg.IsResponse) return false;
        if (msg.Questions.Count == 0) return msg.ResponseCode != 0; // alguns erros omitem a pergunta
        string expected;
        try { expected = new System.Globalization.IdnMapping().GetAscii(name.Trim().TrimEnd('.')); } catch { expected = name; }
        var q = msg.Questions[0];
        return string.Equals(q.Name.TrimEnd('.'), expected, StringComparison.OrdinalIgnoreCase) && q.Type == DnsType.A;
    }

    public static DnsQueryResult ToResult(string name, bool cachedQuery, DnsMessage msg, double elapsedMs)
    {
        var addrs = msg.Answers.Where(a => a.Type == DnsType.A).Select(a => a.Data).ToList();
        var outcome = msg.ResponseCode switch
        {
            0 => addrs.Count > 0 ? DnsQueryOutcome.Answer : DnsQueryOutcome.NoData,
            3 => DnsQueryOutcome.NxDomain,
            2 => DnsQueryOutcome.ServerFailure,
            5 => DnsQueryOutcome.Refused,
            _ => DnsQueryOutcome.OtherError,
        };
        // Truncada sem endereços utilizáveis ainda conta como resposta do servidor (tempo válido).
        if (msg.Truncated && outcome == DnsQueryOutcome.NoData && msg.ResponseCode == 0) outcome = DnsQueryOutcome.Answer;
        return new DnsQueryResult(name, cachedQuery, outcome, elapsedMs, DnsPacket.RCodeName(msg.ResponseCode), msg.Truncated, addrs,
            msg.Truncated ? "Resposta truncada (TC)." : null);
    }

    private static async Task<DnsServerBenchmark> BenchSystemResolverAsync(IReadOnlyList<string> names, TimeSpan timeout, IProgress<DnsQueryResult>? progress, CancellationToken ct)
    {
        var results = new List<DnsQueryResult>();
        foreach (var name in names)
        {
            foreach (bool cached in new[] { false, true })
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var sw = Stopwatch.StartNew();
                DnsQueryResult r;
                try
                {
                    var a = await System.Net.Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, cts.Token).ConfigureAwait(false);
                    r = new DnsQueryResult(name, cached, a.Length > 0 ? DnsQueryOutcome.Answer : DnsQueryOutcome.NoData, sw.Elapsed.TotalMilliseconds,
                        "NOERROR", false, a.Select(x => x.ToString()).ToList(), null);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    r = new DnsQueryResult(name, cached, DnsQueryOutcome.Timeout, null, "—", false, Array.Empty<string>(), "Tempo esgotado.");
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound)
                {
                    r = new DnsQueryResult(name, cached, DnsQueryOutcome.NxDomain, sw.Elapsed.TotalMilliseconds, "NXDOMAIN", false, Array.Empty<string>(), null);
                }
                catch (SocketException ex)
                {
                    r = new DnsQueryResult(name, cached, DnsQueryOutcome.NetworkError, null, "—", false, Array.Empty<string>(), ex.SocketErrorCode.ToString());
                }
                results.Add(r); progress?.Report(r);
            }
        }
        return Summarize(new DnsServerEndpoint("sistema", "Resolvedor do sistema (Windows)", DnsServerSource.SystemResolver), results);
    }
}
