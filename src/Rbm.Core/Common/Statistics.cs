namespace Rbm.Core;

/// <summary>Resumo estatístico de um conjunto de amostras.</summary>
public sealed record StatSummary(
    int Count, double Mean, double Median, double StdDev, double Min, double Max,
    double P1, double P5, double P95, double P99, double CoefficientOfVariation,
    double Ci95Low, double Ci95High)
{
    public static readonly StatSummary Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
}

/// <summary>Resultado de comparação antes/depois.</summary>
public sealed record ComparisonResult(
    StatSummary Before, StatSummary After, double DeltaPercent, double PValue, bool Significant, string Verdict);

public static class Stats
{
    public static StatSummary Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return StatSummary.Empty;
        var sorted = samples.Where(double.IsFinite).OrderBy(x => x).ToArray();
        int n = sorted.Length;
        if (n == 0) return StatSummary.Empty;
        double mean = sorted.Average();
        double sd = n > 1 ? Math.Sqrt(sorted.Sum(x => (x - mean) * (x - mean)) / (n - 1)) : 0;
        double half = n > 1 ? TCritical95(n - 1) * sd / Math.Sqrt(n) : double.NaN;
        return new StatSummary(n, mean, Percentile(sorted, 50), sd, sorted[0], sorted[^1],
            Percentile(sorted, 1), Percentile(sorted, 5), Percentile(sorted, 95), Percentile(sorted, 99),
            mean != 0 ? sd / Math.Abs(mean) : double.NaN, mean - half, mean + half);
    }

    /// <summary>Percentil com interpolação linear (método R-7 / Excel PERCENTILE.INC). Exige array ordenado.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>
    /// Compara duas séries com teste t de Welch (bicaudal). <paramref name="higherIsBetter"/> define o sentido.
    /// Só declara ganho/perda se p &lt; 0.05 E a variação exceder <paramref name="minMeaningfulPercent"/>.
    /// </summary>
    public static ComparisonResult Compare(IReadOnlyList<double> before, IReadOnlyList<double> after, bool higherIsBetter, double minMeaningfulPercent = 2.0)
    {
        var a = Summarize(before);
        var b = Summarize(after);
        if (a.Count < 2 || b.Count < 2)
            return new ComparisonResult(a, b, double.NaN, double.NaN, false, "Amostras insuficientes para comparação (mínimo 2 por lado).");

        double delta = a.Mean != 0 ? (b.Mean - a.Mean) / Math.Abs(a.Mean) * 100.0 : double.NaN;
        double va = a.StdDev * a.StdDev / a.Count, vb = b.StdDev * b.StdDev / b.Count;
        double p;
        if (va + vb == 0) p = a.Mean == b.Mean ? 1.0 : 0.0;
        else
        {
            double t = (b.Mean - a.Mean) / Math.Sqrt(va + vb);
            double df = (va + vb) * (va + vb) / ((va * va) / (a.Count - 1) + (vb * vb) / (b.Count - 1));
            p = 2 * (1 - StudentTCdf(Math.Abs(t), df));
        }
        bool sig = p < 0.05 && Math.Abs(delta) >= minMeaningfulPercent;
        string verdict;
        if (!sig) verdict = $"Sem diferença estatisticamente significativa (Δ {delta:+0.0;-0.0}%, p={p:0.000}). Trate como variação normal.";
        else
        {
            bool improved = higherIsBetter ? delta > 0 : delta < 0;
            verdict = improved
                ? $"Melhora consistente de {Math.Abs(delta):0.0}% (p={p:0.000})."
                : $"Piora consistente de {Math.Abs(delta):0.0}% (p={p:0.000}). Considere reverter.";
        }
        return new ComparisonResult(a, b, delta, p, sig, verdict);
    }

    // Valor crítico t bicaudal 95% (aproximação Cornish-Fisher, precisa o bastante p/ df >= 1).
    public static double TCritical95(int df)
    {
        if (df <= 0) return double.NaN;
        double[] table = { 12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228, 2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086, 2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042 };
        if (df <= 30) return table[df - 1];
        const double z = 1.959964;
        double g1 = (z * z * z + z) / 4, g2 = (5 * Math.Pow(z, 5) + 16 * z * z * z + 3 * z) / 96;
        return z + g1 / df + g2 / (df * df);
    }

    /// <summary>CDF da distribuição t de Student via função beta incompleta regularizada.</summary>
    public static double StudentTCdf(double t, double df)
    {
        double x = df / (df + t * t);
        double ib = RegularizedIncompleteBeta(df / 2.0, 0.5, x);
        return t >= 0 ? 1 - 0.5 * ib : 0.5 * ib;
    }

    private static double RegularizedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double lbeta = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x);
        if (x < (a + 1) / (a + b + 2)) return Math.Exp(lbeta) * BetaCf(a, b, x) / a;
        return 1 - Math.Exp(lbeta) * BetaCf(b, a, 1 - x) / b;
    }

    private static double BetaCf(double a, double b, double x)
    {
        const int maxIt = 200; const double eps = 3e-14, fpmin = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1, c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1 / d; double h = d;
        for (int m = 1; m <= maxIt; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1 + aa / c; if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1 / d; h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1 + aa / c; if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1 / d; double del = d * c; h *= del;
            if (Math.Abs(del - 1) < eps) break;
        }
        return h;
    }

    private static double LogGamma(double x)
    {
        double[] c = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++) ser += c[j] / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
