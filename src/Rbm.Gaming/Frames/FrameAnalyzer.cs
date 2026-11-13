using Rbm.Core;

namespace Rbm.Gaming.Frames;

/// <summary>
/// Análise de frametimes reais capturados pelo PresentMon. Funções puras (sem I/O) — fáceis de testar.
/// Nada é estimado: métricas indisponíveis ficam NaN/null e o texto diz "indeterminado".
/// </summary>
public static class FrameAnalyzer
{
    /// <summary>Análise completa de uma captura já parseada.</summary>
    public static FrameAnalysis Analyze(ParsedFrameCapture capture, FrameAnalysisOptions? options = null)
    {
        options ??= FrameAnalysisOptions.Default;
        var frames = capture.Frames;
        var ft = frames.Where(f => double.IsFinite(f.FrameTimeMs) && f.FrameTimeMs > 0).Select(f => f.FrameTimeMs).ToArray();
        var metrics = ComputeMetrics(ft);
        var dispFt = frames.Where(f => f.Displayed == true && double.IsFinite(f.DisplayChangeMs) && f.DisplayChangeMs > 0).Select(f => f.DisplayChangeMs).ToArray();
        FrameMetrics? displayed = dispFt.Length >= 2 ? ComputeMetrics(dispFt) : null;

        var warnings = capture.Warnings.ToList();
        if (metrics.FrameCount < 2) warnings.Add("Quadros insuficientes para análise.");
        if (!metrics.Low01PercentReliable && metrics.FrameCount > 0) warnings.Add("Menos de 1000 quadros: o 0,1% low não é estatisticamente confiável.");

        var tearing = SummarizeTearing(frames);
        return new FrameAnalysis(
            capture.Layout, capture.Application, capture.ProcessId, capture.Runtime,
            metrics, displayed,
            BuildHistogram(ft, options.HistogramBucketMs),
            DetectStutters(frames, options.Stutter ?? StutterOptions.Default),
            SummarizePresentModes(frames),
            tearing,
            AdjustBoundForSync(AnalyzeBound(frames, options.BoundThreshold), tearing),
            PerSecondFps(frames),
            warnings);
    }

    /// <summary>
    /// Com V-Sync ligado (SyncInterval ≥ 1 predominante), aplicações que esperam o vsync/limitam FPS antes do Present()
    /// inflam o CPUBusy: um veredito "CPU" vira confiança baixa com ressalva explícita.
    /// </summary>
    public static BoundAnalysis AdjustBoundForSync(BoundAnalysis bound, TearingSummary tearing)
    {
        bool vsyncOn = tearing.SyncIntervals.Count > 0 && tearing.SyncIntervals[0].SyncInterval >= 1 && tearing.SyncIntervals[0].Percent >= 50;
        if (!vsyncOn || bound.Kind != BoundKind.CpuBound) return bound;
        return bound with
        {
            Confidence = Confidence.Low,
            LabelPt = "CPU ou V-Sync/limitador (indistinguível com V-Sync ligado)",
            ExplanationPt = bound.ExplanationPt + " Atenção: o V-Sync está ligado; aplicações que aguardam o vsync ou limitam o FPS antes do Present() aparecem como 'CPU ocupada'. Desligue o V-Sync/limitador para um diagnóstico confiável.",
        };
    }

    /// <summary>Métricas de FPS/frametime a partir de frametimes (ms). Valores não finitos ou ≤ 0 são ignorados.</summary>
    public static FrameMetrics ComputeMetrics(IReadOnlyList<double> frameTimesMs)
    {
        var sorted = frameTimesMs.Where(x => double.IsFinite(x) && x > 0).OrderBy(x => x).ToArray();
        int n = sorted.Length;
        if (n == 0) return FrameMetrics.Empty;
        double sum = 0; foreach (var x in sorted) sum += x;
        double mean = sum / n;
        double ss = 0; foreach (var x in sorted) ss += (x - mean) * (x - mean);
        double sd = n > 1 ? Math.Sqrt(ss / (n - 1)) : 0;
        double median = Stats.Percentile(sorted, 50);
        double p95 = Stats.Percentile(sorted, 95), p99 = Stats.Percentile(sorted, 99), p999 = Stats.Percentile(sorted, 99.9);
        return new FrameMetrics(
            n, sum / 1000.0, n * 1000.0 / sum, mean, median, 1000.0 / median, p95, p99, p999,
            1000.0 / p99, 1000.0 / p999,
            1000.0 / WorstMean(sorted, 0.01), 1000.0 / WorstMean(sorted, 0.001),
            sd, sorted[0], sorted[^1], n >= 1000);
    }

