using System.Runtime.InteropServices;
using Rbm.Processes.Native;

namespace Rbm.Processes;

/// <summary>
/// Enumera todos os processos com UMA chamada a NtQuerySystemInformation(SystemProcessInformation).
/// Reaproveita o buffer nativo e o cache de nomes entre chamadas (baixa alocação). Não é thread-safe:
/// use uma instância por consumidor ou o método estático <see cref="Snapshot"/>.
/// </summary>
public sealed unsafe class SystemProcessEnumerator : IDisposable
{
    private nint _buffer;
    private uint _size;
    private readonly Dictionary<ProcessKey, string> _names = new();
    private bool _disposed;

    /// <summary>Tamanho esperado da estrutura nativa em x64 (verificação defensiva de layout).</summary>
    internal const int ExpectedStructSize = 256;

    public SystemProcessEnumerator(uint initialBufferBytes = 512 * 1024)
    {
        _size = initialBufferBytes;
        _buffer = Marshal.AllocHGlobal((nint)_size);
    }

    /// <summary>Snapshot único sem manter estado (para o agente/ações pontuais).</summary>
    public static IReadOnlyList<RawProcessInfo> Snapshot()
    {
        using var e = new SystemProcessEnumerator();
        return e.Enumerate();
    }

    /// <summary>Procura um processo por PID numa enumeração nova.</summary>
    public static RawProcessInfo? Find(int pid)
    {
        foreach (var p in Snapshot()) if (p.Pid == pid) return p;
        return null;
    }

    /// <summary>Lê todos os processos. Lança <see cref="InvalidOperationException"/> se a chamada nativa falhar.</summary>
    public IReadOnlyList<RawProcessInfo> Enumerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sizeof(SYSTEM_PROCESS_INFORMATION) != ExpectedStructSize || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Layout de SYSTEM_PROCESS_INFORMATION inesperado (somente x64 é suportado).");
        int status = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformationClass, (void*)_buffer, _size, out uint needed);
            if (status == 0) return Parse();
            if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH) break;
            // Cresce com folga (processos novos entre a consulta e a nova chamada).
            uint newSize = Math.Max(needed + 64 * 1024, _size * 2);
            Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal((nint)newSize);
            _size = newSize;
        }
        throw new InvalidOperationException($"NtQuerySystemInformation falhou (NTSTATUS 0x{status:X8}).");
    }

    private List<RawProcessInfo> Parse()
    {
        var list = new List<RawProcessInfo>(Math.Max(64, _names.Count + 16));
        var seen = new HashSet<ProcessKey>();
        byte* p = (byte*)_buffer;
        byte* end = p + _size;
        while (true)
        {
            var spi = (SYSTEM_PROCESS_INFORMATION*)p;
            int pid = (int)spi->UniqueProcessId;
            var key = new ProcessKey(pid, spi->CreateTime);
            if (!_names.TryGetValue(key, out var name))
            {
                if (spi->ImageNameBuffer != 0 && spi->ImageNameLength > 0)
                    name = new string((char*)spi->ImageNameBuffer, 0, spi->ImageNameLength / 2);
                else name = pid == 0 ? "Idle" : pid == 4 ? "System" : $"PID {pid}";
                _names[key] = name;
            }
            seen.Add(key);
            list.Add(new RawProcessInfo(
                pid, (int)spi->InheritedFromUniqueProcessId, name, spi->CreateTime, (int)spi->SessionId,
                (int)spi->NumberOfThreads, (int)spi->HandleCount, spi->BasePriority,
                (long)spi->WorkingSetSize, (long)spi->PrivatePageCount, (long)spi->PeakWorkingSetSize,
                spi->KernelTime, spi->UserTime,
                spi->ReadTransferCount, spi->WriteTransferCount, spi->OtherTransferCount, true));

            if (spi->NextEntryOffset == 0) break;
            p += spi->NextEntryOffset;
            if (p >= end) break;
        }
        // Remove nomes de processos que já terminaram.
        if (_names.Count > seen.Count + 32)
            foreach (var k in _names.Keys.Where(k => !seen.Contains(k)).ToList()) _names.Remove(k);
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Marshal.FreeHGlobal(_buffer);
        _buffer = 0;
    }
}
