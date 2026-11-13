using System.Globalization;
using Rbm.Benchmarks.Framework;

namespace Rbm.Benchmarks.Memory;

public enum MemoryBandwidthKind { Read, Write, Copy }

/// <summary>Largura de banda de memória (leitura/escrita/cópia) com Vector&lt;T&gt; em buffer muito maior que o L3. IDs: mem.bandwidth.{read|write|copy}.</summary>
public sealed unsafe class MemoryBandwidthBenchmark(MemoryBandwidthKind kind) : IterativeBenchmark
{
    public MemoryBandwidthKind Kind { get; } = kind;

    private int BasePasses => Kind switch { MemoryBandwidthKind.Read => 16, MemoryBandwidthKind.Write => 16, _ => 8 };

    public override string Id => $"mem.bandwidth.{Kind.ToString().ToLowerInvariant()}";
    public override string Name => Kind switch
    {
        MemoryBandwidthKind.Read => "Memória — largura de banda de leitura",
        MemoryBandwidthKind.Write => "Memória — largura de banda de escrita",
        _ => "Memória — largura de banda de cópia",
    };
    public override BenchmarkCategory Category => BenchmarkCategory.Memory;
    public override string Unit => BenchmarkUnits.GigabytesPerSecond;
    public override bool HigherIsBetter => true;
    public override string Methodology =>
        "Buffer nativo alinhado a 4 KiB (NativeMemory.AlignedAlloc), padrão 512 MiB (configurável; deve ser muito maior que o cache L3), " +
        "tocado por inteiro antes da medição. O buffer é dividido em fatias contíguas iguais, uma por thread (padrão N = Environment.ProcessorCount, " +
        "prioridade Normal), e cada thread percorre sua fatia com Vector<T> (" + System.Numerics.Vector<byte>.Count * 8 + " bits nesta máquina), 4 vetores por passo. " +
        Kind switch
        {
            MemoryBandwidthKind.Read => $"LEITURA: soma de todas as palavras de 64 bits ({BasePasses} passagens por iteração); a soma é validada contra o valor esperado. GB/s = bytes lidos / tempo.",
            MemoryBandwidthKind.Write => $"ESCRITA: grava um valor constante em todo o buffer ({BasePasses} passagens por iteração) usando stores normais (com leitura-para-posse, RFO; " +
                "ferramentas que usam stores não temporais reportam valores maiores). GB/s = bytes gravados / tempo. Conteúdo verificado por amostragem.",
            _ => $"CÓPIA: o buffer é dividido em origem e destino (metade cada) e copiado com Vector<T> ({BasePasses} passagens por iteração). " +
                "GB/s segue a convenção STREAM: bytes lidos + bytes gravados, por segundo. Conteúdo verificado por amostragem.",
        } +
        " GB = 10^9 bytes. Código gerenciado (.NET JIT): mede a vazão alcançável por um programa típico, não o pico teórico do controlador de memória. Compare apenas na mesma máquina.";

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(this, options);

    private sealed class Session(MemoryBandwidthBenchmark b, BenchmarkOptions o) : BenchmarkSession
    {
        private NativeBuffer? _buf;
        private WorkerGroup? _group;
        private long _chunk;     // bytes por thread (por buffer, na cópia)
        private long _half;      // bytes de cada metade (cópia)
        private int _threads, _passes;
        private ulong _pass;

