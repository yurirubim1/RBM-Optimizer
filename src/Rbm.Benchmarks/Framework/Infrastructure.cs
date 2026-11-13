using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Rbm.Benchmarks.Framework;

/// <summary>Falha esperada (pré-condição, checksum divergente, E/S). A mensagem é pt-BR e vai para o resultado.</summary>
public sealed class BenchmarkFailureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Buffer nativo alinhado (NativeMemory.AlignedAlloc). Não é movido pelo GC.</summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    public byte* Pointer { get; private set; }
    public long Length { get; }

    public NativeBuffer(long length, int alignment = 4096)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        Pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
    }

    public Span<byte> AsSpan(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return new Span<byte>(Pointer + offset, length);
    }

    public void Dispose()
    {
        if (Pointer != null) { NativeMemory.AlignedFree(Pointer); Pointer = null; }
        GC.SuppressFinalize(this);
    }

    ~NativeBuffer() { if (Pointer != null) NativeMemory.AlignedFree(Pointer); }
}

internal static class MemoryGuard
{
    /// <summary>Exige memória física disponível ≥ bytes + 512 MiB de margem.</summary>
    public static void EnsureAvailable(long bytes)
    {
        ulong? avail = NativeMethods.AvailablePhysicalMemory();
        long margin = 512 * BenchmarkOptions.MiB;
        if (avail is ulong a && (ulong)(bytes + margin) > a)
            throw new BenchmarkFailureException(
                $"Memória física disponível insuficiente: o teste precisa de {bytes / (double)BenchmarkOptions.MiB:0} MiB + 512 MiB de margem; disponível: {a / (double)BenchmarkOptions.MiB:0} MiB. Feche programas ou reduza o tamanho do buffer.");
    }
}

/// <summary>
/// Grupo de threads dedicadas (prioridade Normal) que executam o mesmo delegate em paralelo.
/// As threads são criadas uma vez por sessão para que a criação não entre no tempo medido.
/// </summary>
internal sealed class WorkerGroup : IDisposable
{
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim[] _start;
    private readonly CountdownEvent _done;
    private readonly List<Exception> _errors = new();
    private Action<int>? _work;
    private volatile bool _disposed;

    public int Count => _threads.Length;

    public WorkerGroup(int count, string name)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        _threads = new Thread[count];
        _start = new SemaphoreSlim[count];
        _done = new CountdownEvent(count);
        for (int i = 0; i < count; i++)
        {
            _start[i] = new SemaphoreSlim(0);
            _threads[i] = new Thread(Loop) { IsBackground = true, Name = $"{name} #{i}", Priority = ThreadPriority.Normal };
            _threads[i].Start(i);
        }
    }

    private void Loop(object? state)
    {
        int idx = (int)state!;
        while (true)
        {
            _start[idx].Wait();
            if (_disposed) return;
            try { _work!(idx); }
            catch (Exception ex) { lock (_errors) _errors.Add(ex); }
            finally { _done.Signal(); }
        }
    }

    /// <summary>Executa <paramref name="work"/>(índiceDaThread) em todas as threads; retorna o tempo de parede em segundos.</summary>
    public double Run(Action<int> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _work = work;
        lock (_errors) _errors.Clear();
        _done.Reset(_threads.Length);
        long t0 = Stopwatch.GetTimestamp();
        foreach (var s in _start) s.Release();
        _done.Wait();
        long t1 = Stopwatch.GetTimestamp();
        _work = null;
        Exception? first;
        lock (_errors)
            first = _errors.FirstOrDefault(e => e is not OperationCanceledException) ?? _errors.FirstOrDefault();
        if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
        return (t1 - t0) / (double)Stopwatch.Frequency;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _start) s.Release();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));
        foreach (var s in _start) s.Dispose();
        _done.Dispose();
    }
}

internal static class Timing
{
    public static double TicksToMicroseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
    public static double TicksToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    public static long MicrosecondsToTicks(double us) => (long)Math.Round(us * Stopwatch.Frequency / 1_000_000.0);

    /// <summary>Espera ativa (sem ceder a CPU) por <paramref name="ticks"/> do Stopwatch.</summary>
    public static void SpinFor(long ticks)
    {
        long end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(8);
    }
}

/// <summary>Utilitários de percentis sobre amostras brutas (usa o mesmo método R-7 do Core).</summary>
internal static class Distribution
{
    public static (double P50, double P99, double Max, double Mean) Of(Span<double> values)
    {
        if (values.Length == 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
        var arr = values.ToArray();
        Array.Sort(arr);
        return (Rbm.Core.Stats.Percentile(arr, 50), Rbm.Core.Stats.Percentile(arr, 99), arr[^1], arr.Average());
    }
}
