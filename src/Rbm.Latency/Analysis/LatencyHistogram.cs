namespace Rbm.Latency;

/// <summary>
/// Faixas fixas do histograma de duração de DPC/ISR (µs):
/// &lt;50, 50–100, 100–250, 250–500, 500–1000, 1000–2000, &gt;2000.
/// Limite inferior inclusivo, superior exclusivo (ex.: 500 µs cai em "500–1000").
/// </summary>
public static class LatencyHistogram
{
    /// <summary>Limites superiores (exclusivos) das faixas; a última faixa é aberta.</summary>
    public static readonly double[] UpperBoundsUs = { 50, 100, 250, 500, 1000, 2000 };

    public static readonly string[] Labels = { "<50 µs", "50–100 µs", "100–250 µs", "250–500 µs", "500–1000 µs", "1000–2000 µs", ">2000 µs" };

    public static int BucketCount => Labels.Length;

    /// <summary>Índice da faixa para uma duração em µs.</summary>
    public static int BucketIndex(double us)
    {
        for (int i = 0; i < UpperBoundsUs.Length; i++)
            if (us < UpperBoundsUs[i]) return i;
        return UpperBoundsUs.Length;
    }

    public static double LowerBound(int index) => index == 0 ? 0 : UpperBoundsUs[index - 1];
    public static double? UpperBound(int index) => index < UpperBoundsUs.Length ? UpperBoundsUs[index] : null;

    /// <summary>Monta as faixas a partir das contagens.</summary>
    public static IReadOnlyList<HistogramBucket> Build(IReadOnlyList<long> counts)
    {
        if (counts.Count != BucketCount) throw new ArgumentException($"Esperadas {BucketCount} contagens.", nameof(counts));
        var list = new HistogramBucket[BucketCount];
        for (int i = 0; i < BucketCount; i++) list[i] = new HistogramBucket(Labels[i], LowerBound(i), UpperBound(i), counts[i]);
        return list;
    }

    public static IReadOnlyList<HistogramBucket> EmptyBuckets() => Build(new long[BucketCount]);

    /// <summary>Histograma de uma lista simples de durações (útil em testes e relatórios).</summary>
    public static IReadOnlyList<HistogramBucket> FromSamples(IEnumerable<double> durationsUs)
    {
        var c = new long[BucketCount];
        foreach (var d in durationsUs) c[BucketIndex(d)]++;
        return Build(c);
    }
}