        public override void Prepare(SessionContext ctx)
        {
            _threads = o.EffectiveThreads;
            long total = o.MemoryBandwidthBufferBytes;
            MemoryGuard.EnsureAvailable(total);
            int parts = b.Kind == MemoryBandwidthKind.Copy ? 2 * _threads : _threads;
            _chunk = total / parts / 4096 * 4096;
            if (_chunk < 4096) throw new BenchmarkFailureException("Buffer pequeno demais para o número de threads.");
            _half = _chunk * _threads;
            long used = b.Kind == MemoryBandwidthKind.Copy ? 2 * _half : _half;
            _passes = o.Scale(b.BasePasses);

            ctx.Detail($"Alocando e inicializando {used / (double)BenchmarkOptions.MiB:0} MiB...");
            _buf = new NativeBuffer(used);
            _group = new WorkerGroup(_threads, "RBMBench Mem");
            byte* basePtr = _buf.Pointer;
            long chunk = _chunk;
            _group.Run(i => MemoryKernels.FillIndexPattern(basePtr + i * chunk, chunk, i * chunk / 8));
            if (b.Kind == MemoryBandwidthKind.Copy)
                _group.Run(i => MemoryKernels.Fill(basePtr + _half + i * chunk, chunk, 0));

            Parameters["kind"] = b.Kind.ToString();
            Parameters["bufferBytes"] = used.ToString(CultureInfo.InvariantCulture);
            Parameters["threads"] = _threads.ToString(CultureInfo.InvariantCulture);
            Parameters["passes"] = _passes.ToString(CultureInfo.InvariantCulture);
            Parameters["vectorBits"] = (System.Numerics.Vector<byte>.Count * 8).ToString(CultureInfo.InvariantCulture);
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            var ct = ctx.Ct;
            byte* basePtr = _buf!.Pointer;
            long chunk = _chunk, half = _half;
            int passes = _passes;
            double seconds;
            double bytesCounted;
            switch (b.Kind)
            {
                case MemoryBandwidthKind.Read:
                {
                    var sums = new ulong[_threads];
                    seconds = _group!.Run(i =>
                    {
                        ulong s = 0;
                        for (int p = 0; p < passes; p++)
                        {
                            ct.ThrowIfCancellationRequested();
                            s += MemoryKernels.ReadSum(basePtr + i * chunk, chunk);
                        }
                        sums[i] = s;
                    });
                    ulong total = 0;
                    foreach (var s in sums) total += s;
                    ulong expected = MemoryKernels.ExpectedIndexSum(half / 8) * (ulong)passes;
                    if (total != expected)
                        throw new BenchmarkFailureException("Soma de verificação da leitura divergente — resultado descartado (possível instabilidade de memória).");
                    bytesCounted = (double)half * passes;
                    break;
                }
                case MemoryBandwidthKind.Write:
                {
                    ulong value = 0xA5A5_0000_0000_0000UL ^ ++_pass;
                    seconds = _group!.Run(i =>
                    {
                        for (int p = 0; p < passes; p++)
                        {
                            ct.ThrowIfCancellationRequested();
                            MemoryKernels.Fill(basePtr + i * chunk, chunk, value);
                        }
                    });
                    VerifySamples(basePtr, half, w => w == value, "escrita");
                    bytesCounted = (double)half * passes;
                    break;
                }
                default:
                {
                    seconds = _group!.Run(i =>
                    {
                        for (int p = 0; p < passes; p++)
                        {
                            ct.ThrowIfCancellationRequested();
                            MemoryKernels.Copy(basePtr + i * chunk, basePtr + half + i * chunk, chunk);
                        }
                    });
                    byte* dst = basePtr + half;
                    VerifyCopy(dst, half);
                    bytesCounted = 2.0 * half * passes;
                    break;
                }
            }
            return new IterationMeasurement(bytesCounted / seconds / 1e9);
        }

        private static void VerifySamples(byte* p, long bytes, Func<ulong, bool> ok, string what)
        {
            ulong* w = (ulong*)p;
            long n = bytes / 8;
            for (long i = 0; i < n; i += 4099)
                if (!ok(w[i])) throw new BenchmarkFailureException($"Verificação da {what} divergente — resultado descartado.");
            if (!ok(w[n - 1])) throw new BenchmarkFailureException($"Verificação da {what} divergente — resultado descartado.");
        }

        private static void VerifyCopy(byte* dst, long bytes)
        {
            ulong* w = (ulong*)dst;
            long n = bytes / 8;
            for (long i = 0; i < n; i += 4099)
                if (w[i] != (ulong)i) throw new BenchmarkFailureException("Verificação da cópia divergente — resultado descartado.");
            if (w[n - 1] != (ulong)(n - 1)) throw new BenchmarkFailureException("Verificação da cópia divergente — resultado descartado.");
        }

        public override void Dispose()
        {
            _group?.Dispose();
            _buf?.Dispose();
        }
    }
}

public enum MemoryLatencyKind { Dram, Cache }

