using System.Globalization;

namespace Rbm.Benchmarks.Cpu;

/// <summary>
/// Carga de trabalho de CPU determinística. Uma "unidade" é um bloco fixo de trabalho com checksum de referência.
/// Uma iteração ST executa <see cref="UnitsPerIteration"/> unidades; a MT executa o mesmo número POR thread.
/// </summary>
internal abstract class CpuWorkload
{
    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract string Unit { get; }
    public abstract string Methodology { get; }
    public abstract int UnitsPerIteration { get; }

    /// <summary>Quantidade de trabalho de uma unidade, na unidade de medida (ex.: milhões de operações, MB).</summary>
    public double AmountPerUnit { get; protected set; }
    /// <summary>Checksum de referência de uma unidade (calculado no Prepare).</summary>
    public ulong ReferenceChecksum { get; protected set; }
    public bool Prepared { get; private set; }

    public void Prepare()
    {
        if (Prepared) return;
        PrepareCore();
        var st = CreateThreadState();
        ReferenceChecksum = RunUnit(st);
        Prepared = true;
    }

    protected abstract void PrepareCore();
    public abstract object CreateThreadState();
    public abstract ulong RunUnit(object state);
    public abstract IReadOnlyDictionary<string, string> Parameters { get; }

    protected static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Inteiros: hash de 4 vias sobre 256 KiB (cabe no L2). Operação = 1 palavra de 64 bits processada.</summary>
internal sealed class IntegerHashWorkload(int words = 32 * 1024, int units = 16384) : CpuWorkload
{
    private ulong[] _data = Array.Empty<ulong>();
    public override string Key => "integer";
    public override string Name => "Inteiros (hash 64 bits)";
    public override string Unit => BenchmarkUnits.MegaOpsPerSecond;
    public override int UnitsPerIteration => units;
    public override string Methodology =>
        $"Buffer fixo de {words} palavras de 64 bits ({words * 8 / 1024} KiB, cabe no cache L2) gerado por xorshift64* com semente fixa. " +
        "Cada unidade de trabalho calcula um hash de 4 vias independentes (xor, multiplicação 64×64, xorshift, rotação, soma) sobre o buffer inteiro. " +
        $"Operação = 1 palavra processada. Uma iteração = {units} passagens por thread. O checksum de cada passagem é comparado ao de referência.";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["words"] = words.ToString(CultureInfo.InvariantCulture) };

    protected override void PrepareCore()
    {
        _data = CpuKernels.CreateIntegerInput(words);
        AmountPerUnit = words / 1e6;
    }
    public override object CreateThreadState() => _data; // somente leitura, compartilhado
    public override ulong RunUnit(object state) => CpuKernels.HashWords((ulong[])state);
}

/// <summary>Ponto flutuante: Mandelbrot em grade fixa. Operação = 1 iteração z←z²+c (precisão dupla).</summary>
internal sealed class MandelbrotWorkload(int width = 320, int height = 240, int maxIter = 256, int units = 48) : CpuWorkload
{
    public override string Key => "float";
    public override string Name => "Ponto flutuante (Mandelbrot)";
    public override string Unit => BenchmarkUnits.MegaOpsPerSecond;
    public override int UnitsPerIteration => units;
    public override string Methodology =>
        $"Conjunto de Mandelbrot em grade fixa {width}×{height}, região x∈[{CpuKernels.MandelXMin}; {CpuKernels.MandelXMax}], y∈[{CpuKernels.MandelYMin}; {CpuKernels.MandelYMax}]," +
        $"máximo de {maxIter} iterações por pixel, precisão dupla escalar (IEEE 754, determinística). " +
        $"Operação = 1 iteração z←z²+c (≈7 operações de ponto flutuante). Uma unidade = grade completa; uma iteração = {units} grades por thread. " +
        "Checksum = soma ponderada das contagens por pixel.";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string>
    {
        ["width"] = width.ToString(CultureInfo.InvariantCulture),
        ["height"] = height.ToString(CultureInfo.InvariantCulture),
        ["maxIter"] = maxIter.ToString(CultureInfo.InvariantCulture),
    };

    protected override void PrepareCore()
    {
        var (iters, _) = CpuKernels.Mandelbrot(width, height, maxIter, 0, height);
        AmountPerUnit = iters / 1e6;
    }
    public override object CreateThreadState() => this;
    public override ulong RunUnit(object state) => CpuKernels.Mandelbrot(width, height, maxIter, 0, height).Checksum;
}

