using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Rbm.Network.Latency;

/// <summary>
/// Testes de latência concorrentes (um laço por alvo). Modo finito (<see cref="RunAsync"/>) ou contínuo
/// (<see cref="StreamAsync"/> com Count = null). Seguro para chamar de threads de fundo; sem elevação.
/// </summary>
public static class LatencyTester
{
    /// <summary>Executa o teste e devolve estatísticas por alvo. Cancelamento devolve resultados parciais (Cancelled = true).</summary>
    public static async Task<LatencyTestResult> RunAsync(LatencyTestOptions options, IProgress<LatencySample>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var acc = new LatencyAccumulator(options.Targets, options.MaxSamplesKept);
        bool cancelled = false;
        try
        {
            await foreach (var s in StreamCoreAsync(options, acc, ct).ConfigureAwait(false))
                progress?.Report(s);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        return new LatencyTestResult(started, DateTimeOffset.Now, acc.Snapshot(), cancelled);
    }

    /// <summary>
    /// Fluxo de amostras de todos os alvos (intercaladas). Com Count = null roda até o cancelamento.
    /// Para estatísticas em tempo real, alimente um <see cref="LatencyAccumulator"/> com as amostras.
    /// </summary>
    public static IAsyncEnumerable<LatencySample> StreamAsync(LatencyTestOptions options, CancellationToken ct = default) =>
        StreamCoreAsync(options, null, ct);

    private static async IAsyncEnumerable<LatencySample> StreamCoreAsync(LatencyTestOptions options, LatencyAccumulator? acc,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Validate(options);
        var channel = Channel.CreateUnbounded<LatencySample>(new UnboundedChannelOptions { SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loops = options.Targets.Select(t => Task.Run(() => TargetLoopAsync(t, options, channel.Writer, acc, linked.Token))).ToArray();
        _ = Task.WhenAll(loops).ContinueWith(t => channel.Writer.TryComplete(t.Exception?.GetBaseException()), TaskScheduler.Default);

        try
        {
            await foreach (var s in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return s;
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(loops).ConfigureAwait(false); } catch { }
        }
    }

    private static void Validate(LatencyTestOptions o)
    {
        if (o.Targets.Count == 0) throw new ArgumentException("Nenhum alvo informado.", nameof(o));
        if (o.Targets.Count > 64) throw new ArgumentException("Máximo de 64 alvos simultâneos.", nameof(o));
        if (o.Count is <= 0) throw new ArgumentException("Count deve ser positivo ou null (contínuo).", nameof(o));
        if (o.Interval < TimeSpan.FromMilliseconds(100)) throw new ArgumentException("Intervalo mínimo: 100 ms.", nameof(o));
        if (o.Timeout < TimeSpan.FromMilliseconds(100) || o.Timeout > TimeSpan.FromSeconds(10)) throw new ArgumentException("Timeout deve estar entre 100 ms e 10 s.", nameof(o));
        if (o.PayloadBytes is < 0 or > 1472) throw new ArgumentException("Payload ICMP entre 0 e 1472 bytes.", nameof(o));
        foreach (var t in o.Targets)
        {
            if (!Probes.IsValidHost(t.Host)) throw new ArgumentException($"Host inválido: {t.Host}", nameof(o));
            if (t.Method == ProbeMethod.Tcp && t.TcpPort is < 1 or > 65535) throw new ArgumentException($"Porta TCP inválida: {t.TcpPort}", nameof(o));
        }
    }

    private static async Task TargetLoopAsync(LatencyTarget target, LatencyTestOptions o, ChannelWriter<LatencySample> writer,
        LatencyAccumulator? acc, CancellationToken ct)
    {
        IPAddress? addr = null;
        try { addr = await Probes.ResolveAsync(target.Host, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (addr is null)
        {
            var s = new LatencySample(target.Id, target.Label, 0, DateTimeOffset.Now, ProbeStatus.ResolveFailed, null, null, "Falha ao resolver o nome.");
            acc?.SetError(target.Id, "Falha ao resolver o nome.");
            acc?.Add(s);
            writer.TryWrite(s);
            return;
        }
        acc?.SetResolved(target.Id, addr.ToString());

        var payload = new byte[o.PayloadBytes];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)('a' + i % 23); // mesmo padrão do ping.exe
        using var ping = new Ping();
        var next = DateTimeOffset.UtcNow;
        for (int seq = 1; o.Count is null || seq <= o.Count; seq++)
        {
            if (ct.IsCancellationRequested) return;
            next += o.Interval;
            ProbeResult r;
            try
            {
                r = target.Method == ProbeMethod.Tcp
                    ? await Probes.TcpConnectAsync(addr, target.TcpPort, o.Timeout, ct).ConfigureAwait(false)
                    : await Probes.IcmpAsync(ping, addr, o.Timeout, payload, null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            var sample = new LatencySample(target.Id, target.Label, seq, DateTimeOffset.Now, r.Status, r.RttMs, addr.ToString(), r.Detail);
            acc?.Add(sample);
            writer.TryWrite(sample);

            if (o.Count is not null && seq >= o.Count) break;
            var wait = next - DateTimeOffset.UtcNow;
            if (wait < TimeSpan.Zero) { next = DateTimeOffset.UtcNow; continue; } // atrasou (timeout longo): não acumula rajada
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>
/// Acumulador thread-safe de amostras para estatísticas em tempo real (modo contínuo).
/// Mantém uma janela das últimas N amostras por alvo.
/// </summary>
public sealed class LatencyAccumulator
{
    private sealed class State
    {
        public required LatencyTarget Target;
        public readonly Queue<LatencySample> Window = new();
        public string? Resolved;
        public string? Error;
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly int _max;
    private readonly object _lock = new();

    public LatencyAccumulator(IEnumerable<LatencyTarget> targets, int maxSamplesPerTarget = 3600)
    {
        _max = Math.Max(10, maxSamplesPerTarget);
        foreach (var t in targets)
            if (_states.TryAdd(t.Id, new State { Target = t })) _order.Add(t.Id);
    }

    public void Add(LatencySample s)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(s.TargetId, out var st)) return;
            if (s.Status == ProbeStatus.ResolveFailed) return; // não é uma sonda enviada
            st.Window.Enqueue(s);
            while (st.Window.Count > _max) st.Window.Dequeue();
        }
    }

    internal void SetResolved(string id, string addr) { lock (_lock) if (_states.TryGetValue(id, out var s)) s.Resolved = addr; }
    internal void SetError(string id, string err) { lock (_lock) if (_states.TryGetValue(id, out var s)) s.Error = err; }

    /// <summary>Estatísticas atuais por alvo (na ordem em que os alvos foram informados).</summary>
    public IReadOnlyList<TargetLatencyResult> Snapshot()
    {
        lock (_lock)
        {
            return _order.Select(id =>
            {
                var st = _states[id];
                var samples = st.Window.ToArray();
                return new TargetLatencyResult(st.Target, st.Resolved, LatencyMath.Compute(samples), samples, st.Error);
            }).ToList();
        }
    }
}
