namespace Rbm.Processes;

/// <summary>Impacto de um processo ao longo de uma janela de amostras.</summary>
public sealed record ProcessImpact(
    ProcessKey Key,
    string Name,
    string? ImagePath,
    int SampleCount,
    TimeSpan ObservedFor,
    double AvgCpuPercent,
    double PeakCpuPercent,
    double AvgIoBytesPerSec,
    double PeakIoBytesPerSec,
    long WorkingSetStart,
    long WorkingSetEnd,
    double WorkingSetSlopeBytesPerMin,
    long PrivateBytesStart,
    long PrivateBytesEnd,
    double PrivateBytesSlopeBytesPerMin,
    // Fração dos intervalos em que a memória privada cresceu (1 = crescimento contínuo).
    double GrowthMonotonicity,
    bool PossibleLeak,
    double ImpactScore,
    IReadOnlyList<string> Notes);

public sealed record BackgroundImpactReport(
    DateTimeOffset From,
    DateTimeOffset To,
    int SnapshotCount,
    IReadOnlyList<ProcessImpact> ByImpact,
    IReadOnlyList<ProcessImpact> TopByAverageCpu,
    IReadOnlyList<ProcessImpact> TopByPeakCpu,
    IReadOnlyList<ProcessImpact> TopByIo,
    IReadOnlyList<ProcessImpact> TopByMemoryGrowth,
    IReadOnlyList<string> Notes);

public sealed record ImpactAnalysisOptions
{
    public int TopN { get; init; } = 10;
    /// <summary>Nomes a ignorar (ex.: o jogo em primeiro plano). Idle (PID 0) é sempre ignorado.</summary>
    public IReadOnlySet<string>? ExcludeNames { get; init; }
    public int MinSamplesForLeak { get; init; } = 6;
    public TimeSpan MinWindowForLeak { get; init; } = TimeSpan.FromMinutes(2);
    public long LeakMinGrowthBytes { get; init; } = 50L * 1024 * 1024;
    public double LeakMinSlopeBytesPerMin { get; init; } = 5.0 * 1024 * 1024;
    public double LeakMinMonotonicity { get; init; } = 0.8;
}

/// <summary>
/// "Análise de impacto em segundo plano": classifica processos por CPU média/pico, IO e crescimento de memória
/// (inclinação por regressão linear + monotonicidade) a partir de amostras reais de <see cref="ProcessSampler"/>.
/// As notas são descritivas; nenhuma ação é tomada automaticamente.
/// </summary>
public static class BackgroundImpactAnalyzer
{
    public static BackgroundImpactReport Analyze(IReadOnlyList<ProcessSnapshot> window, ImpactAnalysisOptions? options = null)
    {
        options ??= new ImpactAnalysisOptions();
        var notes = new List<string>();
        if (window.Count == 0)
            return new BackgroundImpactReport(DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, [], [], [], [], [], ["Nenhuma amostra coletada."]);

        var ordered = window.OrderBy(s => s.Timestamp).ToList();
        DateTimeOffset from = ordered[0].Timestamp, to = ordered[^1].Timestamp;
        if (ordered.Count(s => s.RatesAvailable) == 0) notes.Add("Sem amostras com taxas (CPU/IO): colete ao menos duas amostras.");
        if (to - from < options.MinWindowForLeak) notes.Add($"Janela curta ({(to - from).TotalSeconds:0} s): conclusões sobre crescimento de memória são pouco confiáveis.");

        var series = new Dictionary<ProcessKey, List<(DateTimeOffset t, ProcessEntry e)>>();
        foreach (var s in ordered)
            foreach (var p in s.Processes)
            {
                if (p.Pid == 0) continue;
                if (options.ExcludeNames is { } ex && (ex.Contains(p.Name) || ex.Contains(Path.GetFileNameWithoutExtension(p.Name)))) continue;
                if (!series.TryGetValue(p.Key, out var list)) series[p.Key] = list = new();
                list.Add((s.Timestamp, p));
            }

        var impacts = new List<ProcessImpact>(series.Count);
        foreach (var (key, list) in series) impacts.Add(Evaluate(key, list, options));

        int n = Math.Max(1, options.TopN);
        var byImpact = impacts.OrderByDescending(i => i.ImpactScore).ToList();
        return new BackgroundImpactReport(from, to, ordered.Count, byImpact,
            impacts.Where(i => i.AvgCpuPercent > 0).OrderByDescending(i => i.AvgCpuPercent).Take(n).ToList(),
            impacts.Where(i => i.PeakCpuPercent > 0).OrderByDescending(i => i.PeakCpuPercent).Take(n).ToList(),
            impacts.Where(i => i.AvgIoBytesPerSec > 0).OrderByDescending(i => i.AvgIoBytesPerSec).Take(n).ToList(),
            impacts.Where(i => i.PrivateBytesSlopeBytesPerMin > 0 || i.WorkingSetSlopeBytesPerMin > 0)
                .OrderByDescending(i => Math.Max(i.PrivateBytesSlopeBytesPerMin, i.WorkingSetSlopeBytesPerMin)).Take(n).ToList(),
            notes);
    }