/// <summary>Compressão Deflate (nível Optimal) de texto pseudoaleatório compressível. Medida em MB/s de entrada.</summary>
internal sealed class DeflateWorkload(int bytes = 2 * 1024 * 1024, int units = 12) : CpuWorkload
{
    private byte[] _input = Array.Empty<byte>();
    public override string Key => "compression";
    public override string Name => "Compressão (Deflate)";
    public override string Unit => BenchmarkUnits.MegabytesPerSecond;
    public override int UnitsPerIteration => units;
    public double CompressionRatio { get; private set; }
    public override string Methodology =>
        $"Texto pseudoaleatório compressível de {bytes / 1024} KiB (vocabulário fixo de 512 palavras, semente fixa, distribuição enviesada) " +
        "comprimido com System.IO.Compression.DeflateStream (CompressionLevel.Optimal; zlib-ng embutido no .NET) para um buffer em memória reutilizado. " +
        $"Vazão = bytes de ENTRADA (10^6) por segundo. Uma iteração = {units} compressões por thread. " +
        "O checksum da saída comprimida é comparado ao de referência; a descompressão é validada uma vez na preparação.";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["inputBytes"] = bytes.ToString(CultureInfo.InvariantCulture) };

    protected override void PrepareCore()
    {
        _input = CpuKernels.CreateCompressibleText(bytes);
        using var ms = new MemoryStream(bytes);
        CpuKernels.DeflateCompress(_input, ms);
        var round = CpuKernels.DeflateDecompress(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        if (!round.AsSpan().SequenceEqual(_input))
            throw new Framework.BenchmarkFailureException("Validação da compressão falhou: a descompressão não reproduziu a entrada.");
        CompressionRatio = bytes / (double)ms.Length;
        AmountPerUnit = bytes / 1e6;
    }
    public override object CreateThreadState() => new MemoryStream(_input.Length);
    public override ulong RunUnit(object state) => CpuKernels.DeflateCompress(_input, (MemoryStream)state);
}

/// <summary>Ordenação de 1 milhão de inteiros de 32 bits (Array.Sort, introsort). Medida em milhões de elementos/s.</summary>
internal sealed class SortWorkload(int count = 1_000_000, int units = 8) : CpuWorkload
{
    private int[] _master = Array.Empty<int>();
    public override string Key => "sort";
    public override string Name => "Ordenação (inteiros)";
    public override string Unit => BenchmarkUnits.MegaElementsPerSecond;
    public override int UnitsPerIteration => units;
    public override string Methodology =>
        $"Vetor de {count:N0} inteiros de 32 bits gerado por xorshift64* com semente fixa. Cada unidade copia o vetor original " +
        "para um vetor de trabalho da thread (cópia incluída no tempo, ~1% do total) e ordena com Array.Sort (introsort). " +
        $"Vazão = elementos ordenados por segundo. Uma iteração = {units} ordenações por thread. Ordenação validada por completo na preparação; " +
        "cada unidade é verificada por checksum amostral do vetor ordenado.";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["elements"] = count.ToString(CultureInfo.InvariantCulture) };

    protected override void PrepareCore()
    {
        _master = CpuKernels.CreateSortInput(count);
        var probe = new int[count];
        CpuKernels.SortAndChecksum(_master, probe);
        if (!CpuKernels.IsSorted(probe))
            throw new Framework.BenchmarkFailureException("Validação da ordenação falhou.");
        AmountPerUnit = count / 1e6;
    }
    public override object CreateThreadState() => new int[_master.Length];
    public override ulong RunUnit(object state) => CpuKernels.SortAndChecksum(_master, (int[])state);
}

/// <summary>SHA-256 sobre buffer fixo. Medida em MB/s.</summary>
internal sealed class Sha256Workload(int bytes = 4 * 1024 * 1024, int units = 192) : CpuWorkload
{
    private byte[] _input = Array.Empty<byte>();
    public override string Key => "sha256";
    public override string Name => "SHA-256";
    public override string Unit => BenchmarkUnits.MegabytesPerSecond;
    public override int UnitsPerIteration => units;
    public override string Methodology =>
        $"SHA-256 (System.Security.Cryptography.SHA256.HashData, implementação CNG do Windows) sobre {bytes / 1024} KiB de bytes pseudoaleatórios (semente fixa). " +
        "ATENÇÃO: a CNG usa as extensões SHA da CPU (SHA-NI) quando disponíveis; o resultado mede essa via acelerada, não um hash puramente em software, " +
        $"e não é comparável entre CPUs com e sem SHA-NI como medida de 'inteiros'. Vazão = bytes (10^6) por segundo. Uma iteração = {units} hashes por thread.";
    public override IReadOnlyDictionary<string, string> Parameters => new Dictionary<string, string> { ["inputBytes"] = bytes.ToString(CultureInfo.InvariantCulture) };

    protected override void PrepareCore()
    {
        _input = CpuKernels.CreateRandomBytes(bytes);
        AmountPerUnit = bytes / 1e6;
    }
    public override object CreateThreadState() => new byte[32];
    public override ulong RunUnit(object state) => CpuKernels.Sha256Checksum(_input, (byte[])state);
}
