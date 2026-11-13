using System.Runtime.CompilerServices;
using Rbm.Gaming.Frames;

namespace Rbm.Tests.Gaming;

/// <summary>
/// Utilitários dos testes do Rbm.Gaming.
/// Fixtures (Gaming/Fixtures): trechos REAIS de capturas do PresentMon 2.6.0 feitas neste PC (Win11 26200, RX 6600):
///  - pm26_default.csv   → layout padrão 2.3+ (TimeInMs, MsBetweenPresents, MsCPUBusy, MsGPUBusy...)
///  - pm26_v2metrics.csv → --v2_metrics (CPUStartTime, FrameTime, CPUBusy, GPUBusy...)
///  - pm26_v1metrics.csv → --v1_metrics (TimeInSeconds, msBetweenPresents, Dropped...)
///  - pm1x_layout.csv    → mesmas linhas do v1, com as colunas na ordem documentada do PresentMon 1.9.2.
/// Os arquivos são localizados pelo caminho do fonte (CallerFilePath) ou, se copiados, pela pasta de saída.
/// </summary>
internal static class GamingTestUtil
{
    public static string FixturesDir([CallerFilePath] string sourceFile = "")
    {
        var nearSource = Path.Combine(Path.GetDirectoryName(sourceFile) ?? "", "Fixtures");
        if (Directory.Exists(nearSource)) return nearSource;
        return Path.Combine(AppContext.BaseDirectory, "Gaming", "Fixtures");
    }

    public static string Fixture(string name) => Path.Combine(FixturesDir(), name);

    public static FrameRecord Frame(double ft, double time = double.NaN, double cpuBusy = double.NaN, double gpuBusy = double.NaN,
        bool? displayed = true, string? mode = null, bool? tearing = null, int? sync = null) =>
        new(time, ft, double.NaN, double.NaN, cpuBusy, double.NaN, gpuBusy, double.NaN, double.NaN, double.NaN, displayed, mode, tearing, sync);

    /// <summary>Quadros com tempo acumulado (TimeMs = soma dos frametimes).</summary>
    public static List<FrameRecord> Timeline(IEnumerable<double> frameTimes)
    {
        var list = new List<FrameRecord>();
        double t = 0;
        foreach (var ft in frameTimes) { t += ft; list.Add(Frame(ft, t)); }
        return list;
    }

    public static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "rbm-gaming-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static void WriteBytes(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }
}
