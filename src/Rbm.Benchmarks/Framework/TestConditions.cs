using System.Diagnostics;
using System.Reflection;
using Rbm.Benchmarks.Framework;
using Microsoft.Win32;

namespace Rbm.Benchmarks;

/// <summary>Condições do sistema capturadas imediatamente antes de um benchmark. Campos null = indisponível.</summary>
public sealed record TestConditions
{
    public DateTimeOffset CapturedAt { get; init; }
    public string? PowerPlanName { get; init; }
    public Guid? PowerPlanGuid { get; init; }
    /// <summary>True = tomada; false = bateria; null = desconhecido.</summary>
    public bool? OnAcPower { get; init; }
    public bool? HasBattery { get; init; }
    public int? BatteryPercent { get; init; }
    /// <summary>Uso médio de CPU do sistema (%) na janela antes do início (GetSystemTimes).</summary>
    public double? BackgroundCpuPercent { get; init; }
    public double SampleWindowSeconds { get; init; }
    /// <summary>Processos (outros) usando &gt; 5% da CPU total na janela. Null se não medido.</summary>
    public int? HighCpuProcessCount { get; init; }
    /// <summary>Nomes e uso (%) dos processos acima de 5%.</summary>
    public IReadOnlyList<string> HighCpuProcesses { get; init; } = Array.Empty<string>();
    public string ProcessPriority { get; init; } = "";
    public string WindowsVersion { get; init; } = "";
    public int WindowsBuild { get; init; }
    public int? WindowsUbr { get; init; }
    /// <summary>Resolução atual do timer do sistema (ms), via NtQueryTimerResolution.</summary>
    public double? TimerResolutionCurrentMs { get; init; }
    public double? TimerResolutionFinestMs { get; init; }
    public double? TimerResolutionCoarsestMs { get; init; }
    public string? CpuName { get; init; }
    public int LogicalProcessors { get; init; }
    public bool IsElevated { get; init; }
    public string RuntimeVersion { get; init; } = "";
    /// <summary>False se o assembly de benchmarks foi compilado sem otimizações (Debug) — resultados não representativos.</summary>
    public bool JitOptimized { get; init; } = true;
    public bool DebuggerAttached { get; init; }
}

/// <summary>Captura de <see cref="TestConditions"/> (P/Invoke, sem WMI, baixo custo).</summary>
public static class ConditionsProbe
{
    public const double BackgroundLoadThresholdPercent = 10.0;
    public const double HighCpuProcessThresholdPercent = 5.0;

    /// <summary>
    /// Mede a carga de fundo durante <paramref name="window"/> (o processo atual fica ocioso nesse período) e
    /// lê os demais dados. Cancelável (lança <see cref="OperationCanceledException"/>).
    /// </summary>
    public static TestConditions Capture(TimeSpan window, CancellationToken ct = default)
    {
        if (window < TimeSpan.Zero) window = TimeSpan.Zero;
        int selfPid = Environment.ProcessId;

        Dictionary<int, (string Name, TimeSpan Cpu)>? before = window > TimeSpan.Zero ? SnapshotProcessTimes(selfPid) : null;
        bool sysOk = NativeMethods.GetSystemTimes(out long idle0, out long k0, out long u0);
        var sw = Stopwatch.StartNew();
        if (window > TimeSpan.Zero && ct.WaitHandle.WaitOne(window))
            ct.ThrowIfCancellationRequested();
        double elapsed = sw.Elapsed.TotalSeconds;

        double? bg = null;
        if (window > TimeSpan.Zero && sysOk && NativeMethods.GetSystemTimes(out long idle1, out long k1, out long u1))
        {
            // kernelTime inclui o tempo ocioso.
            double total = (k1 - k0) + (u1 - u0);
            double idle = idle1 - idle0;
            if (total > 0) bg = Math.Clamp((total - idle) / total * 100.0, 0, 100);
        }

        int? highCount = null;
        var high = new List<string>();
        if (before is not null && elapsed > 0)
        {
            var after = SnapshotProcessTimes(selfPid);
            double capacity = elapsed * Environment.ProcessorCount;
            foreach (var (pid, a) in after)
            {
                if (!before.TryGetValue(pid, out var b) || b.Name != a.Name) continue;
                double pct = (a.Cpu - b.Cpu).TotalSeconds / capacity * 100.0;
                if (pct > HighCpuProcessThresholdPercent) high.Add($"{a.Name} ({pct:0.0}%)");
            }
            highCount = high.Count;
        }

        var (planGuid, planName) = ReadPowerPlan();
        bool? ac = null, hasBattery = null; int? batt = null;
        if (NativeMethods.GetSystemPowerStatus(out var ps))
        {
            ac = ps.ACLineStatus switch { 1 => true, 0 => false, _ => null };
            hasBattery = ps.BatteryFlag == 255 ? null : (ps.BatteryFlag & 128) == 0;
            if (hasBattery == true && ps.BatteryLifePercent <= 100) batt = ps.BatteryLifePercent;
        }

        var (cur, finest, coarsest) = ReadTimerResolution();

        string priority;
        try { using var p = Process.GetCurrentProcess(); priority = p.PriorityClass.ToString(); }
        catch (Exception) { priority = "Desconhecida"; }

        var (build, ubr, version) = ReadWindowsVersion();

        return new TestConditions
        {
            CapturedAt = DateTimeOffset.Now,
            PowerPlanGuid = planGuid,
            PowerPlanName = planName,
            OnAcPower = ac,
            HasBattery = hasBattery,
            BatteryPercent = batt,
            BackgroundCpuPercent = bg is null ? null : Math.Round(bg.Value, 2),
            SampleWindowSeconds = Math.Round(elapsed, 3),
            HighCpuProcessCount = highCount,
            HighCpuProcesses = high,
            ProcessPriority = priority,
            WindowsBuild = build,
            WindowsUbr = ubr,
            WindowsVersion = version,
            TimerResolutionCurrentMs = cur,
            TimerResolutionFinestMs = finest,
            TimerResolutionCoarsestMs = coarsest,
            CpuName = ReadCpuName(),
            LogicalProcessors = Environment.ProcessorCount,
            IsElevated = Environment.IsPrivilegedProcess,
            RuntimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            JitOptimized = IsJitOptimized(),
            DebuggerAttached = Debugger.IsAttached,
        };
    }

