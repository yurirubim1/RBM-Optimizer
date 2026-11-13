using Rbm.Core;
using Rbm.Gaming.Frames;
using Xunit;
using static Rbm.Tests.Gaming.GamingTestUtil;

namespace Rbm.Tests.Gaming;

public class FrameAnalyzerTests
{
    [Fact]
    public void Average_fps_is_frames_over_total_time_not_mean_of_instantaneous_fps()
    {
        var m = FrameAnalyzer.ComputeMetrics([10.0, 30.0]);
        Assert.Equal(50.0, m.AverageFps, 9);             // 2 quadros / 40 ms
        Assert.NotEqual((100.0 + 1000.0 / 30) / 2, m.AverageFps, 3); // média dos instantâneos seria 66,7
        Assert.Equal(0.04, m.DurationSeconds, 9);
    }

    [Fact]
    public void Constant_frametime_gives_identical_metrics()
    {
        var m = FrameAnalyzer.ComputeMetrics(Enumerable.Repeat(10.0, 100).ToList());
        Assert.Equal(100, m.AverageFps, 9);
        Assert.Equal(100, m.Low1PercentFps, 9);
        Assert.Equal(100, m.Low1PercentAvgFps, 9);
        Assert.Equal(0, m.StdDevFrameTimeMs, 9);
        Assert.False(m.Low01PercentReliable);
    }

    [Fact]
    public void One_percent_and_point_one_percent_lows_follow_documented_definitions()
    {
        var ft = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList();
        var m = FrameAnalyzer.ComputeMetrics(ft);
        Assert.Equal(1000, m.FrameCount);
        // P99 (R-7): posição 0,99×999 = 989,01 → 10 + (50−10)×0,01 = 10,4 ms
        Assert.Equal(10.4, m.P99FrameTimeMs, 9);
        Assert.Equal(1000 / 10.4, m.Low1PercentFps, 9);
        Assert.Equal(50, m.P999FrameTimeMs, 9);
        Assert.Equal(20, m.Low01PercentFps, 9);
        Assert.Equal(20, m.Low1PercentAvgFps, 9);   // média dos 10 piores = 50 ms
        Assert.Equal(20, m.Low01PercentAvgFps, 9);  // pior 1 quadro
        Assert.Equal(10, m.MedianFrameTimeMs, 9);
        Assert.Equal(1000.0 * 1000 / (990 * 10 + 10 * 50), m.AverageFps, 9);
        Assert.Equal(10, m.P95FrameTimeMs, 9);
        Assert.True(m.Low01PercentReliable);
        double mean = (990 * 10 + 500) / 1000.0;
        double sd = Math.Sqrt((990 * Math.Pow(10 - mean, 2) + 10 * Math.Pow(50 - mean, 2)) / 999);
        Assert.Equal(sd, m.StdDevFrameTimeMs, 9);
    }

    [Fact]
    public void Empty_or_invalid_input_returns_nan_not_fabricated_values()
    {
        var m = FrameAnalyzer.ComputeMetrics([double.NaN, 0, -1]);
        Assert.Equal(0, m.FrameCount);
        Assert.True(double.IsNaN(m.AverageFps));
    }

    [Fact]
    public void Stutter_requires_both_ratio_and_absolute_delta()
    {
        // base 10 ms; 25 ms → 2,5× e +15 ms → stutter
        var a = Timeline(Enumerable.Repeat(10.0, 50).Append(25).Concat(Enumerable.Repeat(10.0, 50)));
        var s = FrameAnalyzer.DetectStutters(a);
        var e = Assert.Single(s.Events);
        Assert.Equal(50, e.FrameIndex);
        Assert.Equal(10, e.BaselineMs, 9);

        // base 5 ms; 12 ms → 2,4× mas só +7 ms → NÃO é stutter
        Assert.Equal(0, FrameAnalyzer.DetectStutters(Timeline(Enumerable.Repeat(5.0, 50).Append(12).Concat(Enumerable.Repeat(5.0, 10)))).Count);
        // base 10 ms; 19 ms → +9 ms mas só 1,9× → NÃO é stutter
        Assert.Equal(0, FrameAnalyzer.DetectStutters(Timeline(Enumerable.Repeat(10.0, 50).Append(19).Concat(Enumerable.Repeat(10.0, 10)))).Count);
        // limiares configuráveis
        Assert.Equal(1, FrameAnalyzer.DetectStutters(Timeline(Enumerable.Repeat(5.0, 50).Append(12).Concat(Enumerable.Repeat(5.0, 10))), new StutterOptions(MinDeltaMs: 5)).Count);
    }

