using System.Diagnostics;
using Rbm.Core;

namespace Rbm.Latency;

/// <summary>
/// Estimador da taxa de relatório (Hz) de dispositivos de entrada a partir de timestamps QPC de WM_INPUT.
/// <para>
/// Uso pela UI: registre Raw Input (<see cref="RawInputHelper.Register"/>), e a cada WM_INPUT chame
/// <c>estimator.Add(deviceHandle, Stopwatch.GetTimestamp())</c> (de preferência só para eventos de movimento — ver
/// <see cref="RawInputHelper.TryParse"/>). Consulte <see cref="GetEstimate(long)"/> periodicamente.
/// </para>
/// Método: intervalos entre eventos consecutivos do mesmo dispositivo; intervalos &gt; <c>pauseThresholdMs</c> (padrão 50 ms)
/// são tratados como pausas (dispositivo parado) e descartados; mantém uma janela móvel dos últimos N intervalos (padrão 2000);
/// taxa = 1000 / mediana dos intervalos (a mediana resiste a pausas curtas, agrupamentos e relatórios perdidos).
/// Mede a taxa de relatórios entregues enquanto há movimento — não a latência de clique.
/// Thread-safe.
/// </summary>
public sealed class PollingRateEstimator
{
    /// <summary>Taxas de polling USB comuns (Hz).</summary>
    public static readonly int[] StandardRatesHz = { 125, 250, 500, 1000, 2000, 4000, 8000 };
    public const int MinIntervalsForAnyEstimate = 10;

    private readonly double _ticksToMs;
    private readonly int _window;
    private readonly double _pauseMs;
    private readonly int _minIntervals;
    private readonly Dictionary<long, DeviceState> _devices = new();
    private readonly object _lock = new();

