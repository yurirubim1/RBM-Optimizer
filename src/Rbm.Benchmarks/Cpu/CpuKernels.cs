using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rbm.Benchmarks.Framework;

namespace Rbm.Benchmarks.Cpu;

/// <summary>
/// Núcleos de trabalho determinísticos. Cada um devolve um checksum que depende de todo o trabalho realizado,
/// impedindo que o JIT elimine o cálculo, e permitindo detectar resultados incorretos (instabilidade de hardware).
/// </summary>
public static class CpuKernels
{
    // ---------- Inteiros ----------

    /// <summary>Gera o buffer de entrada do teste de inteiros (xorshift64*, semente fixa).</summary>
    public static ulong[] CreateIntegerInput(int words, ulong seed = 0xA9E7_5EED_0000_0001UL)
    {
        var rng = new XorShift64Star(seed);
        var data = new ulong[words];
        for (int i = 0; i < data.Length; i++) data[i] = rng.NextUInt64();
        return data;
    }

    /// <summary>
    /// Hash de 4 vias independentes sobre palavras de 64 bits: por palavra = xor, multiplicação 64×64,
    /// xorshift, rotação e soma. As 4 vias exercitam o paralelismo de instruções do núcleo.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong HashWords(ReadOnlySpan<ulong> data)
    {
        ulong h0 = 0x243F6A8885A308D3UL, h1 = 0x13198A2E03707344UL, h2 = 0xA4093822299F31D0UL, h3 = 0x082EFA98EC4E6C89UL;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            h0 = Mix(h0, data[i]);
            h1 = Mix(h1, data[i + 1]);
            h2 = Mix(h2, data[i + 2]);
            h3 = Mix(h3, data[i + 3]);
        }
        for (; i < data.Length; i++) h0 = Mix(h0, data[i]);
        ulong h = h0 ^ BitOperations.RotateLeft(h1, 16) ^ BitOperations.RotateLeft(h2, 32) ^ BitOperations.RotateLeft(h3, 48);
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h, ulong w)
    {
        h ^= w;
        h *= 0x9E3779B97F4A7C15UL;
        h ^= h >> 29;
        return BitOperations.RotateLeft(h, 23) + w;
    }

    /// <summary>Hash de bytes (usa <see cref="HashWords"/> + cauda), para checksums de saídas.</summary>
    public static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(bytes);
        ulong h = HashWords(words);
        var tail = bytes[(words.Length * 8)..];
        ulong t = (ulong)bytes.Length;
        for (int i = 0; i < tail.Length; i++) t = (t << 8 | tail[i]) * 0x100000001B3UL;
        return h ^ (t * 0x9E3779B97F4A7C15UL);
    }

    // ---------- Ponto flutuante (Mandelbrot) ----------

    public const double MandelXMin = -2.2, MandelXMax = 1.0, MandelYMin = -1.2, MandelYMax = 1.2;

    /// <summary>
    /// Mandelbrot em grade fixa, precisão dupla escalar. Retorna o total de iterações z←z²+c executadas
    /// (a "operação" contada) e um checksum ponderado pela posição de cada pixel.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static (long Iterations, ulong Checksum) Mandelbrot(int width, int height, int maxIter, int rowStart, int rowEnd)
    {
        long total = 0;
        ulong checksum = 0;
        double dx = (MandelXMax - MandelXMin) / width, dy = (MandelYMax - MandelYMin) / height;
        for (int y = rowStart; y < rowEnd; y++)
        {
            double ci = MandelYMin + y * dy;
            for (int x = 0; x < width; x++)
            {
                double cr = MandelXMin + x * dx;
                double zr = 0, zi = 0;
                int n = 0;
                while (n < maxIter)
                {
                    double zr2 = zr * zr, zi2 = zi * zi;
                    if (zr2 + zi2 > 4.0) break;
                    zi = 2.0 * zr * zi + ci;
                    zr = zr2 - zi2 + cr;
                    n++;
                }
                total += n;
                checksum = checksum * 0x100000001B3UL + (ulong)n + (ulong)(y * width + x);
            }
        }
        return (total, checksum);
    }

    // ---------- Compressão ----------

    /// <summary>
    /// Texto pseudoaleatório compressível: vocabulário fixo de 512 "palavras" geradas por semente,
    /// escolhidas com distribuição enviesada (aprox. Zipf), separadas por espaço/quebra de linha.
    /// </summary>
    public static byte[] CreateCompressibleText(int bytes, ulong seed = 0xC0DE_C0DE_2024UL)
    {
        var rng = new XorShift64Star(seed);
        var vocab = new byte[512][];
        for (int i = 0; i < vocab.Length; i++)
        {
            int len = 2 + (int)rng.NextBelow(9);
            var w = new byte[len];
            for (int j = 0; j < len; j++) w[j] = (byte)('a' + (int)rng.NextBelow(26));
            vocab[i] = w;
        }
        var data = new byte[bytes];
        int pos = 0, wordsInLine = 0;
        while (pos < bytes)
        {
            ulong a = rng.NextBelow(512), b = rng.NextBelow(512);
            var w = vocab[(int)(a * b / 512)];
            for (int j = 0; j < w.Length && pos < bytes; j++) data[pos++] = w[j];
            if (pos < bytes) data[pos++] = (byte)(++wordsInLine % 12 == 0 ? '\n' : ' ');
        }
        return data;
    }

    /// <summary>Comprime com Deflate (CompressionLevel.Optimal) para <paramref name="output"/> (reutilizado). Retorna o checksum da saída.</summary>
    public static ulong DeflateCompress(ReadOnlySpan<byte> input, MemoryStream output)
    {
        output.Position = 0;
        output.SetLength(0);
        using (var ds = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(input);
        return HashBytes(output.GetBuffer().AsSpan(0, (int)output.Length)) ^ (ulong)output.Length;
    }

    public static byte[] DeflateDecompress(ReadOnlySpan<byte> compressed)
    {
        using var src = new MemoryStream(compressed.ToArray());
        using var ds = new DeflateStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        ds.CopyTo(dst);
        return dst.ToArray();
    }

    // ---------- Ordenação ----------

    public static int[] CreateSortInput(int count, ulong seed = 0x5027_0000_1234UL)
    {
        var rng = new XorShift64Star(seed);
        var a = new int[count];
        for (int i = 0; i < a.Length; i++) a[i] = (int)rng.NextUInt64();
        return a;
    }

    /// <summary>Copia <paramref name="master"/> para <paramref name="work"/> e ordena (introsort do .NET). Retorna checksum do resultado.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong SortAndChecksum(int[] master, int[] work)
    {
        master.AsSpan().CopyTo(work);
        Array.Sort(work);
        ulong c = (ulong)work.Length;
        for (int i = 0; i < work.Length; i += 997) c = c * 0x100000001B3UL + (uint)work[i] + (ulong)i;
        return c ^ (uint)work[^1];
    }

    public static bool IsSorted(ReadOnlySpan<int> a)
    {
        for (int i = 1; i < a.Length; i++) if (a[i - 1] > a[i]) return false;
        return true;
    }

    // ---------- SHA-256 ----------

    public static byte[] CreateRandomBytes(int count, ulong seed = 0x5_4A25_6000UL)
    {
        var data = new byte[count];
        new XorShift64Star(seed).Fill(data);
        return data;
    }

    /// <summary>SHA-256 via CNG (Windows); usa extensões SHA da CPU quando disponíveis. Checksum = primeiros 8 bytes.</summary>
    public static ulong Sha256Checksum(ReadOnlySpan<byte> input, Span<byte> hash32)
    {
        SHA256.HashData(input, hash32);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash32);
    }
}
