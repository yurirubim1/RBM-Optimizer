using Rbm.Core;

namespace Rbm.Network.Latency;

/// <summary>Cálculos puros de latência/jitter/perda (testáveis).</summary>
public static class LatencyMath
{
    /// <summary>Média das diferenças absolutas entre valores consecutivos. NaN se houver menos de 2 valores.</summary>
    public static double MeanAbsoluteDifference(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return double.NaN;
        double sum = 0;
        for (int i = 1; i < values.Count; i++) sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }

    /// <summary>
    /// Jitter suavizado no estilo RFC 3550 (§6.4.1): J(i) = J(i−1) + (|D(i−1,i)| − J(i−1)) / 16, com J(0) = 0.
    /// Aqui D é a diferença entre RTTs consecutivos (o RFC usa tempo de trânsito; o princípio é o mesmo).
    /// NaN se houver menos de 2 valores.
    /// </summary>
    public static double Rfc3550Jitter(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return double.NaN;
        double j = 0;
        for (int i = 1; i < values.Count; i++)
        {
            double d = Math.Abs(values[i] - values[i - 1]);
            j += (d - j) / 16.0;
        }
        return j;
    }

    /// <summary>Perda percentual. NaN se nada foi enviado.</summary>
    public static double LossPercent(int sent, int received) => sent <= 0 ? double.NaN : (sent - received) * 100.0 / sent;

    /// <summary>
    /// Estatísticas de uma sequência de amostras (na ordem de envio). O jitter considera apenas respostas
    /// consecutivas recebidas (perdas são ignoradas na sequência, não tratadas como zero).
    /// </summary>
    public static LatencyStats Compute(IReadOnlyList<LatencySample> samples)
    {
        if (samples.Count == 0) return LatencyStats.Empty;
        var ordered = samples.OrderBy(s => s.Sequence).ToList();
        var rtts = new List<double>(ordered.Count);
        int timeouts = 0, errors = 0, run = 0, maxRun = 0;
        foreach (var s in ordered)
        {
            if (s.Received) { rtts.Add(s.RttMs!.Value); run = 0; continue; }
            if (s.Status == ProbeStatus.Timeout) timeouts++; else errors++;
            run++; maxRun = Math.Max(maxRun, run);
        }
        int sent = ordered.Count, received = rtts.Count;
        var sum = Stats.Summarize(rtts);
        return new LatencyStats(sent, received, sent - received, timeouts, errors, LossPercent(sent, received),
            sum.Min, sum.Mean, sum.Median, sum.Max, sum.P95, sum.P99, sum.StdDev,
            MeanAbsoluteDifference(rtts), Rfc3550Jitter(rtts), maxRun);
    }

    /// <summary>Estatísticas a partir de RTTs (null = perdido), na ordem de envio.</summary>
    public static LatencyStats Compute(IReadOnlyList<double?> rttsInOrder)
    {
        var samples = rttsInOrder.Select((r, i) => new LatencySample("", "", i, default,
            r is null ? ProbeStatus.Timeout : ProbeStatus.Success, r, null)).ToList();
        return Compute(samples);
    }
}