    /// <param name="qpcFrequency">Frequência do QPC (ticks/s) — use <see cref="Stopwatch.Frequency"/>.</param>
    /// <param name="windowSize">Nº de intervalos na janela móvel (50..100000).</param>
    /// <param name="pauseThresholdMs">Intervalos acima disto são pausas (1..1000 ms).</param>
    /// <param name="minIntervals">Mínimo de intervalos para confiança Média/Alta.</param>
    public PollingRateEstimator(long qpcFrequency, int windowSize = 2000, double pauseThresholdMs = 50, int minIntervals = 100)
    {
        if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));
        if (windowSize is < 50 or > 100_000) throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (pauseThresholdMs is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pauseThresholdMs));
        _ticksToMs = 1000.0 / qpcFrequency;
        _window = windowSize;
        _pauseMs = pauseThresholdMs;
        _minIntervals = Math.Max(MinIntervalsForAnyEstimate, minIntervals);
    }

    /// <summary>Estimador com a frequência QPC desta máquina.</summary>
    public static PollingRateEstimator CreateDefault() => new(Stopwatch.Frequency);

    public void Add(IntPtr deviceHandle, long qpcTimestamp) => Add(deviceHandle.ToInt64(), qpcTimestamp);

    public void Add(long deviceHandle, long qpcTimestamp)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceHandle, out var st)) _devices[deviceHandle] = st = new DeviceState(_window);
            if (st.LastTimestamp is { } last)
            {
                long dt = qpcTimestamp - last;
                if (dt < 0) { st.OutOfOrder++; return; } // fora de ordem: ignora, mantém o último
                double ms = dt * _ticksToMs;
                if (ms > _pauseMs) st.Pauses++;
                else st.Push(ms);
            }
            st.LastTimestamp = qpcTimestamp;
        }
    }

    public void Reset()
    {
        lock (_lock) _devices.Clear();
    }

    public void Reset(long deviceHandle)
    {
        lock (_lock) _devices.Remove(deviceHandle);
    }

    public IReadOnlyCollection<long> Devices
    {
        get { lock (_lock) return _devices.Keys.ToArray(); }
    }

    /// <summary>Estimativa atual do dispositivo (null se houver menos de 10 intervalos válidos).</summary>
    public PollingRateEstimate? GetEstimate(long deviceHandle)
    {
        double[] intervals; int pauses;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceHandle, out var st)) return null;
            intervals = st.Snapshot();
            pauses = st.Pauses;
        }
        return Estimate(deviceHandle, intervals, pauses, _minIntervals);
    }

    public PollingRateEstimate? GetEstimate(IntPtr deviceHandle) => GetEstimate(deviceHandle.ToInt64());

    public IReadOnlyList<PollingRateEstimate> GetAllEstimates() =>
        Devices.Select(GetEstimate).Where(e => e is not null).Select(e => e!).OrderByDescending(e => e.IntervalCount).ToList();

    /// <summary>Cálculo puro a partir de intervalos (ms) já filtrados.</summary>
    public static PollingRateEstimate? Estimate(long deviceHandle, IReadOnlyList<double> intervalsMs, int pausesIgnored, int minIntervals = 100)
    {
        if (intervalsMs.Count < MinIntervalsForAnyEstimate) return null;
        var sorted = intervalsMs.ToArray();
        Array.Sort(sorted);
        double median = Stats.Percentile(sorted, 50);
        double mean = sorted.Average();
        double p1 = Stats.Percentile(sorted, 1);
        double p99 = Stats.Percentile(sorted, 99);
        double sd = sorted.Length > 1 ? Math.Sqrt(sorted.Sum(x => (x - mean) * (x - mean)) / (sorted.Length - 1)) : 0;

        var notes = new List<string> { LatencyTexts.PollingRateNote };
        if (median <= 0)
        {
            notes.Add("Mediana de intervalo zero: eventos chegaram agrupados (a UI processou mensagens em lote). Estimativa inválida.");
            return new PollingRateEstimate(deviceHandle, sorted.Length, pausesIgnored, median, mean, p1, p99, sd, 0, 0, 0, Confidence.Low, notes);
        }

        double rate = 1000.0 / median;
        int nearest = NearestStandardRate(rate);
        double stability = sorted.Count(x => Math.Abs(x - median) <= 0.2 * median) * 100.0 / sorted.Length;
        double bunched = sorted.Count(x => x < 0.25 * median) * 100.0 / sorted.Length;

        if (bunched > 10)
            notes.Add($"{bunched:0}% dos intervalos são muito curtos (eventos agrupados): a fila de mensagens da UI pode estar atrasando WM_INPUT. {LatencyTexts.RawInputTimingNote}");
        double ratio = rate > nearest ? rate / nearest : nearest / rate;
        if (ratio > 1.25)
            notes.Add($"A taxa observada ({rate:0} Hz) não corresponde bem a uma taxa padrão: o dispositivo pode não enviar relatório a cada ciclo (movimento lento) ou a medição foi perturbada. Mova o mouse de forma contínua e rápida.");
        if (rate >= 1500)
            notes.Add(LatencyTexts.RawInputTimingNote);

        var confidence = sorted.Length >= Math.Max(500, minIntervals) && stability >= 80 && ratio <= 1.1 ? Confidence.High
            : sorted.Length >= minIntervals && stability >= 50 ? Confidence.Medium
            : Confidence.Low;

        return new PollingRateEstimate(deviceHandle, sorted.Length, pausesIgnored, median, mean, p1, p99, sd, rate, nearest, stability, confidence, notes);
    }

    /// <summary>Taxa padrão mais próxima (em escala logarítmica).</summary>
    public static int NearestStandardRate(double hz)
    {
        if (!(hz > 0) || !double.IsFinite(hz)) return 0;
        int best = StandardRatesHz[0];
        double bestDist = double.MaxValue;
        foreach (var r in StandardRatesHz)
        {
            double d = Math.Abs(Math.Log(hz / r));
            if (d < bestDist) { bestDist = d; best = r; }
        }
        return best;
    }

    private sealed class DeviceState(int capacity)
    {
        private readonly double[] _ring = new double[capacity];
        private int _next, _count;
        public long? LastTimestamp;
        public int Pauses;
        public int OutOfOrder;

        public void Push(double ms)
        {
            _ring[_next] = ms;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        public double[] Snapshot()
        {
            var arr = new double[_count];
            int start = (_next - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < _count; i++) arr[i] = _ring[(start + i) % _ring.Length];
            return arr;
        }
    }
}
