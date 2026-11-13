using System.Diagnostics;
using Rbm.Latency.Native;
using Rbm.Security;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Rbm.Latency;

/// <summary>
/// Analisador DPC/ISR por ETW de kernel (TraceEvent).
/// <para><b>Requer elevação — executado via RBM.Agent</b> (operação <c>latency.capture</c>, payload <c>LatencyCaptureRequest</c>).</para>
/// <para>
/// Usa uma sessão de kernel PRIVADA ("system logger", Windows 8+) chamada <see cref="PrivateSessionName"/>, para não disputar a
/// sessão global "NT Kernel Logger" com outras ferramentas (LatencyMon, xperf, Process Monitor). Se a sessão privada falhar,
/// recorre ao "NT Kernel Logger" somente se ele não estiver em uso — nunca interrompe sessões que não sejam do RBM Optimizer.
/// </para>
/// Palavras-chave: DeferedProcedureCalls | Interrupt | ImageLoad | Process (+ MemoryHardFaults opcional).
/// Duração de cada execução: <c>DPCTraceData.ElapsedTimeMSec</c> / <c>ISRTraceData.ElapsedTimeMSec</c>, que o TraceEvent calcula como
/// timestamp do evento − InitialTime (campo do payload, no relógio QPC da sessão). Os eventos são emitidos ao FIM da execução.
/// </summary>
public sealed class DpcIsrCaptureService
{
    /// <summary>Prefixo de todas as sessões ETW criadas pelo RBM Optimizer para latência (usado na limpeza de sessões órfãs).</summary>
    public const string SessionPrefix = "RBM-Latency";
    /// <summary>Prefixo usado por versões de desenvolvimento anteriores (nome antigo do produto); também é limpo.</summary>
    public const string LegacySessionPrefix = "Rbm-Latency";
    public const string PrivateSessionName = SessionPrefix + "-Kernel";
    public const int MinSeconds = 5;
    public const int MaxSeconds = 300;

