using System.Diagnostics;
using System.Globalization;
using Rbm.Benchmarks.Cpu;
using Rbm.Benchmarks.Framework;

namespace Rbm.Benchmarks.Responsiveness;

/// <summary>
/// Precisão de Sleep(1): distribuição do tempo real de Thread.Sleep(1) com a resolução padrão do processo e com
/// timeBeginPeriod(1) (sempre restaurado com timeEndPeriod em finally). ID: sched.sleep.
/// </summary>
public sealed class SleepPrecisionBenchmark : IterativeBenchmark
{
    public override string Id => "sched.sleep";
    public override string Name => "Responsividade — precisão do Sleep(1 ms)";
    public override BenchmarkCategory Category => BenchmarkCategory.Responsiveness;
    public override string Unit => BenchmarkUnits.Microseconds;
    public override bool HigherIsBetter => false;
    public override string Methodology =>
        "Mede, com Stopwatch, a duração real de chamadas Thread.Sleep(1) (pedido: 1000 µs) em uma thread de prioridade Normal. " +
        "Por iteração: N chamadas (padrão 100) com a resolução de timer padrão do processo e N chamadas com timeBeginPeriod(1) ativo " +
        "(timeEndPeriod(1) é sempre chamado em finally, inclusive em cancelamento). Métrica principal: mediana do Sleep(1) com timeBeginPeriod(1). " +
        "Secundárias: mediana/P99/máximo em cada modo. Desde o Windows 10 2004 a resolução pedida vale apenas para o processo que a pede; " +
        "no Windows 11 processos com janela minimizada/oculta podem não recebê-la. Valores próximos de 1000 µs = timer preciso; " +
        "~15600 µs = resolução padrão de 15,6 ms.";

    private static readonly MetricDefinition[] s_metrics =
    {
        new("sleep.default.p50", "Sleep(1) resolução padrão — mediana", BenchmarkUnits.Microseconds, false),
        new("sleep.default.p99", "Sleep(1) resolução padrão — P99", BenchmarkUnits.Microseconds, false),
        new("sleep.default.max", "Sleep(1) resolução padrão — máximo", BenchmarkUnits.Microseconds, false),
        new("sleep.hires.p50", "Sleep(1) com timeBeginPeriod(1) — mediana", BenchmarkUnits.Microseconds, false),
        new("sleep.hires.p99", "Sleep(1) com timeBeginPeriod(1) — P99", BenchmarkUnits.Microseconds, false),
        new("sleep.hires.max", "Sleep(1) com timeBeginPeriod(1) — máximo", BenchmarkUnits.Microseconds, false),
    };
    public override IReadOnlyList<MetricDefinition> SecondaryMetrics => s_metrics;

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(options);