    /// <summary>Média dos maiores frametimes (fração <paramref name="fraction"/> do total, mínimo 1 quadro).</summary>
    private static double WorstMean(double[] sorted, double fraction)
    {
        int k = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
        double s = 0;
        for (int i = sorted.Length - k; i < sorted.Length; i++) s += sorted[i];
        return s / k;
    }

    /// <summary>
    /// Stutter = frametime &gt; Factor × mediana móvel dos N quadros anteriores E (frametime − mediana) &gt; MinDeltaMs.
    /// </summary>
    public static StutterSummary DetectStutters(IReadOnlyList<FrameRecord> frames, StutterOptions? options = null)
    {
        options ??= StutterOptions.Default;
        int w = Math.Max(3, options.WindowFrames);
        var window = new List<double>(w + 1);   // ordenada
        var fifo = new Queue<double>(w + 1);
        var events = new List<StutterEvent>();
        double total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            double ft = frames[i].FrameTimeMs;
            if (!double.IsFinite(ft) || ft <= 0) continue;
            total += ft;
            if (window.Count >= Math.Max(1, options.MinHistoryFrames))
            {
                double m = MedianSorted(window);
                if (ft > options.Factor * m && ft - m > options.MinDeltaMs)
                    events.Add(new StutterEvent(i, frames[i].TimeMs, ft, m));
            }
            InsertSorted(window, ft);
            fifo.Enqueue(ft);
            if (fifo.Count > w) RemoveSorted(window, fifo.Dequeue());
        }