    private static ProcessImpact Evaluate(ProcessKey key, List<(DateTimeOffset t, ProcessEntry e)> s, ImpactAnalysisOptions o)
    {
        var last = s[^1].e;
        var cpu = s.Where(x => x.e.CpuPercent is not null).Select(x => x.e.CpuPercent!.Value).ToList();
        var io = s.Where(x => x.e.IoTotalBytesPerSec is not null).Select(x => x.e.IoTotalBytesPerSec!.Value).ToList();
        double avgCpu = cpu.Count > 0 ? cpu.Average() : 0, peakCpu = cpu.Count > 0 ? cpu.Max() : 0;
        double avgIo = io.Count > 0 ? io.Average() : 0, peakIo = io.Count > 0 ? io.Max() : 0;
        TimeSpan observed = s[^1].t - s[0].t;

        double wsSlope = Slope(s, x => x.WorkingSetBytes);
        double pvSlope = Slope(s, x => x.PrivateBytes);
        int ups = 0, deltas = 0;
        for (int i = 1; i < s.Count; i++)
        {
            long d = s[i].e.PrivateBytes - s[i - 1].e.PrivateBytes;
            if (d == 0) continue;
            deltas++;
            if (d > 0) ups++;
        }
        double mono = deltas > 0 ? ups / (double)deltas : 0;
        long pvGrowth = last.PrivateBytes - s[0].e.PrivateBytes;
        long wsGrowth = last.WorkingSetBytes - s[0].e.WorkingSetBytes;
        bool leak = s.Count >= o.MinSamplesForLeak && observed >= o.MinWindowForLeak
            && pvGrowth >= o.LeakMinGrowthBytes && pvSlope >= o.LeakMinSlopeBytesPerMin && mono >= o.LeakMinMonotonicity;

        var notes = new List<string>();
        if (avgCpu >= 5) notes.Add($"Uso médio de CPU de {avgCpu:0.#}% em segundo plano — consumo contínuo relevante.");
        else if (peakCpu >= 50) notes.Add($"Uso intermitente: picos de até {peakCpu:0.#}% de CPU, média baixa ({avgCpu:0.#}%).");
        if (avgIo >= 1024 * 1024) notes.Add($"Atividade de disco média de {avgIo / 1048576.0:0.#} MB/s.");
        if (leak)
            notes.Add($"Memória privada cresceu de {Mb(s[0].e.PrivateBytes)} para {Mb(last.PrivateBytes)} (≈{pvSlope / 1048576.0:0.#} MB/min) quase sem quedas — " +
                      "possível vazamento de memória. Observe por mais tempo antes de concluir; reiniciar o aplicativo libera a memória.");
        else if (wsGrowth >= o.LeakMinGrowthBytes && wsSlope >= o.LeakMinSlopeBytesPerMin)
            notes.Add($"Working set cresceu {Mb(wsGrowth)} na janela (≈{wsSlope / 1048576.0:0.#} MB/min); pode ser cache normal do aplicativo.");
        if (cpu.Count == 0) notes.Add("CPU/IO indisponíveis para este processo (primeira amostra ou acesso negado).");

        double score = avgCpu + peakCpu * 0.1 + avgIo / 1048576.0 * 2 + (leak ? 10 : 0);
        return new ProcessImpact(key, last.Name, last.ImagePath, s.Count, observed, avgCpu, peakCpu, avgIo, peakIo,
            s[0].e.WorkingSetBytes, last.WorkingSetBytes, wsSlope, s[0].e.PrivateBytes, last.PrivateBytes, pvSlope, mono, leak, score, notes);
    }

    /// <summary>Inclinação (bytes/min) por mínimos quadrados.</summary>
    public static double Slope(IReadOnlyList<(DateTimeOffset t, ProcessEntry e)> s, Func<ProcessEntry, long> value)
    {
        if (s.Count < 2) return 0;
        var t0 = s[0].t;
        double n = s.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, e) in s)
        {
            double x = (t - t0).TotalMinutes, y = value(e);
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double den = n * sxx - sx * sx;
        return Math.Abs(den) < 1e-12 ? 0 : (n * sxy - sx * sy) / den;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0} MB";
}