    /// <summary>Executa <paramref name="count"/> Sleep(1) e devolve as durações em µs.</summary>
    internal static double[] MeasureSleeps(int count, CancellationToken ct)
    {
        var a = new double[count];
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            long t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            a[i] = Timing.TicksToMicroseconds(Stopwatch.GetTimestamp() - t0);
        }
        return a;
    }

    private sealed class Session(BenchmarkOptions o) : BenchmarkSession
    {
        public override void Prepare(SessionContext ctx)
        {
            Parameters["samplesPerMode"] = o.SleepSamplesPerIteration.ToString(CultureInfo.InvariantCulture);
            var (cur, _, _) = ConditionsProbe.ReadTimerResolution();
            if (cur is not null) Info["timerResolutionBeforeMs"] = cur.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            int n = o.SleepSamplesPerIteration;
            var def = Distribution.Of(MeasureSleeps(n, ctx.Ct));
            double[] hires;
            bool raised = NativeMethods.timeBeginPeriod(1) == 0;
            try
            {
                if (!raised) Warnings.Add("timeBeginPeriod(1) foi recusado pelo sistema; o modo 'alta resolução' usou a resolução vigente.");
                var (cur, _, _) = ConditionsProbe.ReadTimerResolution();
                if (cur is not null) Info["timerResolutionDuringHiresMs"] = cur.Value.ToString("0.####", CultureInfo.InvariantCulture);
                hires = MeasureSleeps(n, ctx.Ct);
            }
            finally
            {
                if (raised) NativeMethods.timeEndPeriod(1);
            }
            var hi = Distribution.Of(hires);
            return new IterationMeasurement(hi.P50, new Dictionary<string, double>
            {
                ["sleep.default.p50"] = def.P50, ["sleep.default.p99"] = def.P99, ["sleep.default.max"] = def.Max,
                ["sleep.hires.p50"] = hi.P50, ["sleep.hires.p99"] = hi.P99, ["sleep.hires.max"] = hi.Max,
            });
        }

        public override void Complete(SessionContext ctx)
        {
            var (cur, _, _) = ConditionsProbe.ReadTimerResolution();
            if (cur is not null) Info["timerResolutionAfterMs"] = cur.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Latência de despertar de thread: tempo entre Set() de um evento do kernel e a retomada da thread que esperava,
/// com o sistema ocioso e sob carga de CPU em todas as threads lógicas. ID: sched.wakeup.
/// </summary>
public sealed class WakeLatencyBenchmark : IterativeBenchmark
{
    public override string Id => "sched.wakeup";
    public override string Name => "Responsividade — latência de despertar de thread";
    public override BenchmarkCategory Category => BenchmarkCategory.Responsiveness;
    public override string Unit => BenchmarkUnits.Microseconds;
    public override bool HigherIsBetter => false;
    public override string Methodology =>
        "Uma thread 'em espera' (prioridade Normal) bloqueia em um evento do kernel (EventWaitHandle, auto-reset). A thread sinalizadora aguarda um " +
        "intervalo pseudoaleatório fixo de 100–600 µs (espera ativa, para garantir que a outra thread esteja bloqueada), registra o instante, chama Set() " +
        "e a thread em espera registra o instante em que voltou a executar. Latência = diferença (Stopwatch, µs). Por iteração: N rodadas (padrão 500) " +
        "com o sistema ocioso e N rodadas com carga de fundo (uma thread de cálculo inteiro de prioridade Normal por processador lógico). " +
        "Métrica principal: P99 ocioso. Secundárias: P50/P99/máximo em cada condição. Inclui custo de troca de contexto e eventual migração de núcleo.";

    private static readonly MetricDefinition[] s_metrics =
    {
        new("wake.idle.p50", "Despertar (ocioso) — P50", BenchmarkUnits.Microseconds, false),
        new("wake.idle.p99", "Despertar (ocioso) — P99", BenchmarkUnits.Microseconds, false),
        new("wake.idle.max", "Despertar (ocioso) — máximo", BenchmarkUnits.Microseconds, false),
        new("wake.load.p50", "Despertar (sob carga) — P50", BenchmarkUnits.Microseconds, false),
        new("wake.load.p99", "Despertar (sob carga) — P99", BenchmarkUnits.Microseconds, false),
        new("wake.load.max", "Despertar (sob carga) — máximo", BenchmarkUnits.Microseconds, false),
    };
    public override IReadOnlyList<MetricDefinition> SecondaryMetrics => s_metrics;

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(options);

    private sealed class Session(BenchmarkOptions o) : BenchmarkSession
    {
        private readonly EventWaitHandle _signal = new(false, EventResetMode.AutoReset);
        private Thread? _waiter;
        private volatile bool _stop;
        private long _wakeTs;
        private int _ack;
        private int _round;

        public override void Prepare(SessionContext ctx)
        {
            _waiter = new Thread(WaiterLoop) { IsBackground = true, Name = "RBMBench Waiter", Priority = ThreadPriority.Normal };
            _waiter.Start();
            Parameters["samplesPerCondition"] = o.WakeSamplesPerIteration.ToString(CultureInfo.InvariantCulture);
            Parameters["loadThreads"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        }

        private void WaiterLoop()
        {
            while (true)
            {
                _signal.WaitOne();
                long ts = Stopwatch.GetTimestamp();
                if (_stop) return;
                Volatile.Write(ref _wakeTs, ts);
                Volatile.Write(ref _ack, 1);
            }
        }

        private double[] MeasureRounds(int n, CancellationToken ct)
        {
            var lat = new double[n];
            var rng = new XorShift64Star(0x3A4E_0000UL + (ulong)_round++);
            long timeout = Stopwatch.Frequency * 2;
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                Timing.SpinFor(Timing.MicrosecondsToTicks(100 + rng.NextBelow(500)));
                Volatile.Write(ref _ack, 0);
                long t0 = Stopwatch.GetTimestamp();
                _signal.Set();
                while (Volatile.Read(ref _ack) == 0)
                {
                    if (Stopwatch.GetTimestamp() - t0 > timeout)
                        throw new BenchmarkFailureException("A thread em espera não respondeu em 2 s — medição abortada.");
                    Thread.SpinWait(4);
                }
                lat[i] = Timing.TicksToMicroseconds(Volatile.Read(ref _wakeTs) - t0);
            }
            return lat;
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            int n = o.WakeSamplesPerIteration;
            var idle = Distribution.Of(MeasureRounds(n, ctx.Ct));
            (double P50, double P99, double Max, double Mean) load;
            using (var bg = new BackgroundLoad(Environment.ProcessorCount))
            {
                Thread.Sleep(50); // deixa a carga estabilizar
                load = Distribution.Of(MeasureRounds(n, ctx.Ct));
            }
            return new IterationMeasurement(idle.P99, new Dictionary<string, double>
            {
                ["wake.idle.p50"] = idle.P50, ["wake.idle.p99"] = idle.P99, ["wake.idle.max"] = idle.Max,
                ["wake.load.p50"] = load.P50, ["wake.load.p99"] = load.P99, ["wake.load.max"] = load.Max,
            });
        }

        public override void Dispose()
        {
            _stop = true;
            _signal.Set();
            _waiter?.Join(TimeSpan.FromSeconds(5));
            _signal.Dispose();
        }
    }
}

/// <summary>Carga de fundo: N threads de prioridade Normal executando hash de inteiros até Dispose.</summary>
internal sealed class BackgroundLoad : IDisposable
{
    private readonly Thread[] _threads;
    private volatile bool _stop;
    private static readonly ulong[] s_data = CpuKernels.CreateIntegerInput(4096);
    public static ulong Sink;

    public BackgroundLoad(int threads)
    {
        _threads = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            _threads[i] = new Thread(() =>
            {
                ulong acc = 0;
                while (!_stop) acc += CpuKernels.HashWords(s_data);
                Interlocked.Add(ref Sink, acc);
            }) { IsBackground = true, Name = "RBMBench Load", Priority = ThreadPriority.Normal };
            _threads[i].Start();
        }
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// "Jitter" de execução: laço ocupado lendo o Stopwatch e registrando lacunas acima do limiar (padrão 50 µs).
/// Indicador INDIRETO de interrupções (preempção, DPC/ISR, SMI, gerenciamento de energia) — não é medição de DPC. ID: sched.jitter.
/// </summary>
public sealed class JitterBenchmark : IterativeBenchmark
{
    public override string Id => "sched.jitter";
    public override string Name => "Responsividade — interrupções no laço ocupado (jitter)";
    public override BenchmarkCategory Category => BenchmarkCategory.Responsiveness;
    public override string Unit => BenchmarkUnits.EventsPerSecond;
    public override bool HigherIsBetter => false;
    public override string Methodology =>
        "Uma thread de prioridade Normal executa um laço ocupado que lê Stopwatch.GetTimestamp() continuamente durante a duração da iteração (padrão 1 s). " +
        "Sempre que o intervalo entre duas leituras consecutivas excede o limiar (padrão 50 µs), conta-se uma 'lacuna': a thread deixou de executar " +
        "nesse período (preempção pelo agendador, interrupções/DPCs, SMIs de firmware, transições de energia). Métrica principal: lacunas por segundo. " +
        "Secundárias: maior lacuna (µs) e % do tempo perdido em lacunas. É um INDICADOR INDIRETO: não identifica a causa e não substitui " +
        "a medição de latência de DPC/ISR por ETW. A thread não é fixada a um núcleo.";

    private static readonly MetricDefinition[] s_metrics =
    {
        new("jitter.gapsPerSecond", "Lacunas acima do limiar por segundo", BenchmarkUnits.EventsPerSecond, false),
        new("jitter.maxGap", "Maior lacuna", BenchmarkUnits.Microseconds, false),
        new("jitter.lostPercent", "Tempo perdido em lacunas", BenchmarkUnits.Percent, false),
    };
    public override IReadOnlyList<MetricDefinition> SecondaryMetrics => s_metrics;

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(options);

    /// <summary>Executa o laço por <paramref name="duration"/>; retorna (lacunas, soma das lacunas em ticks, maior lacuna em ticks, ticks totais).</summary>
    internal static (long Gaps, long GapTicks, long MaxGap, long Total) Spin(TimeSpan duration, double thresholdUs, CancellationToken ct)
    {
        long thr = Math.Max(1, Timing.MicrosecondsToTicks(thresholdUs));
        long dur = Timing.MicrosecondsToTicks(duration.TotalMilliseconds * 1000);
        long start = Stopwatch.GetTimestamp(), prev = start, gaps = 0, gapTicks = 0, max = 0;
        int k = 0;
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            long gap = now - prev;
            if (gap > thr) { gaps++; gapTicks += gap; if (gap > max) max = gap; }
            prev = now;
            if (now - start >= dur) return (gaps, gapTicks, max, now - start);
            if ((++k & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class Session(BenchmarkOptions o) : BenchmarkSession
    {
        public override void Prepare(SessionContext ctx)
        {
            Parameters["durationSeconds"] = o.JitterDurationPerIteration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            Parameters["thresholdUs"] = o.JitterThresholdMicroseconds.ToString("R", CultureInfo.InvariantCulture);
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            var (gaps, gapTicks, max, total) = Spin(o.JitterDurationPerIteration, o.JitterThresholdMicroseconds, ctx.Ct);
            double seconds = Timing.TicksToSeconds(total);
            double perSec = gaps / seconds;
            return new IterationMeasurement(perSec, new Dictionary<string, double>
            {
                ["jitter.gapsPerSecond"] = perSec,
                ["jitter.maxGap"] = Timing.TicksToMicroseconds(max),
                ["jitter.lostPercent"] = gapTicks * 100.0 / total,
            });
        }
    }
}