    [Fact]
    public void Rolling_median_adapts_to_new_baseline_and_ignores_early_frames()
    {
        // primeiros quadros (sem histórico) não são avaliados; após troca de 10 → 30 ms, não deve acusar stutters contínuos
        var ft = new List<double> { 100 }.Concat(Enumerable.Repeat(10.0, 40)).Concat(Enumerable.Repeat(30.0, 60)).ToList();
        var s = FrameAnalyzer.DetectStutters(Timeline(ft));
        Assert.True(s.Count <= 15, $"stutters={s.Count}");
        Assert.DoesNotContain(s.Events, e => e.FrameIndex == 0);
        Assert.DoesNotContain(s.Events, e => e.FrameIndex > 41 + 15);
    }

    [Fact]
    public void Stutters_are_grouped_into_clusters_and_rate_per_minute()
    {
        var ft = Enumerable.Repeat(10.0, 100).ToList();
        ft[50] = 40; ft[60] = 40;              // ~100 ms de distância → mesmo agrupamento
        ft.AddRange(Enumerable.Repeat(10.0, 600));
        ft[600] = 50;                          // ~5 s depois → novo agrupamento
        var s = FrameAnalyzer.DetectStutters(Timeline(ft));
        Assert.Equal(3, s.Count);
        Assert.Equal(2, s.Clusters.Count);
        Assert.Equal(2, s.Clusters[0].Count);
        Assert.Equal(50, s.Clusters[1].WorstFrameTimeMs);
        double minutes = ft.Sum() / 60000.0;
        Assert.Equal(3 / minutes, s.PerMinute, 6);
    }

    [Fact]
    public void Histogram_counts_all_frames()
    {
        var ft = Enumerable.Range(0, 1000).Select(i => 5 + (i % 10) * 0.9).Append(200).ToList();
        var h = FrameAnalyzer.BuildHistogram(ft, bucketWidthMs: 1);
        Assert.Equal(ft.Count, h.Sum(b => b.Count));
        Assert.Equal(100, h.Sum(b => b.Percent), 6);
        Assert.Null(h[^1].UpperMs); // 200 ms cai na faixa de transbordo
        Assert.All(h.Take(h.Count - 1), b => Assert.Equal(1, b.UpperMs!.Value - b.LowerMs, 9));
        Assert.Empty(FrameAnalyzer.BuildHistogram([]));
    }

    [Fact]
    public void Bound_heuristic_classifies_and_is_indeterminate_without_columns()
    {
        var gpu = Enumerable.Range(0, 400).Select(_ => Frame(10, cpuBusy: 4, gpuBusy: 9.8)).ToList();
        var r = FrameAnalyzer.AnalyzeBound(gpu);
        Assert.Equal(BoundKind.GpuBound, r.Kind);
        Assert.Equal(Confidence.High, r.Confidence);

        var cpu = Enumerable.Range(0, 400).Select(_ => Frame(10, cpuBusy: 9.5, gpuBusy: 3)).ToList();
        Assert.Equal(BoundKind.CpuBound, FrameAnalyzer.AnalyzeBound(cpu).Kind);

        var capped = Enumerable.Range(0, 400).Select(_ => Frame(16.7, cpuBusy: 4, gpuBusy: 5)).ToList();
        Assert.Equal(BoundKind.Limited, FrameAnalyzer.AnalyzeBound(capped).Kind);

        // Com V-Sync ligado, "CPU" é rebaixado (CPUBusy inclui espera de vsync/limitador em muitos apps)
        var cpuVsync = Enumerable.Range(0, 400).Select(_ => Frame(16.7, cpuBusy: 16, gpuBusy: 3, sync: 1)).ToList();
        var adj = FrameAnalyzer.AdjustBoundForSync(FrameAnalyzer.AnalyzeBound(cpuVsync), FrameAnalyzer.SummarizeTearing(cpuVsync));
        Assert.Equal(Confidence.Low, adj.Confidence);
        Assert.Contains("V-Sync", adj.LabelPt);
        Assert.Same(r, FrameAnalyzer.AdjustBoundForSync(r, FrameAnalyzer.SummarizeTearing(gpu))); // GPU não muda

        var mixed = gpu.Take(200).Concat(cpu.Take(200)).ToList();
        Assert.Equal(BoundKind.Mixed, FrameAnalyzer.AnalyzeBound(mixed).Kind);

        var none = Enumerable.Range(0, 400).Select(_ => Frame(10)).ToList();
        var ind = FrameAnalyzer.AnalyzeBound(none);
        Assert.Equal(BoundKind.Indeterminate, ind.Kind);
        Assert.Equal("Indeterminado", ind.LabelPt);
        // colunas preenchidas com zero = sem dado
        Assert.Equal(BoundKind.Indeterminate, FrameAnalyzer.AnalyzeBound(Enumerable.Range(0, 400).Select(_ => Frame(10, cpuBusy: 0, gpuBusy: 0)).ToList()).Kind);
    }

