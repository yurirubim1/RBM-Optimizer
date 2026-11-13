using Rbm.Gaming.Frames;
using Xunit;

namespace Rbm.Tests.Gaming;

public class PresentMonCsvParserTests
{
    private static ParsedFrameCapture Parse(string fixture, string? process = "msedge.exe") =>
        PresentMonCsvParser.ParseFile(GamingTestUtil.Fixture(fixture), new PresentMonParseOptions(process));

    [Theory]
    [InlineData("pm26_default.csv", PresentMonCsvLayout.V2Default)]
    [InlineData("pm26_v2metrics.csv", PresentMonCsvLayout.V2Metrics)]
    [InlineData("pm26_v1metrics.csv", PresentMonCsvLayout.V1)]
    [InlineData("pm1x_layout.csv", PresentMonCsvLayout.V1)]
    public void Detects_layout_from_header(string fixture, PresentMonCsvLayout expected)
    {
        var header = PresentMonCsvParser.SplitCsv(File.ReadLines(GamingTestUtil.Fixture(fixture)).First().TrimStart('﻿'));
        Assert.Equal(expected, PresentMonCsvParser.DetectLayout(header));
    }

    // Valores de referência calculados de forma independente (awk) sobre os mesmos arquivos.
    [Theory]
    [InlineData("pm26_default.csv", 46, 60.149259, "MsBetweenPresents")]
    [InlineData("pm26_v2metrics.csv", 22, 60.278200, "FrameTime")]
    [InlineData("pm26_v1metrics.csv", 45, 60.007489, "msBetweenPresents")]
    [InlineData("pm1x_layout.csv", 45, 60.007489, "msBetweenPresents")]
    public void Parses_target_process_and_computes_average_fps(string fixture, int frames, double avgFps, string ftColumn)
    {
        var cap = Parse(fixture);
        Assert.Equal("msedge.exe", cap.Application);
        Assert.Equal(19052, cap.ProcessId);
        Assert.Equal(frames, cap.Frames.Count);
        Assert.Equal(ftColumn, cap.FrameTimeColumn);
        var m = FrameAnalyzer.ComputeMetrics(cap.Frames.Select(f => f.FrameTimeMs).ToList());
        Assert.Equal(avgFps, m.AverageFps, 4);
        Assert.NotEmpty(cap.OtherTargets);
    }

    [Fact]
    public void Default_layout_has_cpu_gpu_busy_display_and_time_in_ms()
    {
        var cap = Parse("pm26_default.csv");
        Assert.True(cap.HasCpuGpuBusy);
        Assert.True(cap.HasDisplayTracking);
        Assert.Equal("coluna TimeInMs", cap.TimeSource);
        Assert.Equal("DXGI", cap.Runtime);
        Assert.Equal(27.3081, cap.Frames[0].TimeMs, 4); // primeira linha do msedge: TimeInMs = 27.3081
        Assert.All(cap.Frames, f => Assert.Equal("Composed: Flip", f.PresentMode));
        Assert.All(cap.Frames, f => Assert.Equal(1, f.SyncInterval));
        Assert.True(cap.Frames.Zip(cap.Frames.Skip(1)).All(p => p.Second.TimeMs > p.First.TimeMs));
    }

    [Fact]
    public void V2_metrics_cpu_start_time_unit_is_autodetected_as_ms()
    {
        var cap = Parse("pm26_v2metrics.csv");
        Assert.Equal("coluna CPUStartTime", cap.TimeSource);
        Assert.True(cap.HasCpuGpuBusy);
        // CPUStartTime do 1º quadro do msedge = 15.4391 (ms); FrameTime ≈ diferença entre inícios consecutivos.
        Assert.Equal(15.4391, cap.Frames[0].TimeMs, 4);
        Assert.Equal(cap.Frames[1].TimeMs - cap.Frames[0].TimeMs, cap.Frames[0].FrameTimeMs, 1);
    }

    [Fact]
    public void V1_time_in_seconds_is_converted_and_dropped_flag_is_read()
    {
        var cap = Parse("pm26_v1metrics.csv");
        Assert.Equal("coluna TimeInSeconds", cap.TimeSource);
        Assert.True(cap.Frames[0].TimeMs > 1 && cap.Frames[^1].TimeMs < 4000);
        Assert.All(cap.Frames, f => Assert.False(f.Displayed)); // essas linhas reais vêm com Dropped=1
        Assert.All(cap.Frames, f => Assert.True(double.IsNaN(f.DisplayChangeMs)));
        Assert.False(cap.HasCpuGpuBusy);
        var same = Parse("pm1x_layout.csv");
        Assert.Equal(cap.Frames.Select(f => f.FrameTimeMs), same.Frames.Select(f => f.FrameTimeMs));
    }

    [Fact]
    public void Without_process_filter_picks_busiest_known_process_and_main_swapchain()
    {
        var cap = PresentMonCsvParser.ParseFile(GamingTestUtil.Fixture("pm26_default.csv"));
        Assert.Equal("Code.exe", cap.Application);
        Assert.Equal(155, cap.Frames.Count);
        Assert.Equal("0x23CEEB16B10", cap.SwapChain);

        var roblox = PresentMonCsvParser.ParseFile(GamingTestUtil.Fixture("pm26_default.csv"), new("RobloxStudioBeta")); // sem .exe também funciona
        Assert.Equal("RobloxStudioBeta.exe", roblox.Application);
        Assert.Contains(roblox.Warnings, w => w.Contains("swap chains"));
    }

    [Fact]
    public void Unknown_process_or_header_is_rejected()
    {
        Assert.Throws<FormatException>(() => Parse("pm26_default.csv", "naoexiste.exe"));
        Assert.Throws<FormatException>(() => PresentMonCsvParser.ParseText("a,b,c\n1,2,3\n"));
        Assert.Throws<FormatException>(() => PresentMonCsvParser.ParseText(""));
    }

    [Fact]
    public void Handles_na_values_quotes_and_missing_time_column()
    {
        const string csv = "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,msBetweenPresents,msBetweenDisplayChange\n" +
                           "\"My, Game.exe\",10,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,10.0,10.0\n" +
                           "\"My, Game.exe\",10,0x1,DXGI,0,0,1,Hardware: Independent Flip,1,NA,0\n" +
                           "\"My, Game.exe\",10,0x1,DXGI,0,0,1,Hardware: Independent Flip,0,20.0,30.0\n";
        var cap = PresentMonCsvParser.ParseText(csv);
        Assert.Equal("My, Game.exe", cap.Application);
        Assert.Equal(3, cap.Frames.Count);
        Assert.True(double.IsNaN(cap.Frames[1].FrameTimeMs));
        Assert.False(cap.Frames[1].Displayed);
        Assert.Equal("soma acumulada dos frametimes", cap.TimeSource);
        Assert.Equal([10.0, 10.0, 30.0], cap.Frames.Select(f => f.TimeMs));
        Assert.True(cap.Frames[0].AllowsTearing);
    }
}