/// <summary>
/// Latência por perseguição de ponteiros aleatória (Sattolo, passo de linha de cache de 64 B). IDs: mem.latency.dram, mem.latency.cache.
/// </summary>
public sealed unsafe class MemoryLatencyBenchmark(MemoryLatencyKind kind) : IterativeBenchmark
{
    public const int LineStride = 64;
    public MemoryLatencyKind Kind { get; } = kind;

    private long BaseSteps => Kind == MemoryLatencyKind.Dram ? 8_000_000 : 64_000_000;

    public override string Id => Kind == MemoryLatencyKind.Dram ? "mem.latency.dram" : "mem.latency.cache";
    public override string Name => Kind == MemoryLatencyKind.Dram ? "Memória — latência (DRAM)" : "Memória — latência (cache L2)";
    public override BenchmarkCategory Category => BenchmarkCategory.Memory;
    public override string Unit => BenchmarkUnits.Nanoseconds;
    public override bool HigherIsBetter => false;
    public override string Methodology =>
        (Kind == MemoryLatencyKind.Dram
            ? "Buffer de 256 MiB (configurável; muito maior que o L3). "
            : "Buffer pequeno de 256 KiB (configurável; maior que o L1d e menor que o L2 típico) para mostrar a latência de cache em contraste com a DRAM. ") +
        "Uma linha de 64 bytes por elemento; as linhas formam um único ciclo aleatório (algoritmo de Sattolo, xorshift64* com semente fixa), " +
        "de modo que cada acesso depende do anterior (p = *p) e o prefetcher não consegue prever o próximo endereço. " +
        $"Uma iteração = {BaseSteps:N0} acessos dependentes em uma thread; resultado = tempo / acessos (ns por acesso). " +
        "Limitações: páginas de 4 KiB — em buffers grandes o valor inclui faltas de TLB (page walks), por isso fica acima da latência 'pura' " +
        "anunciada por ferramentas que usam páginas grandes; código gerenciado; compare apenas na mesma máquina.";

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(this, options);

    private sealed class Session(MemoryLatencyBenchmark b, BenchmarkOptions o) : BenchmarkSession
    {
        private NativeBuffer? _buf;
        private long _steps;
        private nint? _referenceEnd;

        public override void Prepare(SessionContext ctx)
        {
            long size = b.Kind == MemoryLatencyKind.Dram ? o.MemoryLatencyBufferBytes : o.CacheLatencyBufferBytes;
            long lines = size / LineStride;
            if (lines < 2 || lines > int.MaxValue) throw new BenchmarkFailureException("Tamanho de buffer de latência inválido.");
            size = lines * LineStride;
            MemoryGuard.EnsureAvailable(size + lines * 4);
            ctx.Detail("Montando cadeia aleatória de ponteiros (Sattolo)...");
            var next = MemoryKernels.BuildSattoloCycle((int)lines);
            ctx.Ct.ThrowIfCancellationRequested();
            _buf = new NativeBuffer(size);
            MemoryKernels.LinkChain(_buf.Pointer, next, LineStride); // também toca todas as páginas
            _steps = Math.Max(8, (long)Math.Round(b.BaseSteps * o.WorkScale) / 8 * 8);

            Parameters["kind"] = b.Kind.ToString();
            Parameters["bufferBytes"] = size.ToString(CultureInfo.InvariantCulture);
            Parameters["stride"] = LineStride.ToString(CultureInfo.InvariantCulture);
            Parameters["steps"] = _steps.ToString(CultureInfo.InvariantCulture);
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            nint start = (nint)_buf!.Pointer;
            // Divide em blocos para responder ao cancelamento rapidamente.
            const long block = 1 << 20;
            nint p = start;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long remaining = _steps;
            while (remaining > 0)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                long n = Math.Min(block, remaining);
                p = MemoryKernels.Chase(p, n);
                remaining -= n;
            }
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_referenceEnd is null) _referenceEnd = p;
            else if (_referenceEnd != p)
                throw new BenchmarkFailureException("Posição final da cadeia divergente — resultado descartado (possível instabilidade de memória).");
            Info["finalLine"] = ((p - start) / LineStride).ToString(CultureInfo.InvariantCulture);
            return new IterationMeasurement(Timing.TicksToSeconds(t1 - t0) * 1e9 / _steps);
        }

        public override void Dispose() => _buf?.Dispose();
    }
}
