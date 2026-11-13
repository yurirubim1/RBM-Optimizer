using System.Numerics;
using System.Runtime.CompilerServices;
using Rbm.Benchmarks.Framework;

namespace Rbm.Benchmarks.Memory;

/// <summary>Núcleos SIMD (Vector&lt;T&gt;) de leitura/escrita/cópia e perseguição de ponteiros.</summary>
public static unsafe class MemoryKernels
{
    /// <summary>Granularidade exigida pelos laços (4 vetores por passo).</summary>
    public static int BlockBytes => 4 * Vector<byte>.Count;

    /// <summary>Soma (com overflow) todas as palavras de 64 bits. <paramref name="bytes"/> deve ser múltiplo de <see cref="BlockBytes"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong ReadSum(byte* p, long bytes)
    {
        int vs = Vector<byte>.Count;
        Vector<ulong> a0 = default, a1 = default, a2 = default, a3 = default;
        byte* end = p + bytes;
        for (; p + 4 * vs <= end; p += 4 * vs)
        {
            a0 += Unsafe.ReadUnaligned<Vector<ulong>>(p);
            a1 += Unsafe.ReadUnaligned<Vector<ulong>>(p + vs);
            a2 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 2 * vs);
            a3 += Unsafe.ReadUnaligned<Vector<ulong>>(p + 3 * vs);
        }
        return Vector.Sum(a0 + a1 + a2 + a3);
    }

    /// <summary>Escreve <paramref name="value"/> em todas as palavras (stores normais, com RFO).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Fill(byte* p, long bytes, ulong value)
    {
        int vs = Vector<byte>.Count;
        var v = new Vector<ulong>(value);
        byte* end = p + bytes;
        for (; p + 4 * vs <= end; p += 4 * vs)
        {
            Unsafe.WriteUnaligned(p, v);
            Unsafe.WriteUnaligned(p + vs, v);
            Unsafe.WriteUnaligned(p + 2 * vs, v);
            Unsafe.WriteUnaligned(p + 3 * vs, v);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Copy(byte* src, byte* dst, long bytes)
    {
        int vs = Vector<byte>.Count;
        byte* end = src + bytes;
        for (; src + 4 * vs <= end; src += 4 * vs, dst += 4 * vs)
        {
            var v0 = Unsafe.ReadUnaligned<Vector<byte>>(src);
            var v1 = Unsafe.ReadUnaligned<Vector<byte>>(src + vs);
            var v2 = Unsafe.ReadUnaligned<Vector<byte>>(src + 2 * vs);
            var v3 = Unsafe.ReadUnaligned<Vector<byte>>(src + 3 * vs);
            Unsafe.WriteUnaligned(dst, v0);
            Unsafe.WriteUnaligned(dst + vs, v1);
            Unsafe.WriteUnaligned(dst + 2 * vs, v2);
            Unsafe.WriteUnaligned(dst + 3 * vs, v3);
        }
    }

    /// <summary>Preenche palavra i (64 bits, índice global <paramref name="firstIndex"/>+i) com o próprio índice.</summary>
    public static void FillIndexPattern(byte* p, long bytes, long firstIndex)
    {
        ulong* w = (ulong*)p;
        long n = bytes / 8;
        for (long i = 0; i < n; i++) w[i] = (ulong)(firstIndex + i);
    }

    /// <summary>Soma esperada (mod 2^64) de 0..n-1.</summary>
    public static ulong ExpectedIndexSum(long n) => (ulong)(((UInt128)(ulong)n * (ulong)(n - 1)) / 2);

    /// <summary>
    /// Permutação cíclica única (algoritmo de Sattolo, xorshift64* com semente fixa): seguir i → next[i]
    /// visita todos os <paramref name="n"/> elementos antes de voltar ao início.
    /// </summary>
    public static int[] BuildSattoloCycle(int n, ulong seed = 0x1A7E_0C0FFEEUL)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        var rng = new XorShift64Star(seed);
        for (int i = n - 1; i > 0; i--)
        {
            int j = (int)rng.NextBelow((ulong)i); // 0..i-1 (Sattolo: j < i)
            (a[i], a[j]) = (a[j], a[i]);
        }
        return a;
    }

    /// <summary>Monta a cadeia de ponteiros: no início da linha i (passo <paramref name="stride"/>) grava o endereço da linha next[i].</summary>
    public static void LinkChain(byte* basePtr, int[] next, int stride)
    {
        for (int i = 0; i < next.Length; i++)
            *(nint*)(basePtr + (long)i * stride) = (nint)(basePtr + (long)next[i] * stride);
    }

    /// <summary>Percorre a cadeia por <paramref name="steps"/> acessos dependentes (múltiplo de 8). Retorna o ponteiro final.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static nint Chase(nint p, long steps)
    {
        for (long i = steps >> 3; i > 0; i--)
        {
            p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
            p = *(nint*)p; p = *(nint*)p; p = *(nint*)p; p = *(nint*)p;
        }
        return p;
    }
}