    [Fact]
    public void Per_second_fps_uses_frame_time_blocks()
    {
        var fps = FrameAnalyzer.PerSecondFps(Timeline(Enumerable.Repeat(10.0, 300)));
        Assert.Equal(3, fps.Count);
        Assert.All(fps, v => Assert.Equal(100, v, 6));
        Assert.Equal(2, FrameAnalyzer.PerSecondFps(Timeline(Enumerable.Repeat(10.0, 240))).Count); // 0,4 s finais descartados
    }

    [Fact]
    public void Present_mode_and_tearing_summaries()
    {
        var frames = Enumerable.Range(0, 90).Select(_ => Frame(5, mode: "Hardware: Independent Flip", tearing: true, sync: 0))
            .Concat(Enumerable.Range(0, 10).Select(_ => Frame(5, mode: "Composed: Flip", tearing: false, sync: 0))).ToList();
        var pm = FrameAnalyzer.SummarizePresentModes(frames);
        Assert.Equal("Hardware: Independent Flip", pm.Dominant);
        Assert.Equal(PresentLatencyClass.Lowest, pm.Modes[0].Latency);
        Assert.Equal(10, pm.Modes[1].Percent, 9);
        Assert.Contains("Composed: Flip", pm.ExplanationPt);

        var t = FrameAnalyzer.SummarizeTearing(frames);
        Assert.Equal(90, t.AllowsTearingPercent!.Value, 9);
        Assert.Equal(0, t.SyncIntervals[0].SyncInterval);
        Assert.Contains("V-Sync desligado", t.InterpretationPt);

        var vs = FrameAnalyzer.SummarizeTearing([Frame(16.7, tearing: false, sync: 1)]);
        Assert.Contains("V-Sync ligado", vs.InterpretationPt);
        Assert.Null(FrameAnalyzer.SummarizePresentModes([Frame(5)]).Dominant);
    }

    [Fact]
    public void Full_analysis_on_real_capture()
    {
        var cap = PresentMonCsvParser.ParseFile(Fixture("pm26_default.csv"), new("Code.exe"));
        var a = FrameAnalyzer.Analyze(cap);
        Assert.Equal(PresentMonCsvLayout.V2Default, a.Layout);
        Assert.Equal("Composed: Flip", a.PresentModes.Dominant);
        Assert.Equal(PresentLatencyClass.Higher, a.PresentModes.Modes[0].Latency);
        Assert.NotEqual(BoundKind.Indeterminate, a.Bound.Kind); // há CPUBusy/GPUBusy no layout 2.x
        Assert.Contains(a.Warnings, w => w.Contains("0,1% low"));
        Assert.Equal(cap.Frames.Count(f => double.IsFinite(f.FrameTimeMs) && f.FrameTimeMs > 0), a.Histogram.Sum(b => b.Count));

        var v1 = FrameAnalyzer.Analyze(PresentMonCsvParser.ParseFile(Fixture("pm1x_layout.csv"), new("Code.exe")));
        Assert.Equal(BoundKind.Indeterminate, v1.Bound.Kind);
    }
}
