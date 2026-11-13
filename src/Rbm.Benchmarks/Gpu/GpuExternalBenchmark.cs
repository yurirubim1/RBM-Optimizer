namespace Rbm.Benchmarks.Gpu;

/// <summary>
/// Descritor honesto para GPU: NÃO há benchmark sintético de GPU neste módulo. Não existe forma confiável e
/// multi-fabricante de medir a GPU sem uma pilha de renderização própria (Direct3D/Vulkan, shaders, cenas fixas),
/// e um número inventado seria enganoso. A abordagem recomendada é capturar FPS/tempo de quadro em jogos reais
/// (PresentMon, módulo Gaming). ID: gpu.external.
/// </summary>
public sealed class GpuExternalBenchmark : IBenchmark
{
    public string Id => "gpu.external";
    public string Name => "GPU — benchmark sintético (não implementado)";
    public BenchmarkCategory Category => BenchmarkCategory.Gpu;
    public string Unit => "ms/quadro";
    public bool HigherIsBetter => false;
    public bool IsImplemented => false;
    public bool WritesToStorage => false;
    public IReadOnlyList<MetricDefinition> SecondaryMetrics => Array.Empty<MetricDefinition>();

    public const string Explanation =
        "Não há benchmark sintético de GPU no RBM Optimizer: sem uma pilha de renderização própria não é possível medir a GPU de forma confiável e " +
        "comparável entre fabricantes, e não apresentamos números inventados. Para avaliar a GPU, use a captura de desempenho em jogos " +
        "do módulo Gaming (PresentMon): tempo de quadro médio, P99/1% low e 'stutter', sempre na mesma cena, resolução e configurações, " +
        "comparando antes/depois na mesma máquina.";

    public string Methodology =>
        Explanation + " Procedimento recomendado: (1) escolha uma cena repetível (benchmark embutido do jogo ou trecho fixo de 60–120 s); " +
        "(2) fixe resolução, qualidade, V-Sync/limitador de FPS desligados; (3) aqueça a GPU por 1–2 min; (4) capture ao menos 3 execuções com PresentMon; " +
        "(5) compare tempo de quadro (ms) e percentis com o teste t de Welch (mesmo critério dos demais benchmarks).";

    public Task<BenchmarkResult> RunAsync(BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(new BenchmarkResult
        {
            BenchmarkId = Id,
            Name = Name,
            Category = Category,
            Status = BenchmarkStatus.NotImplemented,
            Unit = Unit,
            HigherIsBetter = HigherIsBetter,
            StartedAt = now,
            Duration = TimeSpan.Zero,
            Message = Explanation,
            Methodology = Methodology,
        });
    }
}
