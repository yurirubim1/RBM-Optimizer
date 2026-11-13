using Rbm.Core;

namespace Rbm.Benchmarks;

/// <summary>Categoria do benchmark.</summary>
public enum BenchmarkCategory { Cpu, Memory, Storage, Responsiveness, Gpu }

/// <summary>Estado final de uma execução. Somente <see cref="Completed"/> contém números válidos.</summary>
public enum BenchmarkStatus
{
    Completed = 0,
    /// <summary>Interrompido pelo usuário. Nenhum número parcial é apresentado.</summary>
    Cancelled = 1,
    /// <summary>Falhou (pré-condição não atendida, erro de E/S, checksum divergente...).</summary>
    Failed = 2,
    /// <summary>Descritor sem implementação (ex.: GPU sintético).</summary>
    NotImplemented = 3,
}

/// <summary>Unidades usadas pelos benchmarks (sempre físicas/contáveis).</summary>
public static class BenchmarkUnits
{
    /// <summary>Milhões de operações por segundo (a operação é definida na metodologia de cada teste).</summary>
    public const string MegaOpsPerSecond = "Mops/s";
    /// <summary>Megabytes decimais (10^6 bytes) por segundo.</summary>
    public const string MegabytesPerSecond = "MB/s";
    /// <summary>Gigabytes decimais (10^9 bytes) por segundo.</summary>
    public const string GigabytesPerSecond = "GB/s";
    /// <summary>Milhões de elementos ordenados por segundo.</summary>
    public const string MegaElementsPerSecond = "M elementos/s";
    public const string Iops = "IOPS";
    public const string Nanoseconds = "ns";
    public const string Microseconds = "µs";
    public const string EventsPerSecond = "eventos/s";
    public const string Percent = "%";
    /// <summary>Razão adimensional (ex.: escalonamento MT/ST).</summary>
    public const string Ratio = "x";
}

/// <summary>Contrato de um benchmark. Implementações executam fora da thread de UI.</summary>
public interface IBenchmark
{
    /// <summary>Identificador estável (persistido; usado para comparação).</summary>
    string Id { get; }
    /// <summary>Nome em pt-BR.</summary>
    string Name { get; }
    BenchmarkCategory Category { get; }
    /// <summary>Unidade da métrica principal.</summary>
    string Unit { get; }
    bool HigherIsBetter { get; }
    /// <summary>Metodologia reproduzível (pt-BR).</summary>
    string Methodology { get; }
    /// <summary>False para descritores que apenas explicam a abordagem recomendada (ex.: GPU).</summary>
    bool IsImplemented { get; }
    /// <summary>True se o teste grava dados em disco além do arquivo temporário mínimo (exige opt-in).</summary>
    bool WritesToStorage { get; }
    /// <summary>Métricas secundárias que o resultado pode conter.</summary>
    IReadOnlyList<MetricDefinition> SecondaryMetrics { get; }

