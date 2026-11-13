using System.Runtime.InteropServices;

namespace Rbm.Monitoring;

/// <summary>
/// Wrapper fino sobre a Performance Data Helper (PDH). Usa nomes de contadores em inglês
/// (PdhAddEnglishCounter) para funcionar em qualquer idioma do Windows e suporta curingas (*).
/// </summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    private readonly HashSet<string> _failed = new();

    public PdhQuery()
    {
        uint r = PdhOpenQuery(null, IntPtr.Zero, out _query);
        if (r != 0) throw new InvalidOperationException($"PdhOpenQuery falhou: 0x{r:X8}");
    }

    public IReadOnlyCollection<string> FailedCounters => _failed;

    public bool Add(string path)
    {
        if (_counters.ContainsKey(path)) return true;
        uint r = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var h);
        if (r != 0) { _failed.Add(path); return false; }
        _counters[path] = h;
        return true;
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public double? Value(string path)
    {
        if (!_counters.TryGetValue(path, out var h)) return null;
        uint r = PdhGetFormattedCounterValue(h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v);
        if (r != 0 || (v.CStatus != 0 && v.CStatus != 1)) return null; // PDH_CSTATUS_VALID_DATA / NEW_DATA
        return v.doubleValue;
    }

    /// <summary>Valores de um contador com curinga, por nome de instância.</summary>
    public Dictionary<string, double> Array(string path)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!_counters.TryGetValue(path, out var h)) return result;
        uint size = 0, count = 0;
        uint r = PdhGetFormattedCounterArray(h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = PdhGetFormattedCounterArray(h, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, ref count, buf);
            if (r != 0) return result;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                if (item.FmtValue.CStatus is not (0 or 1)) continue;
                var name = Marshal.PtrToStringUni(item.szName) ?? "";
                // Instâncias duplicadas (ex.: GPU Engine) são somadas.
                result[name] = result.TryGetValue(name, out var prev) ? prev + item.FmtValue.doubleValue : item.FmtValue.doubleValue;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
