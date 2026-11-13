using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rbm.Monitoring;

/// <summary>
/// Amostragem de telemetria em thread dedicada (padrão 1 s). Uma única consulta PDH com curingas
/// coleta CPU/memória/GPU/disco/rede; provedores de sensores complementam temperaturas e potência.
/// Overhead típico: poucos ms por amostra.
/// </summary>
public sealed partial class TelemetryService : ITelemetryService
{
    private readonly ILogger _log;
    private readonly ISystemInventory _inventory;
    private readonly IReadOnlyList<ISensorProvider> _sensors;
    private readonly object _gate = new();
    private readonly LinkedList<TelemetrySnapshot> _history = new();
    private PdhQuery? _pdh;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _sensorsInitialized;

    public int HistoryCapacity { get; set; } = 600; // 10 min @ 1 s
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
    public TelemetrySnapshot? Latest { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };
    public event EventHandler<TelemetrySnapshot>? Sampled;
    public IReadOnlyList<string> UnavailableCounters => _pdh?.FailedCounters.ToList() ?? new List<string>();

    public IReadOnlyList<TelemetrySnapshot> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public TelemetryService(ISystemInventory inventory, IEnumerable<ISensorProvider> sensors, ILogger<TelemetryService>? log = null)
    {
        _inventory = inventory;
        _sensors = sensors.ToList();
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    private const string CpuUtilTotal = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuUtilAll = @"\Processor Information(*)\% Processor Utility";
    private const string CpuPerf = @"\Processor Information(_Total)\% Processor Performance";
    private const string CpuLimit = @"\Processor Information(_Total)\% Performance Limit";
    private const string CpuDpc = @"\Processor Information(_Total)\% DPC Time";
    private const string CpuIntr = @"\Processor Information(_Total)\% Interrupt Time";
    private const string CpuIntrSec = @"\Processor Information(_Total)\Interrupts/sec";
    private const string QueueLen = @"\System\Processor Queue Length";
    private const string CtxSw = @"\System\Context Switches/sec";
    private const string MemAvail = @"\Memory\Available Bytes";
    private const string MemCommit = @"\Memory\Committed Bytes";
    private const string MemCommitLimit = @"\Memory\Commit Limit";
    private const string MemCache = @"\Memory\Cache Bytes";
    private const string MemStandbyNormal = @"\Memory\Standby Cache Normal Priority Bytes";
    private const string MemStandbyReserve = @"\Memory\Standby Cache Reserve Bytes";
    private const string MemStandbyCore = @"\Memory\Standby Cache Core Bytes";
    private const string MemModified = @"\Memory\Modified Page List Bytes";
    private const string MemPageReads = @"\Memory\Page Reads/sec";
    private const string MemPagesInput = @"\Memory\Pages Input/sec";
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string GpuShared = @"\GPU Adapter Memory(*)\Shared Usage";
    private const string DiskIdle = @"\PhysicalDisk(*)\% Idle Time";
    private const string DiskRead = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    private const string DiskReadLat = @"\PhysicalDisk(*)\Avg. Disk sec/Read";
    private const string DiskWriteLat = @"\PhysicalDisk(*)\Avg. Disk sec/Write";
    private const string DiskQueue = @"\PhysicalDisk(*)\Current Disk Queue Length";
    private const string NetRx = @"\Network Interface(*)\Bytes Received/sec";
    private const string NetTx = @"\Network Interface(*)\Bytes Sent/sec";
    private const string NetRxErr = @"\Network Interface(*)\Packets Received Errors";
    private const string NetOutDisc = @"\Network Interface(*)\Packets Outbound Discarded";
    private const string NetRxDisc = @"\Network Interface(*)\Packets Received Discarded";
    private const string NetBw = @"\Network Interface(*)\Current Bandwidth";

    private static readonly string[] AllCounters =
    {
        CpuUtilTotal, CpuUtilAll, CpuPerf, CpuLimit, CpuDpc, CpuIntr, CpuIntrSec, QueueLen, CtxSw,
        MemAvail, MemCommit, MemCommitLimit, MemCache, MemStandbyNormal, MemStandbyReserve, MemStandbyCore, MemModified, MemPageReads, MemPagesInput,
        GpuEngine, GpuDedicated, GpuShared, DiskIdle, DiskRead, DiskWrite, DiskReadLat, DiskWriteLat, DiskQueue,
        NetRx, NetTx, NetRxErr, NetOutDisc, NetRxDisc, NetBw,
    };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Factory.StartNew(() => Loop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _loop = null;
    }

    private void Loop(CancellationToken ct)
    {
        try
        {
            _pdh = new PdhQuery();
            foreach (var c in AllCounters) _pdh.Add(c);
            _pdh.Collect(); // primeira coleta: contadores de taxa precisam de duas amostras
            if (_pdh.FailedCounters.Count > 0)
                _log.LogInformation("Contadores indisponíveis: {Counters}", string.Join(", ", _pdh.FailedCounters));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao iniciar PDH");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                if (ct.WaitHandle.WaitOne(Interval)) break;
                var profile = _inventory.Current;
                if (!_sensorsInitialized && profile is not null)
                {
                    foreach (var s in _sensors) { try { s.Initialize(profile); } catch (Exception ex) { _log.LogWarning(ex, "Sensor {S}", s.Name); } }
                    _sensorsInitialized = true;
                }
                var snap = Sample(profile);
                if (_sensorsInitialized)
                    foreach (var s in _sensors.Where(s => s.IsAvailable))
                        try { s.Enrich(snap); } catch (Exception ex) { _log.LogDebug(ex, "Sensor {S} falhou", s.Name); }

                lock (_gate)
                {
                    _history.AddLast(snap);
                    while (_history.Count > HistoryCapacity) _history.RemoveFirst();
                }
                Latest = snap;
                Sampled?.Invoke(this, snap);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Falha na amostragem de telemetria");
            }
        }
        _pdh?.Dispose();
        _pdh = null;
    }

