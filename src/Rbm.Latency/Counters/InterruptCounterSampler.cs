using System.Globalization;
using System.Runtime.InteropServices;
using Rbm.Core;
using Rbm.Latency.Native;

namespace Rbm.Latency;

/// <summary>
/// Indicadores de DPC/interrupção SEM elevação, via contadores de desempenho (PDH):
/// <c>\Processor(_Total)\% DPC Time</c>, <c>% Interrupt Time</c>, <c>DPCs Queued/sec</c>, <c>Interrupts/sec</c>
/// e, por CPU, <c>\Processor(*)\% DPC Time</c> / <c>% Interrupt Time</c>.
/// Usa PdhAddEnglishCounter, então funciona em Windows em português (nomes de contadores localizados).
/// Resultado apenas indicativo: médias por intervalo, sem picos individuais nem atribuição a drivers.
/// </summary>
public sealed class InterruptCounterSampler
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 600;

    public const string DpcTimePath = @"\Processor(_Total)\% DPC Time";
    public const string InterruptTimePath = @"\Processor(_Total)\% Interrupt Time";
    public const string DpcsQueuedPath = @"\Processor(_Total)\DPCs Queued/sec";
    public const string InterruptsPath = @"\Processor(_Total)\Interrupts/sec";
    private const string PerCpuDpcPath = @"\Processor(*)\% DPC Time";
    private const string PerCpuIntPath = @"\Processor(*)\% Interrupt Time";

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Amostra os contadores por <paramref name="seconds"/> (1..600) com intervalo (0,25..10 s; padrão 1 s).</summary>
    public async Task<InterruptCounterSummary> SampleAsync(int seconds, TimeSpan? interval = null, CancellationToken ct = default)
    {
        if (seconds is < MinSeconds or > MaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Duração deve estar entre {MinSeconds} e {MaxSeconds} segundos.");
        var step = interval ?? TimeSpan.FromSeconds(1);
        if (step < TimeSpan.FromMilliseconds(250) || step > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(interval), "Intervalo deve estar entre 0,25 e 10 segundos.");

        var startedAt = DateTimeOffset.Now;
        uint st = NativeMethods.PdhOpenQueryW(null, IntPtr.Zero, out var query);
        if (st != 0) throw new InvalidOperationException($"PdhOpenQuery falhou (0x{st:X8}).");
        try
        {
            IntPtr Add(string path)
            {
                uint r = NativeMethods.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var c);
                if (r != 0) throw new InvalidOperationException($"Contador indisponível: {path} (0x{r:X8}).");
                return c;
            }
            var cDpc = Add(DpcTimePath);
            var cInt = Add(InterruptTimePath);
            var cDpcQ = Add(DpcsQueuedPath);
            var cInts = Add(InterruptsPath);
            var cCpuDpc = Add(PerCpuDpcPath);
            var cCpuInt = Add(PerCpuIntPath);

            NativeMethods.PdhCollectQueryData(query); // primeira coleta: base para contadores de taxa
            var dpc = new List<double>(); var intr = new List<double>(); var dpcq = new List<double>(); var ints = new List<double>();
            var cpuDpc = new Dictionary<string, List<double>>(); var cpuInt = new Dictionary<string, List<double>>();

            int n = Math.Max(1, (int)Math.Round(seconds / step.TotalSeconds));
            for (int i = 0; i < n; i++)
            {
                await Task.Delay(step, ct).ConfigureAwait(false);
                if (NativeMethods.PdhCollectQueryData(query) != 0) continue;
                AddValue(cDpc, dpc); AddValue(cInt, intr); AddValue(cDpcQ, dpcq); AddValue(cInts, ints);
                AddArray(cCpuDpc, cpuDpc); AddArray(cCpuInt, cpuInt);
            }

            var perCpu = cpuDpc.Keys.Union(cpuInt.Keys)
                .Where(k => !k.Equals("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => int.TryParse(k, out var x) ? x : int.MaxValue).ThenBy(k => k, StringComparer.Ordinal)
                .Select(k =>
                {
                    var d = cpuDpc.GetValueOrDefault(k) ?? new List<double>();
                    var it = cpuInt.GetValueOrDefault(k) ?? new List<double>();
                    return new CpuCounterStats(k, d.Count > 0 ? d.Average() : 0, d.Count > 0 ? d.Max() : 0, it.Count > 0 ? it.Average() : 0, it.Count > 0 ? it.Max() : 0);
                })
                .ToList();

            var sDpc = CounterSeries.From(DpcTimePath, dpc);
            var sInt = CounterSeries.From(InterruptTimePath, intr);
            var sDpcQ = CounterSeries.From(DpcsQueuedPath, dpcq);
            var sInts = CounterSeries.From(InterruptsPath, ints);
            return new InterruptCounterSummary(startedAt, seconds, step.TotalSeconds, sDpc, sInt, sDpcQ, sInts, perCpu,
                Interpret(sDpc, sInt, perCpu), LatencyTexts.CounterLimitations);
        }
        finally
        {
            NativeMethods.PdhCloseQuery(query);
        }
    }

    /// <summary>
    /// Interpretação indicativa (heurística, não norma):
    /// média (% DPC + % Interrupção) do total &lt; 1% = baixa; 1–3% = moderada; &gt; 3% = alta;
    /// pico por CPU &gt; 10% em DPC ou interrupção = concentração relevante naquele CPU.
    /// </summary>
    public static string Interpret(CounterSeries dpc, CounterSeries intr, IReadOnlyList<CpuCounterStats> perCpu)
    {
        if (dpc.Count == 0 || intr.Count == 0) return "Sem amostras válidas dos contadores.";
        double mean = dpc.Mean + intr.Mean;
        string level = mean < 1 ? "baixa" : mean <= 3 ? "moderada" : "alta";
        string text = string.Format(Pt, "Carga média de DPC+interrupções: {0:0.##}% do tempo total de CPU (pico de DPC {1:0.##}%, pico de interrupção {2:0.##}%) — {3}.", mean, dpc.Max, intr.Max, level);
        var hot = perCpu.Where(c => Math.Max(c.MaxDpcPercent, c.MaxInterruptPercent) > 10).OrderByDescending(c => c.MaxDpcPercent + c.MaxInterruptPercent).Take(3).ToList();
        if (hot.Count > 0)
            text += " CPUs com picos acima de 10%: " + string.Join(", ", hot.Select(c => string.Format(Pt, "CPU {0} (DPC {1:0.#}%, int. {2:0.#}%)", c.Instance, c.MaxDpcPercent, c.MaxInterruptPercent))) + ".";
        text += " Indicativo apenas: não mostra picos individuais (µs) nem o driver responsável — use a captura DPC/ISR para isso.";
        return text;
    }

    private static void AddValue(IntPtr counter, List<double> target)
    {
        if (NativeMethods.PdhGetFormattedCounterValue(counter, NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100, out _, out var v) == 0
            && v.CStatus is NativeMethods.PDH_CSTATUS_VALID_DATA or NativeMethods.PDH_CSTATUS_NEW_DATA
            && double.IsFinite(v.doubleValue))
            target.Add(v.doubleValue);
    }

    private static void AddArray(IntPtr counter, Dictionary<string, List<double>> target)
    {
        const uint fmt = NativeMethods.PDH_FMT_DOUBLE | NativeMethods.PDH_FMT_NOCAP100;
        uint size = 0;
        uint r = NativeMethods.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out uint count, IntPtr.Zero);
        if (r != NativeMethods.PDH_MORE_DATA || size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = NativeMethods.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out count, buf);
            if (r != 0) return;
            int itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * itemSize);
                if (item.FmtValue.CStatus is not (NativeMethods.PDH_CSTATUS_VALID_DATA or NativeMethods.PDH_CSTATUS_NEW_DATA)) continue;
                string name = Marshal.PtrToStringUni(item.szName) ?? "?";
                if (!target.TryGetValue(name, out var list)) target[name] = list = new List<double>();
                if (double.IsFinite(item.FmtValue.doubleValue)) list.Add(item.FmtValue.doubleValue);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
