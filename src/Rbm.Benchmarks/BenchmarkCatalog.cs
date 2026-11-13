using System.Text.Json;
using System.Text.Json.Serialization;
using Rbm.Benchmarks.Cpu;
using Rbm.Benchmarks.Gpu;
using Rbm.Benchmarks.Memory;
using Rbm.Benchmarks.Responsiveness;
using Rbm.Benchmarks.Storage;

namespace Rbm.Benchmarks;

/// <summary>Catálogo de todos os benchmarks disponíveis.</summary>
public static class BenchmarkCatalog
{
    /// <summary>Cria instâncias novas de todos os benchmarks (ordem recomendada de execução).</summary>
    public static IReadOnlyList<IBenchmark> CreateAll()
    {
        var list = new List<IBenchmark>();
        list.AddRange(CpuBenchmark.CreateAll());
        list.Add(new MemoryBandwidthBenchmark(MemoryBandwidthKind.Read));
        list.Add(new MemoryBandwidthBenchmark(MemoryBandwidthKind.Write));
        list.Add(new MemoryBandwidthBenchmark(MemoryBandwidthKind.Copy));
        list.Add(new MemoryLatencyBenchmark(MemoryLatencyKind.Cache));
        list.Add(new MemoryLatencyBenchmark(MemoryLatencyKind.Dram));
        list.Add(new StorageBenchmark());
        list.Add(new SleepPrecisionBenchmark());
        list.Add(new WakeLatencyBenchmark());
        list.Add(new JitterBenchmark());
        list.Add(new GpuExternalBenchmark());
        return list;
    }

    public static IBenchmark? Get(string id) => CreateAll().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    public static IReadOnlyList<string> Ids => CreateAll().Select(b => b.Id).ToArray();
}

/// <summary>Execução sequencial de vários benchmarks (um por vez, para não interferirem entre si).</summary>
public static class BenchmarkSuite
{
    /// <summary>
    /// Executa os benchmarks em sequência. Em cancelamento, interrompe e devolve os resultados já obtidos
    /// mais o resultado "Cancelled" do benchmark em andamento.
    /// </summary>
    public static async Task<IReadOnlyList<BenchmarkResult>> RunAsync(IEnumerable<IBenchmark> benchmarks, BenchmarkOptions options,
        IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>();
        foreach (var b in benchmarks)
        {
            if (ct.IsCancellationRequested) break;
            var r = await b.RunAsync(options, progress, ct).ConfigureAwait(false);
            results.Add(r);
            if (r.Status == BenchmarkStatus.Cancelled) break;
        }
        return results;
    }
}

/// <summary>
/// Serialização JSON dos resultados (o app persiste no SQLite como JSON). Use SEMPRE estas opções:
/// elas permitem NaN/Infinity (estatísticas vazias) e gravam enums como texto.
/// </summary>
public static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize(BenchmarkResult result) => JsonSerializer.Serialize(result, Options);
    public static BenchmarkResult? Deserialize(string json) => JsonSerializer.Deserialize<BenchmarkResult>(json, Options);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