    /// <summary>Valida as opções (lança <see cref="ArgumentOutOfRangeException"/> com mensagem pt-BR).</summary>
    public static void Validate(DpcIsrCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Seconds is < MinSeconds or > MaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), options.Seconds, $"Duração da captura deve estar entre {MinSeconds} e {MaxSeconds} segundos.");
        if (options.TopExecutions is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(options), options.TopExecutions, "TopExecutions deve estar entre 1 e 500.");
        if (options.BufferSizeMB is < 16 or > 512)
            throw new ArgumentOutOfRangeException(nameof(options), options.BufferSizeMB, "BufferSizeMB deve estar entre 16 e 512.");
        if (options.Thresholds is { } t && !(t.ElevatedUs > 0 && t.ElevatedUs < t.HighUs && t.HighUs < t.SevereUs && t.SustainedSeconds >= 1 && t.ModuleSpikeCount >= 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Limiares inválidos: exige 0 < ElevatedUs < HighUs < SevereUs e contagens ≥ 1.");
    }

    /// <summary>
    /// Encerra sessões ETW órfãs do RBM Optimizer (nome começando com <see cref="SessionPrefix"/>), ex.: após queda do agente.
    /// Não toca em sessões de outras ferramentas. Requer elevação. Retorna quantas foram encerradas.
    /// </summary>
    public static int StopStaleSessions()
    {
        int stopped = 0;
        List<string> names;
        try { names = TraceEventSession.GetActiveSessionNames(); }
        catch { return 0; }
        foreach (var name in names.Where(IsOwnSessionName))
        {
            try
            {
                using var s = TraceEventSession.GetActiveSession(name);
                if (s is not null && s.Stop(noThrow: true)) stopped++;
            }
            catch { /* melhor esforço */ }
        }
        return stopped;
    }

    /// <summary>true apenas para sessões criadas por este módulo (prefixo atual ou legado). Nunca inclui "NT Kernel Logger".</summary>
    public static bool IsOwnSessionName(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.StartsWith(SessionPrefix + "-", StringComparison.OrdinalIgnoreCase) || name.StartsWith(LegacySessionPrefix + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Captura DPC/ISR por <paramref name="options"/>.Seconds e devolve o relatório.
    /// Requer elevação — executado via RBM.Agent. A sessão é sempre encerrada (cancelamento, exceção ou fim normal).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Processo sem elevação.</exception>
    /// <exception cref="InvalidOperationException">Não foi possível iniciar uma sessão de kernel.</exception>
    /// <exception cref="OperationCanceledException">Cancelado (a sessão é encerrada antes de propagar).</exception>
    public async Task<LatencyReport> CaptureAsync(DpcIsrCaptureOptions options, IProgress<LatencyCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        Validate(options);
        if (!ChannelSecurity.IsElevated())
            throw new UnauthorizedAccessException("A captura DPC/ISR requer elevação (sessão ETW de kernel). Execute pelo agente elevado do RBM Optimizer (RBM.Agent).");
        ct.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        int stale = StopStaleSessions();
        if (stale > 0) warnings.Add($"{stale} sessão(ões) ETW órfã(s) do RBM Optimizer foram encerradas antes da captura.");

        var map = new KernelModuleMap();
        try { map.AddApproximateFromBases(KernelModuleEnumerator.Enumerate()); }
        catch (Exception ex) { warnings.Add("EnumDeviceDrivers indisponível: " + ex.Message); }

        var aggregator = new LatencyAggregator(options.TopExecutions);
        var processNames = new Dictionary<int, string>();
        long dpcEvents = 0, isrEvents = 0;

        var keywords = KernelTraceEventParser.Keywords.DeferedProcedureCalls
                       | KernelTraceEventParser.Keywords.Interrupt
                       | KernelTraceEventParser.Keywords.ImageLoad
                       | KernelTraceEventParser.Keywords.Process;
        if (options.CollectHardFaults) keywords |= KernelTraceEventParser.Keywords.MemoryHardFaults;

        TraceEventSession? session = null;
        Task? processing = null;
        var sw = new Stopwatch();
        DateTimeOffset startedAt = DateTimeOffset.Now;
        long lost = 0;
        string sessionName = PrivateSessionName;
        try
        {
            session = StartSession(options.BufferSizeMB, keywords, warnings);
            var kernel = session.Source.Kernel;

            void OnDpc(DPCTraceData d)
            {
                Interlocked.Increment(ref dpcEvents);
                aggregator.Add(new KernelLatencySample(LatencyEventKind.Dpc, d.TimeStampRelativeMSec, d.ElapsedTimeMSec * 1000.0, d.ProcessorNumber, d.Routine));
            }
            kernel.PerfInfoDPC += OnDpc;
            kernel.PerfInfoTimerDPC += OnDpc;
            kernel.PerfInfoThreadedDPC += OnDpc;
            kernel.PerfInfoISR += d =>
            {
                Interlocked.Increment(ref isrEvents);
                aggregator.Add(new KernelLatencySample(LatencyEventKind.Isr, d.TimeStampRelativeMSec, d.ElapsedTimeMSec * 1000.0, d.ProcessorNumber, d.Routine));
            };

            void OnImage(ImageLoadTraceData d)
            {
                if (KernelModuleMap.IsKernelAddress(d.ImageBase) && d.ImageSize > 0)
                    map.Add(d.ImageBase, (ulong)(uint)d.ImageSize, d.FileName);
            }
            kernel.ImageDCStart += OnImage;
            kernel.ImageLoad += OnImage;
            // ImageUnload é ignorado de propósito: DPCs já agregados por endereço ainda precisam ser resolvidos no fim.

            void OnProcess(ProcessTraceData p)
            {
                if (!string.IsNullOrEmpty(p.ImageFileName)) processNames[p.ProcessID] = p.ImageFileName;
            }
            kernel.ProcessDCStart += OnProcess;
            kernel.ProcessStart += OnProcess;
            if (options.CollectHardFaults)
                kernel.MemoryHardFault += h => aggregator.AddHardFault(h.ProcessID, h.ElapsedTimeMSec);

            var source = session.Source;
            startedAt = DateTimeOffset.Now;
            sw.Start();
            processing = Task.Factory.StartNew(() => source.Process(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var total = TimeSpan.FromSeconds(options.Seconds);
            while (sw.Elapsed < total)
            {
                var remaining = total - sw.Elapsed;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (processing.IsCompleted)
                {
                    warnings.Add("A sessão ETW terminou antes do tempo previsto (pode ter sido encerrada externamente).");
                    break;
                }
                progress?.Report(new LatencyCaptureProgress(Math.Min(sw.Elapsed.TotalSeconds, options.Seconds), options.Seconds,
                    Interlocked.Read(ref dpcEvents), Interlocked.Read(ref isrEvents)));
            }
        }
        finally
        {
            sw.Stop();
            if (session is not null)
            {
                try { session.Stop(noThrow: true); } catch { /* melhor esforço */ }
            }
            if (processing is not null)
            {
                // Process() retorna quando a sessão para; aguarda o esvaziamento dos buffers (limite de 15 s).
                try { await processing.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { warnings.Add("O processamento dos eventos não terminou em 15 s após parar a sessão."); }
                catch (Exception ex) { warnings.Add("Erro no processamento ETW: " + ex.Message); }
            }
            if (session is not null)
            {
                try { lost = session.EventsLost; } catch { }
                sessionName = session.SessionName ?? sessionName;
                try { session.Dispose(); } catch { /* melhor esforço */ }
            }
        }

        ct.ThrowIfCancellationRequested();
        if (processing is { IsFaulted: true } && aggregator.DpcCount + aggregator.IsrCount == 0)
            throw new InvalidOperationException("Falha ao processar eventos da sessão ETW de kernel: " + processing.Exception?.GetBaseException().Message);

        if (map.Modules.All(m => m.Approximate))
            warnings.Add("Nenhum evento de rundown de imagens (ImageDCStart) foi recebido; módulos foram mapeados por EnumDeviceDrivers (faixas aproximadas).");

        var ctx = new LatencyReportContext(
            startedAt,
            options.Seconds,
            sw.Elapsed.TotalSeconds,
            sessionName,
            Environment.ProcessorCount,
            lost,
            processNames,
            options.Thresholds,
            warnings);
        return aggregator.Build(map, ctx);
    }

    private static TraceEventSession StartSession(int bufferMb, KernelTraceEventParser.Keywords keywords, List<string> warnings)
    {
        Exception? privateError = null;
        try
        {
            var s = new TraceEventSession(PrivateSessionName, TraceEventSessionOptions.Create) { BufferSizeMB = bufferMb, StopOnDispose = true };
            try
            {
                s.EnableKernelProvider(keywords);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            privateError = ex;
        }

        // Reserva: NT Kernel Logger, somente se estiver livre (não encerramos sessões de terceiros).
        bool inUse;
        try { inUse = TraceEventSession.GetActiveSessionNames().Contains(KernelTraceEventParser.KernelSessionName, StringComparer.OrdinalIgnoreCase); }
        catch { inUse = true; }
        if (inUse)
            throw new InvalidOperationException(
                $"Não foi possível criar a sessão de kernel privada ({privateError?.Message}) e a sessão \"{KernelTraceEventParser.KernelSessionName}\" " +
                "está em uso por outra ferramenta (ex.: LatencyMon, xperf/WPR, Process Monitor). Feche-a e tente novamente.", privateError);

        var k = new TraceEventSession(KernelTraceEventParser.KernelSessionName, TraceEventSessionOptions.Create) { BufferSizeMB = bufferMb, StopOnDispose = true };
        try
        {
            k.EnableKernelProvider(keywords);
            warnings.Add($"Sessão de kernel privada indisponível ({privateError?.Message}); usada a sessão \"{KernelTraceEventParser.KernelSessionName}\".");
            return k;
        }
        catch (Exception ex)
        {
            k.Dispose();
            throw new InvalidOperationException("Falha ao iniciar a sessão ETW de kernel: " + ex.Message, ex);
        }
    }
}

/// <summary>Lista de drivers carregados via EnumDeviceDrivers (psapi). Endereços reais só com elevação.</summary>
public static class KernelModuleEnumerator
{
    public static IReadOnlyList<(ulong Base, string Name)> Enumerate()
    {
        NativeMethods.EnumDeviceDrivers(null, 0, out uint needed);
        if (needed == 0) return Array.Empty<(ulong, string)>();
        var bases = new IntPtr[needed / (uint)IntPtr.Size + 16];
        if (!NativeMethods.EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out needed))
            throw new System.ComponentModel.Win32Exception();
        int count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)bases.Length);
        var list = new List<(ulong, string)>(count);
        var buf = new char[260];
        for (int i = 0; i < count; i++)
        {
            ulong b = (ulong)bases[i].ToInt64();
            if (!KernelModuleMap.IsKernelAddress(b)) continue; // sem elevação os endereços vêm zerados
            uint len = NativeMethods.GetDeviceDriverBaseNameW(bases[i], buf, (uint)buf.Length);
            if (len == 0) continue;
            list.Add((b, new string(buf, 0, (int)len)));
        }
        return list;
    }
}