    private TelemetrySnapshot Sample(SystemProfile? profile)
    {
        var p = _pdh!;
        p.Collect();
        var s = new TelemetrySnapshot();

        // ---- CPU ----
        s.CpuTotalPercent = Math.Clamp(p.Value(CpuUtilTotal) ?? 0, 0, 100);
        var perCore = p.Array(CpuUtilAll)
            .Where(kv => !kv.Key.Contains("_Total"))
            .Select(kv => (key: ParseCpuInstance(kv.Key), value: Math.Clamp(kv.Value, 0, 100)))
            .OrderBy(x => x.key.group).ThenBy(x => x.key.index)
            .Select(x => x.value).ToArray();
        s.CpuPerCorePercent = perCore;
        if (p.Value(CpuPerf) is { } perf && profile?.Cpu.BaseClockMHz > 0)
            s.CpuEffectiveClockMHz = Reading<double>.Of(profile.Cpu.BaseClockMHz * perf / 100.0, "PDH");
        if (p.Value(CpuLimit) is { } lim) s.CpuPerformanceLimitPercent = Reading<double>.Of(lim, "PDH");
        s.DpcTimePercent = p.Value(CpuDpc) ?? 0;
        s.InterruptTimePercent = p.Value(CpuIntr) ?? 0;
        s.InterruptsPerSec = p.Value(CpuIntrSec) ?? 0;
        s.ProcessorQueueLength = p.Value(QueueLen) ?? 0;
        s.ContextSwitchesPerSec = p.Value(CtxSw) ?? 0;

        // ---- Memória ----
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            s.MemTotalBytes = (long)ms.ullTotalPhys;
            s.MemAvailableBytes = (long)ms.ullAvailPhys;
        }
        s.CommitBytes = (long)(p.Value(MemCommit) ?? 0);
        s.CommitLimitBytes = (long)(p.Value(MemCommitLimit) ?? 0);
        s.CacheBytes = (long)(p.Value(MemCache) ?? 0);
        s.StandbyBytes = (long)((p.Value(MemStandbyNormal) ?? 0) + (p.Value(MemStandbyReserve) ?? 0) + (p.Value(MemStandbyCore) ?? 0));
        s.ModifiedBytes = (long)(p.Value(MemModified) ?? 0);
        s.HardFaultsPerSec = p.Value(MemPageReads) ?? 0;
        s.PagesInputPerSec = p.Value(MemPagesInput) ?? 0;

        // ---- GPU ----
        s.Gpus = SampleGpus(p, profile);

        // ---- Disco ----
        var idle = p.Array(DiskIdle); var rd = p.Array(DiskRead); var wr = p.Array(DiskWrite);
        var rl = p.Array(DiskReadLat); var wl = p.Array(DiskWriteLat); var q = p.Array(DiskQueue);
        foreach (var inst in idle.Keys.Where(k => k != "_Total").OrderBy(k => k))
        {
            s.Disks.Add(new DiskTelemetry
            {
                Instance = inst,
                ActivePercent = Math.Clamp(100 - idle[inst], 0, 100),
                ReadBytesPerSec = rd.GetValueOrDefault(inst), WriteBytesPerSec = wr.GetValueOrDefault(inst),
                AvgReadLatencyMs = rl.GetValueOrDefault(inst) * 1000, AvgWriteLatencyMs = wl.GetValueOrDefault(inst) * 1000,
                QueueLength = q.GetValueOrDefault(inst),
            });
        }

