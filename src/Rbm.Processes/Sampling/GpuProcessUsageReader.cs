using System.Runtime.InteropServices;
using Rbm.Processes.Native;
using static Rbm.Processes.Native.NativeMethods;

namespace Rbm.Processes;

/// <summary>Uso de GPU de um processo (contador "GPU Engine", mesma fonte do Gerenciador de Tarefas).</summary>
public sealed record ProcessGpuUsage(
    int Pid,
    // Soma de todas as engines (pode passar de 100% quando várias engines trabalham em paralelo).
    double SumPercent,
    // Maior engine individual — equivale à coluna "GPU" do Gerenciador de Tarefas.
    double MaxEnginePercent,
    IReadOnlyDictionary<string, double> ByEngineType);

public sealed record GpuUsageSnapshot(bool Available, string? Error, DateTimeOffset Timestamp, IReadOnlyDictionary<int, ProcessGpuUsage> ByPid)
{
    public static GpuUsageSnapshot Unavailable(string error) => new(false, error, DateTimeOffset.Now, new Dictionary<int, ProcessGpuUsage>());
}

/// <summary>
/// Leitor leve de "\GPU Engine(*)\Utilization Percentage" via PDH (sem PerformanceCounter/WMI).
/// Instâncias: "pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D". O contador é de taxa: a primeira leitura
/// após a criação serve de base. Custo típico: poucos ms (centenas de instâncias); leia sob demanda (≥ 1 s).
/// </summary>
public sealed unsafe class GpuProcessUsageReader : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private readonly object _lock = new();
    private nint _query;
    private nint _counter;
    private readonly string? _initError;
    private bool _disposed;

    public GpuProcessUsageReader()
    {
        try
        {
            uint s = PdhOpenQuery(null, 0, out _query);
            if (s != 0) { _initError = $"PdhOpenQuery falhou (0x{s:X8})."; return; }
            s = PdhAddEnglishCounter(_query, CounterPath, 0, out _counter);
            if (s != 0) { _initError = $"Contador \"GPU Engine\" indisponível neste sistema/driver (0x{s:X8})."; Close(); return; }
            // Base para o contador de taxa (falha aqui = nenhuma instância ainda, o que é aceitável).
            _ = PdhCollectQueryData(_query);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _initError = "PDH indisponível: " + ex.Message;
        }
    }

    public bool IsAvailable => _initError is null && _query != 0;
    public string? InitializationError => _initError;

    /// <summary>Coleta e devolve o uso por PID desde a leitura anterior.</summary>
    public GpuUsageSnapshot Read()
    {
        lock (_lock)
        {
            if (_disposed) return GpuUsageSnapshot.Unavailable("Leitor descartado.");
            if (!IsAvailable) return GpuUsageSnapshot.Unavailable(_initError ?? "Indisponível.");
            uint s = PdhCollectQueryData(_query);
            if (s != 0) return GpuUsageSnapshot.Unavailable($"Falha na coleta PDH (0x{s:X8}).");

            uint size = 0;
            s = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, null);
            if (s != PDH_MORE_DATA)
            {
                // Nenhuma instância (nenhum processo usando a GPU) é um resultado válido.
                return s == 0 || count == 0
                    ? new GpuUsageSnapshot(true, null, DateTimeOffset.Now, new Dictionary<int, ProcessGpuUsage>())
                    : GpuUsageSnapshot.Unavailable($"Falha ao ler instâncias (0x{s:X8}).");
            }
            var buffer = new byte[size];
            var items = new List<(string, double)>();
            fixed (byte* p = buffer)
            {
                s = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, p);
                if (s != 0) return GpuUsageSnapshot.Unavailable($"Falha ao ler instâncias (0x{s:X8}).");
                var arr = (PDH_FMT_COUNTERVALUE_ITEM_DOUBLE*)p;
                for (int i = 0; i < count; i++)
                {
                    if (arr[i].CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                    var name = Marshal.PtrToStringUni(arr[i].szName);
                    if (name is not null) items.Add((name, arr[i].Value));
                }
            }
            return new GpuUsageSnapshot(true, null, DateTimeOffset.Now, Aggregate(items));
        }
    }

    /// <summary>Extrai PID e tipo de engine do nome da instância. Puro/testável.</summary>
    public static bool TryParseInstance(string instance, out int pid, out string engineType)
    {
        pid = 0; engineType = "";
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        int end = instance.IndexOf('_', 4);
        if (end < 0 || !int.TryParse(instance.AsSpan(4, end - 4), out pid)) return false;
        int et = instance.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        engineType = et >= 0 ? instance[(et + 9)..] : "Desconhecido";
        if (engineType.Length == 0) engineType = "Desconhecido";
        return true;
    }

    /// <summary>Agrega valores por PID: soma total, maior engine e soma por tipo de engine (limitada a 100%).</summary>
    public static IReadOnlyDictionary<int, ProcessGpuUsage> Aggregate(IEnumerable<(string Instance, double Value)> items)
    {
        var acc = new Dictionary<int, (double sum, double max, Dictionary<string, double> byType)>();
        foreach (var (inst, v) in items)
        {
            if (!TryParseInstance(inst, out int pid, out string type) || !double.IsFinite(v) || v < 0) continue;
            if (!acc.TryGetValue(pid, out var a)) a = (0, 0, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
            a.sum += v;
            a.max = Math.Max(a.max, v);
            a.byType[type] = a.byType.GetValueOrDefault(type) + v;
            acc[pid] = a;
        }
        var result = new Dictionary<int, ProcessGpuUsage>(acc.Count);
        foreach (var (pid, a) in acc)
        {
            var byType = a.byType.ToDictionary(kv => kv.Key, kv => Math.Min(100, kv.Value), StringComparer.OrdinalIgnoreCase);
            result[pid] = new ProcessGpuUsage(pid, a.sum, Math.Min(100, a.max), byType);
        }
        return result;
    }

    private void Close()
    {
        if (_query != 0) { PdhCloseQuery(_query); _query = 0; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}