    /// <summary>
    /// Executa aquecimento + N iterações medidas. Nunca lança exceção para erros de medição:
    /// retorna <see cref="BenchmarkResult"/> com <see cref="BenchmarkStatus"/> adequado.
    /// </summary>
    Task<BenchmarkResult> RunAsync(BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>Definição de uma métrica (principal ou secundária).</summary>
public sealed record MetricDefinition(string Key, string Name, string Unit, bool HigherIsBetter);

/// <summary>Opções de execução. Todos os tamanhos são fixos e registrados no resultado para reprodutibilidade.</summary>
public sealed record BenchmarkOptions
{
    public const long MiB = 1024L * 1024;
    public const long GiB = 1024L * MiB;

    /// <summary>Iterações de aquecimento descartadas (JIT tiering, caches, estados de energia).</summary>
    public int WarmupIterations { get; init; } = 1;
    /// <summary>Iterações medidas (amostras). Padrão 5.</summary>
    public int MeasuredIterations { get; init; } = 5;
    /// <summary>Multiplicador da quantidade de trabalho por iteração (não altera tamanhos de dados). 1.0 = padrão.</summary>
    public double WorkScale { get; init; } = 1.0;
    /// <summary>Threads para testes multi-thread / largura de banda. Null = Environment.ProcessorCount.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>Captura condições de teste (plano de energia, carga de fundo etc.) antes de iniciar.</summary>
    public bool CaptureConditions { get; init; } = true;
    /// <summary>Janela de amostragem da carga de CPU de fundo antes do início.</summary>
    public TimeSpan ConditionsSampleWindow { get; init; } = TimeSpan.FromSeconds(3);

    // Memória
    public long MemoryBandwidthBufferBytes { get; init; } = 512 * MiB;
    public long MemoryLatencyBufferBytes { get; init; } = 256 * MiB;
    /// <summary>Buffer pequeno (cabe no L2) para o ponto de latência de cache.</summary>
    public long CacheLatencyBufferBytes { get; init; } = 256 * 1024;

    // Armazenamento
    /// <summary>Volume alvo (ex.: "D:\"). Null = unidade do sistema.</summary>
    public string? StorageVolume { get; init; }
    /// <summary>Pasta explícita para o arquivo temporário (sobrepõe a pasta padrão do volume). Uso avançado/testes.</summary>
    public string? StorageDirectory { get; init; }
    public long StorageFileBytes { get; init; } = 1 * GiB;
    /// <summary>Opt-in: testes de gravação (desgaste de SSD). Padrão: desligado.</summary>
    public bool StorageIncludeWriteTests { get; init; }
    /// <summary>Duração máxima de cada subteste sequencial por iteração.</summary>
    public TimeSpan StorageSequentialDuration { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Duração de cada subteste aleatório 4 KiB por iteração.</summary>
    public TimeSpan StorageRandomDuration { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Margem livre exigida além de 2× o tamanho do arquivo.</summary>
    public long StorageFreeSpaceMarginBytes { get; init; } = 1 * GiB;

    // Responsividade
    public int SleepSamplesPerIteration { get; init; } = 100;
    public int WakeSamplesPerIteration { get; init; } = 500;
    public TimeSpan JitterDurationPerIteration { get; init; } = TimeSpan.FromSeconds(1);
    public double JitterThresholdMicroseconds { get; init; } = 50;

    internal int EffectiveThreads => Math.Max(1, ThreadCount ?? Environment.ProcessorCount);
    internal int Scale(int baseUnits) => Math.Max(1, (int)Math.Round(baseUnits * WorkScale));

    /// <summary>Valida as opções; retorna mensagem pt-BR de erro ou null.</summary>
    public string? Validate()
    {
        if (WarmupIterations < 0) return "Iterações de aquecimento não podem ser negativas.";
        if (MeasuredIterations < 1) return "É necessária ao menos 1 iteração medida.";
        if (!(WorkScale > 0) || double.IsInfinity(WorkScale)) return "Multiplicador de trabalho inválido.";
        if (ThreadCount is < 1) return "Número de threads inválido.";
        if (MemoryBandwidthBufferBytes < MiB) return "Buffer de largura de banda deve ter ao menos 1 MiB.";
        if (MemoryLatencyBufferBytes < 4096 || CacheLatencyBufferBytes < 4096) return "Buffers de latência devem ter ao menos 4 KiB.";
        if (StorageFileBytes < 16 * MiB) return "Arquivo de teste de armazenamento deve ter ao menos 16 MiB.";
        if (StorageSequentialDuration <= TimeSpan.Zero || StorageRandomDuration <= TimeSpan.Zero) return "Durações dos testes de armazenamento inválidas.";
        if (StorageFreeSpaceMarginBytes < 0) return "Margem de espaço livre inválida.";
        if (SleepSamplesPerIteration < 1 || WakeSamplesPerIteration < 1) return "Número de amostras por iteração inválido.";
        if (JitterDurationPerIteration <= TimeSpan.Zero || JitterThresholdMicroseconds <= 0) return "Parâmetros de jitter inválidos.";
        return null;
    }
}

/// <summary>Fase da execução.</summary>
public enum BenchmarkPhase { Conditions, Preparing, Warmup, Measuring, Finishing }

/// <summary>
/// Progresso reportado durante a execução. <c>Iteration</c> é 1-based dentro da fase (0 quando não se aplica);
/// <c>Fraction</c> é a fração global estimada 0..1; <c>Message</c> em pt-BR.
/// </summary>
public sealed record BenchmarkProgress(
    string BenchmarkId,
    BenchmarkPhase Phase,
    int Iteration,
    int TotalIterations,
    double Fraction,
    string Message);

/// <summary>Amostras e resumo de uma métrica secundária (uma amostra por iteração medida).</summary>
public sealed record BenchmarkMetric
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool HigherIsBetter { get; init; }
    public IReadOnlyList<double> Samples { get; init; } = Array.Empty<double>();
    public StatSummary Summary { get; init; } = StatSummary.Empty;
}

/// <summary>Resultado imutável de uma execução de benchmark.</summary>
public sealed record BenchmarkResult
{
    public string BenchmarkId { get; init; } = "";
    public string Name { get; init; } = "";
    public BenchmarkCategory Category { get; init; }
    public BenchmarkStatus Status { get; init; }
    /// <summary>Unidade da métrica principal.</summary>
    public string Unit { get; init; } = "";
    public bool HigherIsBetter { get; init; }
    /// <summary>Amostras da métrica principal, uma por iteração medida. Vazio se não concluído.</summary>
    public IReadOnlyList<double> Samples { get; init; } = Array.Empty<double>();
    public StatSummary Summary { get; init; } = StatSummary.Empty;
    public IReadOnlyList<BenchmarkMetric> Metrics { get; init; } = Array.Empty<BenchmarkMetric>();
    /// <summary>Duração de parede (s) de cada iteração medida, cronometrada pelo executor.</summary>
    public IReadOnlyList<double> IterationWallSeconds { get; init; } = Array.Empty<double>();
    public int WarmupIterations { get; init; }
    public TestConditions? Conditions { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public bool Cancelled => Status == BenchmarkStatus.Cancelled;
    /// <summary>True se carga de fundo &gt; 10% ou CV das amostras &gt; 5%.</summary>
    public bool UnstableConditions { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Mensagem pt-BR (motivo de falha/cancelamento ou observação).</summary>
    public string? Message { get; init; }
    public string Methodology { get; init; } = "";
    /// <summary>Parâmetros que definem a carga de trabalho. Resultados só são comparáveis se forem iguais.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    /// <summary>Informações adicionais (não afetam comparabilidade): checksums, bytes gravados etc.</summary>
    public IReadOnlyDictionary<string, string> Info { get; init; } = new Dictionary<string, string>();
    public string EngineVersion { get; init; } = BenchmarkEngine.Version;

    public BenchmarkMetric? GetMetric(string key) => Metrics.FirstOrDefault(m => m.Key == key);
}

public static class BenchmarkEngine
{
    /// <summary>Versão da metodologia. Mudanças de carga de trabalho incrementam este número.</summary>
    public const string Version = "1.0";
}