    /// <summary>Avisos pt-BR derivados das condições (não incluem a verificação de CV).</summary>
    public static IEnumerable<string> Assess(TestConditions c)
    {
        if (c.BackgroundCpuPercent > BackgroundLoadThresholdPercent)
            yield return $"Condições instáveis: carga de CPU de fundo de {c.BackgroundCpuPercent:0.0}% antes do início (limite {BackgroundLoadThresholdPercent:0}%).";
        if (c.HighCpuProcessCount > 0)
            yield return $"Processos usando mais de {HighCpuProcessThresholdPercent:0}% da CPU antes do início: {string.Join(", ", c.HighCpuProcesses)}.";
        if (c.OnAcPower == false)
            yield return "Executado na bateria: o desempenho costuma ser limitado. Compare apenas com execuções também na bateria.";
        if (!string.Equals(c.ProcessPriority, "Normal", StringComparison.OrdinalIgnoreCase))
            yield return $"Prioridade do processo é '{c.ProcessPriority}' (esperado: Normal).";
        if (!c.JitOptimized)
            yield return "Assembly compilado sem otimizações (Debug): resultados NÃO representativos.";
        if (c.DebuggerAttached)
            yield return "Depurador anexado: resultados NÃO representativos.";
    }

    public static bool IsBackgroundLoadHigh(TestConditions? c) => c?.BackgroundCpuPercent > BackgroundLoadThresholdPercent;

    /// <summary>Resolução do timer do sistema em ms: (atual, mais fina, mais grossa). Null se indisponível.</summary>
    public static (double? CurrentMs, double? FinestMs, double? CoarsestMs) ReadTimerResolution()
    {
        try
        {
            if (NativeMethods.NtQueryTimerResolution(out uint max, out uint min, out uint c) == 0)
                return (c / 10_000.0, min / 10_000.0, max / 10_000.0);
        }
        catch (Exception) { /* ntdll indisponível */ }
        return (null, null, null);
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> SnapshotProcessTimes(int selfPid)
    {
        var map = new Dictionary<int, (string Name, TimeSpan Cpu)>();
        Process[] procs;
        try { procs = Process.GetProcesses(); } catch (Exception) { return map; }
        foreach (var p in procs)
        {
            try
            {
                if (p.Id != selfPid && p.Id != 0) map[p.Id] = (p.ProcessName, p.TotalProcessorTime);
            }
            catch (Exception) { /* processo protegido ou encerrado: ignorado */ }
            finally { p.Dispose(); }
        }
        return map;
    }

    internal static unsafe (Guid?, string?) ReadPowerPlan()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(0, out nint ptr) != 0 || ptr == 0) return (null, null);
            Guid g;
            try { g = *(Guid*)ptr; }
            finally { NativeMethods.LocalFree(ptr); }

            uint size = 0;
            if (NativeMethods.PowerReadFriendlyName(0, &g, null, null, null, ref size) != 0 || size == 0) return (g, null);
            var buf = new byte[size];
            fixed (byte* b = buf)
            {
                if (NativeMethods.PowerReadFriendlyName(0, &g, null, null, b, ref size) != 0) return (g, null);
            }
            string name = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
            return (g, name);
        }
        catch (Exception) { return (null, null); }
    }

    private static (int Build, int? Ubr, string Version) ReadWindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        int? ubr = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("UBR") is int u) ubr = u;
        }
        catch (Exception) { }
        string version = ubr is null ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.{v.Build}.{ubr}";
        return (v.Build, ubr, version);
    }

    private static string? ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception) { return null; }
    }

    private static bool IsJitOptimized()
    {
        var attr = typeof(ConditionsProbe).Assembly.GetCustomAttribute<DebuggableAttribute>();
        return attr is null || !attr.IsJITOptimizerDisabled;
    }
}