        var clusters = new List<StutterCluster>();
        foreach (var e in events.Where(e => double.IsFinite(e.TimeMs)).OrderBy(e => e.TimeMs))
        {
            if (clusters.Count > 0 && e.TimeMs - clusters[^1].EndMs <= options.ClusterGapMs)
            {
                var c = clusters[^1];
                clusters[^1] = c with { EndMs = e.TimeMs, Count = c.Count + 1, WorstFrameTimeMs = Math.Max(c.WorstFrameTimeMs, e.FrameTimeMs) };
            }
            else clusters.Add(new StutterCluster(e.TimeMs, e.TimeMs, 1, e.FrameTimeMs));
        }
        double minutes = total / 60000.0;
        return new StutterSummary(events.Count, minutes > 0 ? events.Count / minutes : double.NaN, events, clusters, options);
    }

    private static double MedianSorted(List<double> s) => s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;

    private static void InsertSorted(List<double> s, double v)
    {
        int i = s.BinarySearch(v);
        s.Insert(i < 0 ? ~i : i, v);
    }

    private static void RemoveSorted(List<double> s, double v)
    {
        int i = s.BinarySearch(v);
        if (i >= 0) s.RemoveAt(i);
    }

    /// <summary>
    /// Histograma de frametime. Largura automática (0,5/1/2/5/10 ms) para ~60 faixas até o P99,9; acima disso, faixa de transbordo.
    /// </summary>
    public static IReadOnlyList<HistogramBucket> BuildHistogram(IReadOnlyList<double> frameTimesMs, double? bucketWidthMs = null, int maxBuckets = 120)
    {
        var v = frameTimesMs.Where(x => double.IsFinite(x) && x > 0).OrderBy(x => x).ToArray();
        if (v.Length == 0) return [];
        double p999 = Stats.Percentile(v, 99.9);
        double width = bucketWidthMs is > 0 ? bucketWidthMs.Value
            : new[] { 0.5, 1, 2, 5, 10, 20 }.FirstOrDefault(c => Math.Ceiling(p999 / c) <= 60, 50);
        double lower0 = Math.Floor(v[0] / width) * width;
        int count = (int)Math.Clamp(Math.Ceiling((p999 - lower0) / width) + 1, 1, maxBuckets);
        double overflowAt = lower0 + count * width;
        var counts = new int[count];
        int overflow = 0;
        foreach (var x in v)
        {
            if (x >= overflowAt) { overflow++; continue; }
            int b = (int)((x - lower0) / width);
            counts[Math.Clamp(b, 0, count - 1)]++;
        }
        var list = new List<HistogramBucket>(count + 1);
        for (int i = 0; i < count; i++)
            list.Add(new HistogramBucket(Math.Round(lower0 + i * width, 6), Math.Round(lower0 + (i + 1) * width, 6), counts[i], counts[i] * 100.0 / v.Length));
        if (overflow > 0) list.Add(new HistogramBucket(Math.Round(overflowAt, 6), null, overflow, overflow * 100.0 / v.Length));
        return list;
    }

    /// <summary>Descrição (pt-BR) e classe de latência de um modo de apresentação do PresentMon.</summary>
    public static (PresentLatencyClass Latency, string ImpactPt) DescribePresentMode(string? mode) => mode switch
    {
        "Hardware: Independent Flip" => (PresentLatencyClass.Lowest,
            "Independent Flip: a imagem vai direto para a tela sem composição do DWM — latência mínima, equivalente à tela cheia exclusiva."),
        "Hardware Composed: Independent Flip" => (PresentLatencyClass.Lowest,
            "Independent Flip com composição por hardware (planos de sobreposição/MPO): latência mínima mesmo com elementos sobrepostos."),
        "Hardware: Legacy Flip" => (PresentLatencyClass.Lowest,
            "Tela cheia exclusiva (flip legado): latência baixa; o DWM não participa."),
        "Hardware: Legacy Copy to front buffer" => (PresentLatencyClass.Low,
            "Tela cheia exclusiva com cópia (blt) legada: latência baixa, com custo extra de cópia."),
        "Composed: Flip" => (PresentLatencyClass.Higher,
            "Composto pelo DWM (janela/sem bordas sem Independent Flip): costuma somar cerca de 1 quadro de latência. " +
            "Overlays, notificações ou janela que não cobre a tela inteira podem impedir o Independent Flip."),
        "Composed: Copy with GPU GDI" => (PresentLatencyClass.Higher,
            "Modelo de cópia (blt) composto pelo DWM: latência maior e custo extra. No Windows 11, 'Otimizações para jogos em janela' pode converter para o modelo flip."),
        "Composed: Copy with CPU GDI" => (PresentLatencyClass.Higher,
            "Cópia via CPU (GDI) composta pelo DWM: o caminho mais lento, com latência e uso de CPU maiores."),
        "Composed: Composition Atlas" => (PresentLatencyClass.Higher, "Composição via atlas do DWM: latência maior que Independent Flip."),
        _ => (PresentLatencyClass.Unknown, "Modo não identificado pelo PresentMon (ou rastreamento de display desativado)."),
    };

    public static PresentModeSummary SummarizePresentModes(IReadOnlyList<FrameRecord> frames)
    {
        var withMode = frames.Where(f => !string.IsNullOrEmpty(f.PresentMode)).ToList();
        if (withMode.Count == 0)
            return new PresentModeSummary([], null, "Modo de apresentação indisponível (CSV sem coluna PresentMode).");
        var shares = withMode.GroupBy(f => f.PresentMode!)
            .Select(g =>
            {
                var (lat, impact) = DescribePresentMode(g.Key);
                return new PresentModeShare(g.Key, g.Count(), g.Count() * 100.0 / withMode.Count, lat, impact);
            })
            .OrderByDescending(s => s.Count).ToList();
        var dom = shares[0];
        string text = $"Modo predominante: {dom.Mode} ({dom.Percent:0.#}% dos quadros). {dom.ImpactPt}";
        if (shares.Count > 1 && shares[1].Percent >= 5)
            text += $" Também houve {shares[1].Mode} em {shares[1].Percent:0.#}% — trocas de modo podem acontecer ao abrir overlays, alt-tab ou notificações.";
        return new PresentModeSummary(shares, dom.Mode, text);
    }

    public static TearingSummary SummarizeTearing(IReadOnlyList<FrameRecord> frames)
    {
        var tear = frames.Where(f => f.AllowsTearing is not null).ToList();
        double? tearPct = tear.Count > 0 ? tear.Count(f => f.AllowsTearing == true) * 100.0 / tear.Count : null;
        var sync = frames.Where(f => f.SyncInterval is not null).GroupBy(f => f.SyncInterval!.Value)
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        int syncTotal = sync.Sum(s => s.Item2);
        var syncShares = sync.Select(s => new SyncIntervalShare(s.Key, s.Item2, s.Item2 * 100.0 / syncTotal)).ToList();
        var disp = frames.Where(f => f.Displayed is not null).ToList();
        double? droppedPct = disp.Count > 0 ? disp.Count(f => f.Displayed == false) * 100.0 / disp.Count : null;

        var parts = new List<string>();
        if (syncShares.Count == 0) parts.Add("SyncInterval indisponível.");
        else
        {
            var top = syncShares[0];
            if (top.SyncInterval == 0)
            {
                if (tearPct is >= 50) parts.Add("V-Sync desligado pela aplicação (SyncInterval 0) com tearing permitido: menor latência, mas pode haver tearing (com FreeSync/G-SYNC ativo, o tearing é evitado dentro da faixa VRR).");
                else if (tearPct is not null) parts.Add("V-Sync desligado pela aplicação (SyncInterval 0), mas sem tearing permitido: o DWM sincroniza a imagem com a tela (comum em modo composto).");
                else parts.Add("V-Sync desligado pela aplicação (SyncInterval 0).");
            }
            else if (top.SyncInterval > 0)
                parts.Add($"V-Sync ligado pela aplicação (SyncInterval {top.SyncInterval}): sem tearing; o FPS fica limitado ao refresh{(top.SyncInterval > 1 ? $"/{top.SyncInterval}" : "")} e a latência aumenta quando o limite é atingido.");
            else parts.Add("SyncInterval não informado para este runtime.");
        }
        parts.Add("SyncInterval reflete o pedido da aplicação; o driver (painel AMD/NVIDIA) pode forçar ou ignorar o V-Sync.");
        if (droppedPct is > 0) parts.Add($"{droppedPct:0.#}% dos quadros não chegaram à tela (descartados).");
        return new TearingSummary(tearPct, syncShares, droppedPct, string.Join(" ", parts));
    }

    /// <summary>
    /// Heurística de gargalo usando CPUBusy/GPUBusy (PresentMon 2.x). Por quadro: GPU se GPUBusy ≥ limiar × frametime;
    /// senão CPU se CPUBusy ≥ limiar × frametime; senão "limitado" (V-Sync/limitador/espera). Sem as colunas → Indeterminado.
    /// </summary>
    public static BoundAnalysis AnalyzeBound(IReadOnlyList<FrameRecord> frames, double threshold = 0.85)
    {
        int gpu = 0, cpu = 0, lim = 0;
        foreach (var f in frames)
        {
            double ft = f.FrameTimeMs;
            if (!double.IsFinite(ft) || ft <= 0 || !double.IsFinite(f.CpuBusyMs) || !double.IsFinite(f.GpuBusyMs)) continue;
            if (f.CpuBusyMs <= 0 && f.GpuBusyMs <= 0) continue; // colunas preenchidas com zero = sem dado
            if (f.GpuBusyMs >= threshold * ft) gpu++;
            else if (f.CpuBusyMs >= threshold * ft) cpu++;
            else lim++;
        }
        int n = gpu + cpu + lim;
        if (n < 30)
            return new BoundAnalysis(BoundKind.Indeterminate, Confidence.Low, "Indeterminado", null, null, null, n,
                "Indeterminado: o CSV não tem CPUBusy/GPUBusy (PresentMon 1.x ou --v1_metrics) ou há poucos quadros. Use o PresentMon 2.x para esta heurística.");
        double g = gpu * 100.0 / n, c = cpu * 100.0 / n, l = lim * 100.0 / n;
        double top = Math.Max(g, Math.Max(c, l));
        BoundKind kind = top < 60 ? BoundKind.Mixed : top == g ? BoundKind.GpuBound : top == c ? BoundKind.CpuBound : BoundKind.Limited;
        Confidence conf = top >= 80 && n >= 300 ? Confidence.High : top >= 60 ? Confidence.Medium : Confidence.Low;
        string label = kind switch
        {
            BoundKind.GpuBound => "Provavelmente limitado pela GPU",
            BoundKind.CpuBound => "Provavelmente limitado pela CPU (thread de renderização/jogo)",
            BoundKind.Limited => "Provavelmente limitado por V-Sync/limitador de FPS (nem CPU nem GPU saturadas)",
            _ => "Misto/variável entre CPU e GPU",
        };
        string expl = $"Heurística (não é medição direta): em {g:0.#}% dos quadros a GPU ficou ocupada ≥ {threshold:P0} do frametime; " +
                      $"em {c:0.#}% a CPU (CPUBusy) dominou; em {l:0.#}% nenhuma das duas estava saturada (indica limitador, V-Sync ou espera). " +
                      $"Avaliados {n} quadros.";
        return new BoundAnalysis(kind, conf, label, g, c, l, n, expl);
    }

    /// <summary>FPS por segundo de jogo (quadros ÷ soma dos frametimes em cada bloco de ~1 s). Último bloco &lt; 0,5 s é descartado.</summary>
    public static IReadOnlyList<double> PerSecondFps(IReadOnlyList<FrameRecord> frames)
    {
        var list = new List<double>();
        double acc = 0; int count = 0;
        foreach (var f in frames)
        {
            if (!double.IsFinite(f.FrameTimeMs) || f.FrameTimeMs <= 0) continue;
            acc += f.FrameTimeMs; count++;
            if (acc >= 1000) { list.Add(count * 1000.0 / acc); acc = 0; count = 0; }
        }
        if (acc >= 500 && count > 0) list.Add(count * 1000.0 / acc);
        return list;
    }
}
