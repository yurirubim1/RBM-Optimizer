namespace Rbm.Latency;

/// <summary>
/// Acumulador de durações (µs) com memória constante: contagem/soma/mín/máx exatos e um histograma fino
/// para estimar percentis sem guardar todas as amostras (capturas longas podem ter dezenas de milhões de eventos).
/// Resolução do histograma fino: 1 µs em [0, 1000); 10 µs em [1000, 10000); 100 µs em [10000, 100000); acima disso, uma faixa aberta.
/// Percentis são interpolados dentro da faixa e limitados a [mín, máx] observados.
/// </summary>
public sealed class DurationDistribution
{
    private const int Tier1 = 1000;          // 0..999 µs, passo 1
    private const int Tier2 = 900;           // 1000..9990 µs, passo 10
    private const int Tier3 = 900;           // 10000..99900 µs, passo 100
    private const int FineCount = Tier1 + Tier2 + Tier3 + 1;

    private int[]? _fine; // alocado sob demanda
    private readonly long[] _coarse = new long[LatencyHistogram.BucketCount];

    public long Count { get; private set; }
    public double TotalUs { get; private set; }
    public double MinUs { get; private set; } = double.PositiveInfinity;
    public double MaxUs { get; private set; }

    public void Add(double us)
    {
        if (!double.IsFinite(us) || us < 0) throw new ArgumentOutOfRangeException(nameof(us));
        Count++;
        TotalUs += us;
        if (us < MinUs) MinUs = us;
        if (us > MaxUs) MaxUs = us;
        _coarse[LatencyHistogram.BucketIndex(us)]++;
        (_fine ??= new int[FineCount])[FineIndex(us)]++;
    }

    /// <summary>Soma outra distribuição a esta (usado para agregar rotinas → módulo).</summary>
    public void Merge(DurationDistribution other)
    {
        if (other.Count == 0) return;
        Count += other.Count;
        TotalUs += other.TotalUs;
        if (other.MinUs < MinUs) MinUs = other.MinUs;
        if (other.MaxUs > MaxUs) MaxUs = other.MaxUs;
        for (int i = 0; i < _coarse.Length; i++) _coarse[i] += other._coarse[i];
        if (other._fine is { } of)
        {
            _fine ??= new int[FineCount];
            for (int i = 0; i < FineCount; i++) _fine[i] += of[i];
        }
    }

    public double Mean => Count == 0 ? 0 : TotalUs / Count;

    /// <summary>Contagens das 7 faixas do <see cref="LatencyHistogram"/>.</summary>
    public IReadOnlyList<long> CoarseCounts => _coarse;

    /// <summary>Percentil (0..100) estimado pelo histograma fino.</summary>
    public double Percentile(double p)
    {
        if (Count == 0 || _fine is null) return 0;
        if (p <= 0) return MinUs;
        if (p >= 100) return MaxUs;
        // posição (1-based) da amostra de ordem ceil(p/100 * n) — método "nearest rank"
        long target = (long)Math.Ceiling(p / 100.0 * Count);
        if (target < 1) target = 1;
        long cum = 0;
        for (int i = 0; i < FineCount; i++)
        {
            int c = _fine[i];
            if (c == 0) continue;
            if (cum + c >= target)
            {
                var (lo, hi) = FineRange(i);
                if (double.IsPositiveInfinity(hi)) hi = Math.Max(lo, MaxUs);
                double frac = (target - cum - 0.5) / c; // interpolação linear dentro da faixa
                double v = lo + (hi - lo) * Math.Clamp(frac, 0, 1);
                return Math.Clamp(v, MinUs, MaxUs);
            }
            cum += c;
        }
        return MaxUs;
    }

    public LatencyStats ToStats() => Count == 0
        ? LatencyStats.Empty
        : new LatencyStats(Count, TotalUs, MinUs, MaxUs, Mean, Percentile(50), Percentile(99), LatencyHistogram.Build(_coarse));

    internal static int FineIndex(double us)
    {
        if (us < 1000) return (int)us;
        if (us < 10000) return Tier1 + (int)((us - 1000) / 10);
        if (us < 100000) return Tier1 + Tier2 + (int)((us - 10000) / 100);
        return FineCount - 1;
    }

    internal static (double lo, double hi) FineRange(int index)
    {
        if (index < Tier1) return (index, index + 1);
        if (index < Tier1 + Tier2) { double lo = 1000 + (index - Tier1) * 10.0; return (lo, lo + 10); }
        if (index < Tier1 + Tier2 + Tier3) { double lo = 10000 + (index - Tier1 - Tier2) * 100.0; return (lo, lo + 100); }
        return (100000, double.PositiveInfinity);
    }
}