        // ---- Rede ----
        var rx = p.Array(NetRx); var tx = p.Array(NetTx); var re = p.Array(NetRxErr); var od = p.Array(NetOutDisc); var rdisc = p.Array(NetRxDisc); var bw = p.Array(NetBw);
        foreach (var inst in rx.Keys.OrderBy(k => k))
        {
            if (inst.Contains("Loopback", StringComparison.OrdinalIgnoreCase) || inst.Contains("isatap", StringComparison.OrdinalIgnoreCase)) continue;
            s.Networks.Add(new NetTelemetry
            {
                Name = inst, RxBytesPerSec = rx[inst], TxBytesPerSec = tx.GetValueOrDefault(inst),
                PacketsReceivedErrors = re.GetValueOrDefault(inst), PacketsOutboundDiscarded = od.GetValueOrDefault(inst),
                PacketsReceivedDiscarded = rdisc.GetValueOrDefault(inst), CurrentBandwidthBps = bw.GetValueOrDefault(inst),
            });
        }
        return s;
    }

    private static (int group, int index) ParseCpuInstance(string inst)
    {
        var parts = inst.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out var g) && int.TryParse(parts[1], out var i)) return (g, i);
        return (0, int.TryParse(inst, out var x) ? x : 0);
    }

    [GeneratedRegex(@"luid_0x([0-9A-Fa-f]+)_0x([0-9A-Fa-f]+)_phys_(\d+)(?:_eng_(\d+)_engtype_(\w+))?")]
    private static partial Regex GpuInstanceRegex();

    public static (long luid, int phys, int eng, string engType)? ParseGpuInstance(string inst)
    {
        var m = GpuInstanceRegex().Match(inst);
        if (!m.Success) return null;
        long hi = long.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
        long lo = long.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
        return ((hi << 32) | lo, int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : -1, m.Groups[5].Success ? m.Groups[5].Value : "");
    }

    private static List<GpuTelemetry> SampleGpus(PdhQuery p, SystemProfile? profile)
    {
        // Soma por motor físico (todas as instâncias pid_* do mesmo motor) — mesmo método do Gerenciador de Tarefas.
        var perEngine = new Dictionary<(long luid, int phys, int eng, string type), double>();
        foreach (var (inst, v) in p.Array(GpuEngine))
        {
            if (ParseGpuInstance(inst) is not { } key) continue;
            perEngine[key] = perEngine.GetValueOrDefault(key) + v;
        }
        var dedicated = p.Array(GpuDedicated);
        var shared = p.Array(GpuShared);

        var list = new List<GpuTelemetry>();
        var luids = perEngine.Keys.Select(k => k.luid).Concat(dedicated.Keys.Select(k => ParseGpuInstance(k)?.luid ?? 0)).Where(l => l != 0).Distinct();
        foreach (var luid in luids)
        {
            var info = profile?.Gpus.FirstOrDefault(g => g.AdapterLuid == luid);
            if (profile is not null && info is null) continue; // adaptador sem correspondência (ex.: render-only virtual)
            double MaxOf(string type) => perEngine.Where(kv => kv.Key.luid == luid && kv.Key.type.Equals(type, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            var g = new GpuTelemetry
            {
                Name = info?.Name ?? $"GPU {luid:X}",
                AdapterLuid = luid,
                Utilization3DPercent = Math.Min(100, MaxOf("3D")),
                VideoDecodePercent = Math.Min(100, Math.Max(MaxOf("VideoDecode"), MaxOf("Video Decode"))),
                VideoEncodePercent = Math.Min(100, Math.Max(MaxOf("VideoEncode"), MaxOf("Video Encode"))),
                CopyPercent = Math.Min(100, MaxOf("Copy")),
                ComputePercent = Math.Min(100, Math.Max(MaxOf("Compute_0"), MaxOf("Compute_1"))),
                UtilizationMaxEnginePercent = Math.Min(100, perEngine.Where(kv => kv.Key.luid == luid).Select(kv => kv.Value).DefaultIfEmpty(0).Max()),
                DedicatedUsedBytes = (long)dedicated.Where(kv => ParseGpuInstance(kv.Key)?.luid == luid).Sum(kv => kv.Value),
                SharedUsedBytes = (long)shared.Where(kv => ParseGpuInstance(kv.Key)?.luid == luid).Sum(kv => kv.Value),
                DedicatedTotalBytes = info?.DedicatedVideoMemoryBytes ?? 0,
            };
            list.Add(g);
        }
        return list;
    }

    public void Dispose()
    {
        Stop();
        foreach (var s in _sensors) { try { s.Dispose(); } catch { } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}
