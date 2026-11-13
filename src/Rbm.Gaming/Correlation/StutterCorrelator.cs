using Rbm.Core;
using Rbm.Core.Telemetry;
using Rbm.Gaming.Frames;

namespace Rbm.Gaming.Correlation;

/// <summary>Condição de sistema avaliada em cada janela de telemetria.</summary>
public enum TelemetryCondition
{
    CpuTotalHigh,
    GpuUtilHigh,
    VramNearFull,
    RamAvailableLow,
    HardFaultsHigh,
    DiskActiveHigh,
    CpuClockDrop,
    CpuPerformanceLimited,
    CpuTempHigh,
    GpuTempHigh,
    DpcHigh,
}

/// <summary>Limiares (configuráveis) das condições e critérios para destacar uma associação.</summary>
public sealed record CorrelationOptions(
    double CpuTotalPercent = 90,
    double GpuUtil3DPercent = 97,
    double VramUsedFraction = 0.95,
    double RamAvailablePercent = 10,
    double HardFaultsPerSec = 500,
    double DiskActivePercent = 90,
    double CpuClockDropFraction = 0.85,
    double CpuPerfLimitPercent = 90,
    double CpuTempC = 90,
    double GpuTempC = 85,
    double GpuHotspotC = 105,
    double DpcPercent = 5,
    int MinStutterWindows = 2,
    double MinStutterRate = 0.3,
    double MinLift = 2.0)
{
    public static readonly CorrelationOptions Default = new();
}

/// <summary>Associação temporal entre stutters e uma condição (contagens + lift). Nunca indica causa.</summary>
/// <param name="StutterRate">Fração das janelas com stutter em que a condição estava presente.</param>
/// <param name="BaselineRate">Fração das janelas sem stutter (linha de base) em que a condição estava presente.</param>
/// <param name="Lift">StutterRate ÷ BaselineRate; null quando a linha de base é 0 ou não há dados.</param>
public sealed record ConditionAssociation(
    TelemetryCondition Condition,
    string LabelPt,
    int StutterWindowsWithCondition,
    int StutterWindowsEvaluated,
    int BaselineWindowsWithCondition,
    int BaselineWindowsEvaluated,
    double? StutterRate,
    double? BaselineRate,
    double? Lift,
    bool Notable,
    Confidence Confidence,
    string TextPt);

/// <summary>O que estava ativo durante um agrupamento de stutters.</summary>
public sealed record ClusterCorrelation(StutterCluster Cluster, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<TelemetryCondition> ActiveConditions, string TextPt);

public sealed record StutterCorrelationReport(
    int TelemetryWindows,
    int StutterWindows,
    int BaselineWindows,
    IReadOnlyList<ConditionAssociation> Associations,
    IReadOnlyList<ClusterCorrelation> Clusters,
    string SummaryPt)
{
    public const string DisclaimerPt =
        "Associação temporal: mostra condições que coincidiram com os stutters com mais frequência do que no resto da sessão. " +
        "Não prova causa. O alinhamento entre o relógio do PresentMon e a telemetria tem incerteza de cerca de ±1 amostra.";
}

/// <summary>
/// Correlação entre stutters e telemetria do sistema (amostras de ~1 s). Função pura.
/// Cada amostra cobre a janela (t anterior, t]. Janelas com ≥ 1 stutter são comparadas com as janelas sem stutter
/// (linha de base) dentro do período da captura; o resultado é contagem + lift, nunca "causa".
/// </summary>
public static class StutterCorrelator
{
    public static string Label(TelemetryCondition c, CorrelationOptions o) => c switch
    {
        TelemetryCondition.CpuTotalHigh => $"CPU total > {o.CpuTotalPercent:0}%",
        TelemetryCondition.GpuUtilHigh => $"GPU 3D > {o.GpuUtil3DPercent:0}%",
        TelemetryCondition.VramNearFull => $"VRAM ≥ {o.VramUsedFraction:P0} ocupada",
        TelemetryCondition.RamAvailableLow => $"RAM disponível < {o.RamAvailablePercent:0}%",
        TelemetryCondition.HardFaultsHigh => $"Hard faults > {o.HardFaultsPerSec:0}/s",
        TelemetryCondition.DiskActiveHigh => $"Disco ativo > {o.DiskActivePercent:0}%",
        TelemetryCondition.CpuClockDrop => $"Clock efetivo da CPU < {o.CpuClockDropFraction:P0} da mediana da sessão",
        TelemetryCondition.CpuPerformanceLimited => $"Limite de desempenho da CPU < {o.CpuPerfLimitPercent:0}%",
        TelemetryCondition.CpuTempHigh => $"Temperatura da CPU ≥ {o.CpuTempC:0} °C",
        TelemetryCondition.GpuTempHigh => $"Temperatura da GPU ≥ {o.GpuTempC:0} °C (hotspot ≥ {o.GpuHotspotC:0} °C)",
        TelemetryCondition.DpcHigh => $"Tempo de DPC > {o.DpcPercent:0}%",
        _ => c.ToString(),
    };

