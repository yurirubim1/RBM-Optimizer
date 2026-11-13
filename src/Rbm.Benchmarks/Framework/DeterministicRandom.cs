namespace Rbm.Benchmarks.Framework;

/// <summary>
/// Gerador xorshift64* com semente fixa. Determinístico entre execuções, máquinas e versões do .NET
/// (ao contrário de <see cref="Random"/>, cujo algoritmo não é garantido).
/// </summary>
public struct XorShift64Star
{
    private ulong _state;

    public XorShift64Star(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong NextUInt64()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Inteiro uniforme em [0, bound) (método de Lemire, sem viés relevante p/ bound &lt;&lt; 2^64).</summary>
    public ulong NextBelow(ulong bound) => (ulong)(((UInt128)NextUInt64() * bound) >> 64);

    public void Fill(Span<byte> buffer)
    {
        int i = 0;
        for (; i + 8 <= buffer.Length; i += 8)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[i..], NextUInt64());
        if (i < buffer.Length)
        {
            ulong v = NextUInt64();
            for (; i < buffer.Length; i++) { buffer[i] = (byte)v; v >>= 8; }
        }
    }
}
