using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Rbm.Benchmarks.Cpu;
using Rbm.Benchmarks.Framework;
using Rbm.Benchmarks.Memory;
using Xunit;

namespace Rbm.Tests.Benchmarks;

public class BenchmarkKernelTests
{
    [Fact]
    public void XorShift_IsDeterministic_AndBounded()
    {
        var a = new XorShift64Star(42);
        var b = new XorShift64Star(42);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());
        var c = new XorShift64Star(7);
        for (int i = 0; i < 1000; i++) Assert.InRange(c.NextBelow(10), 0UL, 9UL);
    }

    [Fact]
    public void HashWords_MatchesIndependentReference()
    {
        var data = CpuKernels.CreateIntegerInput(1031); // não múltiplo de 4: exercita a cauda
        Assert.Equal(ReferenceHash(data), CpuKernels.HashWords(data));
        Assert.Equal(CpuKernels.HashWords(data), CpuKernels.HashWords(CpuKernels.CreateIntegerInput(1031)));
        data[500] ^= 1;
        Assert.NotEqual(ReferenceHash(CpuKernels.CreateIntegerInput(1031)), CpuKernels.HashWords(data));
    }

    private static ulong ReferenceHash(ulong[] d)
    {
        ulong[] h = { 0x243F6A8885A308D3UL, 0x13198A2E03707344UL, 0xA4093822299F31D0UL, 0x082EFA98EC4E6C89UL };
        static ulong Mix(ulong x, ulong w) { x ^= w; x *= 0x9E3779B97F4A7C15UL; x ^= x >> 29; return BitOperations.RotateLeft(x, 23) + w; }
        int full = d.Length / 4 * 4;
        for (int i = 0; i < full; i++) h[i % 4] = Mix(h[i % 4], d[i]);
        for (int i = full; i < d.Length; i++) h[0] = Mix(h[0], d[i]);
        ulong r = h[0] ^ BitOperations.RotateLeft(h[1], 16) ^ BitOperations.RotateLeft(h[2], 32) ^ BitOperations.RotateLeft(h[3], 48);
        r ^= r >> 33; r *= 0xFF51AFD7ED558CCDUL; r ^= r >> 33;
        return r;
    }

    [Fact]
    public void Mandelbrot_PartitionedRowsSumToWhole_AndIsDeterministic()
    {
        var whole = CpuKernels.Mandelbrot(64, 48, 64, 0, 48);
        var top = CpuKernels.Mandelbrot(64, 48, 64, 0, 20);
        var bottom = CpuKernels.Mandelbrot(64, 48, 64, 20, 48);
        Assert.Equal(whole.Iterations, top.Iterations + bottom.Iterations);
        Assert.Equal(whole, CpuKernels.Mandelbrot(64, 48, 64, 0, 48));
        Assert.True(whole.Iterations > 64 * 48); // há pixels dentro do conjunto
    }

    [Fact]
    public void Deflate_RoundTrips_AndChecksumIsStable()
    {
        var text = CpuKernels.CreateCompressibleText(64 * 1024);
        using var ms = new MemoryStream();
        ulong c1 = CpuKernels.DeflateCompress(text, ms);
        var back = CpuKernels.DeflateDecompress(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        Assert.Equal(text, back);
        Assert.True(ms.Length < text.Length / 2, "dados devem ser compressíveis");
        Assert.Equal(c1, CpuKernels.DeflateCompress(text, ms));
    }

    [Fact]
    public void Sort_ProducesSortedOutput_WithStableChecksum()
    {
        var master = CpuKernels.CreateSortInput(10_000);
        var work = new int[master.Length];
        ulong c = CpuKernels.SortAndChecksum(master, work);
        Assert.True(CpuKernels.IsSorted(work));
        var expected = (int[])master.Clone();
        Array.Sort(expected);
        Assert.Equal(expected, work);
        Assert.Equal(c, CpuKernels.SortAndChecksum(master, new int[master.Length]));
    }

    [Fact]
    public void Sha256Checksum_IsFirst8BytesOfDigest()
    {
        var hash = new byte[32];
        ulong c = CpuKernels.Sha256Checksum("abc"u8, hash);
        // SHA-256("abc") = ba7816bf8f01cfea...
        Assert.Equal(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString("ba7816bf8f01cfea")), c);
        Assert.Equal(SHA256.HashData("abc"u8.ToArray()), hash);
    }

    [Fact]
    public void Workloads_ReferenceChecksumIsReproducible()
    {
        CpuWorkload[] workloads =
        {
            new IntegerHashWorkload(words: 512, units: 2),
            new MandelbrotWorkload(width: 32, height: 24, maxIter: 32, units: 2),
            new DeflateWorkload(bytes: 32 * 1024, units: 2),
            new SortWorkload(count: 5000, units: 2),
            new Sha256Workload(bytes: 4096, units: 2),
        };
        foreach (var w in workloads)
        {
            w.Prepare();
            Assert.True(w.AmountPerUnit > 0, w.Key);
            var s1 = w.CreateThreadState();
            var s2 = w.CreateThreadState();
            Assert.Equal(w.ReferenceChecksum, w.RunUnit(s1));
            Assert.Equal(w.ReferenceChecksum, w.RunUnit(s2));
        }
    }

    [Fact]
    public void Sattolo_ProducesSingleCycleOverAllElements()
    {
        foreach (int n in new[] { 2, 3, 17, 4096 })
        {
            var next = MemoryKernels.BuildSattoloCycle(n);
            var seen = new bool[n];
            int p = 0;
            for (int i = 0; i < n; i++) { Assert.False(seen[p]); seen[p] = true; p = next[p]; }
            Assert.Equal(0, p);
            Assert.All(seen, Assert.True);
        }
    }

    [Fact]
    public unsafe void PointerChase_EndsWhereManagedWalkEnds()
    {
        const int n = 1024, stride = 64;
        var next = MemoryKernels.BuildSattoloCycle(n);
        using var buf = new NativeBuffer(n * stride);
        MemoryKernels.LinkChain(buf.Pointer, next, stride);
        const long steps = 8 * 1000;
        nint end = MemoryKernels.Chase((nint)buf.Pointer, steps);
        int p = 0;
        for (long i = 0; i < steps; i++) p = next[p];
        Assert.Equal((nint)(buf.Pointer + (long)p * stride), end);
    }

    [Fact]
    public unsafe void BandwidthKernels_ReadFillCopy_AreCorrect()
    {
        long bytes = MemoryKernels.BlockBytes * 64L;
        using var a = new NativeBuffer(bytes);
        using var b = new NativeBuffer(bytes);
        MemoryKernels.FillIndexPattern(a.Pointer, bytes, 0);
        Assert.Equal(MemoryKernels.ExpectedIndexSum(bytes / 8), MemoryKernels.ReadSum(a.Pointer, bytes));
        MemoryKernels.Copy(a.Pointer, b.Pointer, bytes);
        Assert.True(new Span<byte>(a.Pointer, (int)bytes).SequenceEqual(new Span<byte>(b.Pointer, (int)bytes)));
        MemoryKernels.Fill(b.Pointer, bytes, 0x1234UL);
        Assert.Equal(0x1234UL * (ulong)(bytes / 8), MemoryKernels.ReadSum(b.Pointer, bytes));
    }
}