    /// <summary>Avalia uma condição numa amostra. null = dado indisponível nesta amostra.</summary>
    public static bool? Evaluate(TelemetryCondition c, TelemetrySnapshot s, CorrelationOptions o, double? sessionMedianClockMHz)
    {
        switch (c)
        {
            case TelemetryCondition.CpuTotalHigh: return s.CpuTotalPercent > o.CpuTotalPercent;
            case TelemetryCondition.GpuUtilHigh: return s.Gpus.Count == 0 ? null : s.Gpus.Any(g => g.Utilization3DPercent > o.GpuUtil3DPercent);
            case TelemetryCondition.VramNearFull:
                var withVram = s.Gpus.Where(g => g.DedicatedTotalBytes > 0).ToList();
                return withVram.Count == 0 ? null : withVram.Any(g => (double)g.DedicatedUsedBytes / g.DedicatedTotalBytes >= o.VramUsedFraction);
            case TelemetryCondition.RamAvailableLow:
                return s.MemTotalBytes <= 0 ? null : s.MemAvailableBytes * 100.0 / s.MemTotalBytes < o.RamAvailablePercent;
            case TelemetryCondition.HardFaultsHigh: return s.HardFaultsPerSec > o.HardFaultsPerSec;
            case TelemetryCondition.DiskActiveHigh: return s.Disks.Count == 0 ? null : s.Disks.Any(d => d.ActivePercent > o.DiskActivePercent);
            case TelemetryCondition.CpuClockDrop:
                if (!s.CpuEffectiveClockMHz.Available || sessionMedianClockMHz is not > 0) return null;
                return s.CpuEffectiveClockMHz.Value < o.CpuClockDropFraction * sessionMedianClockMHz.Value;
            case TelemetryCondition.CpuPerformanceLimited:
                return s.CpuPerformanceLimitPercent.Available ? s.CpuPerformanceLimitPercent.Value < o.CpuPerfLimitPercent : null;
            case TelemetryCondition.CpuTempHigh:
                return s.CpuTemperatureC.Available ? s.CpuTemperatureC.Value >= o.CpuTempC : null;
            case TelemetryCondition.GpuTempHigh:
                var temps = s.Gpus.Where(g => g.TemperatureC.Available || g.HotspotC.Available).ToList();
                return temps.Count == 0 ? null : temps.Any(g => (g.TemperatureC.Available && g.TemperatureC.Value >= o.GpuTempC) || (g.HotspotC.Available && g.HotspotC.Value >= o.GpuHotspotC));
            case TelemetryCondition.DpcHigh: return s.DpcTimePercent > o.DpcPercent;
            default: return null;
        }
    }

    /// <summary>
    /// Correlaciona stutters com telemetria.
    /// </summary>
    /// <param name="captureStart">Início da gravação do PresentMon (relógio local) — origem de <see cref="StutterEvent.TimeMs"/>.</param>
    /// <param name="captureDurationMs">Duração da captura (ms).</param>
    /// <param name="stutters">Resultado de <see cref="FrameAnalyzer.DetectStutters"/>.</param>
    /// <param name="telemetry">Amostras de telemetria (~1 s) do período.</param>
    public static StutterCorrelationReport Correlate(
        DateTimeOffset captureStart, double captureDurationMs, StutterSummary stutters,
        IReadOnlyList<TelemetrySnapshot> telemetry, CorrelationOptions? options = null)
    {
        options ??= CorrelationOptions.Default;
        var conds = Enum.GetValues<TelemetryCondition>();
        var captureEnd = captureStart.AddMilliseconds(Math.Max(0, captureDurationMs));
        var snaps = telemetry.OrderBy(s => s.Timestamp).ToList();
        if (snaps.Count == 0)
            return new StutterCorrelationReport(0, 0, 0, [], [], "Sem telemetria disponível para o período da captura.");

        // Intervalo típico entre amostras (para a primeira janela)
        var deltas = new List<double>();
        for (int i = 1; i < snaps.Count; i++) deltas.Add((snaps[i].Timestamp - snaps[i - 1].Timestamp).TotalMilliseconds);
        double typical = deltas.Count > 0 ? Stats.Summarize(deltas).Median : 1000;
        if (!(typical > 0)) typical = 1000;

        // Janelas (início, fim, amostra) que se sobrepõem à captura
        var windows = new List<(DateTimeOffset start, DateTimeOffset end, TelemetrySnapshot s)>();
        for (int i = 0; i < snaps.Count; i++)
        {
            var end = snaps[i].Timestamp;
            var start = i == 0 ? end.AddMilliseconds(-typical) : snaps[i - 1].Timestamp;
            if ((end - start).TotalMilliseconds > 5 * typical) start = end.AddMilliseconds(-typical); // lacuna na amostragem
            if (end <= captureStart || start >= captureEnd) continue;
            windows.Add((start, end, snaps[i]));
        }
        if (windows.Count == 0)
            return new StutterCorrelationReport(0, 0, 0, [], [], "A telemetria não cobre o período da captura.");

        var clocks = windows.Where(w => w.s.CpuEffectiveClockMHz.Available).Select(w => w.s.CpuEffectiveClockMHz.Value).ToList();
        double? medianClock = clocks.Count > 0 ? Stats.Summarize(clocks).Median : null;

        // Stutters por janela
        var stutterCount = new int[windows.Count];
        foreach (var e in stutters.Events)
        {
            if (!double.IsFinite(e.TimeMs)) continue;
            var t = captureStart.AddMilliseconds(e.TimeMs);
            int idx = FindWindow(windows, t);
            if (idx >= 0) stutterCount[idx]++;
        }

        // Matriz de condições
        var eval = new bool?[windows.Count, conds.Length];
        for (int w = 0; w < windows.Count; w++)
            for (int c = 0; c < conds.Length; c++)
                eval[w, c] = Evaluate(conds[c], windows[w].s, options, medianClock);

        int nStutterW = stutterCount.Count(x => x > 0);
        int nBaseW = windows.Count - nStutterW;
        var assoc = new List<ConditionAssociation>();
        for (int c = 0; c < conds.Length; c++)
        {
            int a = 0, ns = 0, b = 0, nb = 0;
            for (int w = 0; w < windows.Count; w++)
            {
                if (eval[w, c] is not { } v) continue;
                if (stutterCount[w] > 0) { ns++; if (v) a++; } else { nb++; if (v) b++; }
            }
            if (ns == 0 && nb == 0) continue; // condição sem dados
            double? sr = ns > 0 ? (double)a / ns : null;
            double? br = nb > 0 ? (double)b / nb : null;
            double? lift = sr is not null && br is > 0 ? sr / br : null;
            bool notable = a >= options.MinStutterWindows && sr >= options.MinStutterRate &&
                           ((lift is { } l && l >= options.MinLift) || (br == 0 && nb >= 5));
            var conf = ns >= 30 && nb >= 30 ? Confidence.High : ns >= 10 && nb >= 10 ? Confidence.Medium : Confidence.Low;
            string label = Label(conds[c], options);
            string text = ns == 0
                ? $"{label}: nenhuma janela com stutter tinha esse dado."
                : $"{label}: presente em {a} de {ns} janelas com stutter ({sr:P0})" +
                  (nb > 0 ? $" contra {b} de {nb} janelas sem stutter ({br:P0})" : "") +
                  (lift is { } lf ? $" — lift {lf:0.0}×." : br == 0 && a > 0 ? " — nunca ocorreu fora dos stutters." : ".") +
                  (notable ? " Associação temporal relevante (não prova causa)." : "");
            assoc.Add(new ConditionAssociation(conds[c], label, a, ns, b, nb, sr, br, lift, notable, conf, text));
        }

        // Por agrupamento
        var clusterList = new List<ClusterCorrelation>();
        foreach (var cl in stutters.Clusters)
        {
            var cs = captureStart.AddMilliseconds(cl.StartMs);
            var ce = captureStart.AddMilliseconds(cl.EndMs);
            var active = new HashSet<TelemetryCondition>();
            bool any = false;
            for (int w = 0; w < windows.Count; w++)
            {
                // janela (start, end] intersecta [cs, ce]
                if (!(windows[w].end >= cs && windows[w].start < ce)) continue;
                any = true;
                for (int c = 0; c < conds.Length; c++) if (eval[w, c] == true) active.Add(conds[c]);
            }
            var list = active.OrderBy(x => x).ToList();
            string text = !any ? "Sem telemetria neste intervalo."
                : list.Count == 0 ? "Nenhuma das condições monitoradas estava ativa."
                : "Ao mesmo tempo: " + string.Join("; ", list.Select(x => Label(x, options))) + ".";
            clusterList.Add(new ClusterCorrelation(cl, cs, ce, list, text));
        }

        string summary;
        if (stutters.Count == 0) summary = "Nenhum stutter detectado — nada a correlacionar.";
        else if (nStutterW == 0) summary = "Os stutters ocorreram fora do período coberto pela telemetria.";
        else
        {
            var top = assoc.Where(x => x.Notable).OrderByDescending(x => x.Lift ?? double.MaxValue).ThenByDescending(x => x.StutterRate).ToList();
            summary = top.Count == 0
                ? $"Nenhuma condição monitorada se destacou nas {nStutterW} janelas com stutter (comparadas a {nBaseW} sem stutter). Causas possíveis fora da telemetria: compilação de shaders, streaming de assets, rede, o próprio jogo."
                : $"Associações temporais em {nStutterW} janelas com stutter: " + string.Join(" ", top.Select(x => x.TextPt));
        }
        return new StutterCorrelationReport(windows.Count, nStutterW, nBaseW, assoc, clusterList, summary);
    }

    private static int FindWindow(List<(DateTimeOffset start, DateTimeOffset end, TelemetrySnapshot s)> windows, DateTimeOffset t)
    {
        int lo = 0, hi = windows.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (t <= windows[mid].start) hi = mid - 1;
            else if (t > windows[mid].end) lo = mid + 1;
            else return mid;
        }
        return -1;
    }
}
