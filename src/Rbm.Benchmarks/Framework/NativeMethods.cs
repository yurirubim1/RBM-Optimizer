using System.Runtime.InteropServices;

namespace Rbm.Benchmarks.Framework;

internal static unsafe partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint mem);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadFriendlyName(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupGuid,
        Guid* powerSettingGuid, byte* buffer, ref uint bufferSize);

    /// <summary>Valores em unidades de 100 ns. "Maximum" = resolução mais grossa; "Minimum" = mais fina.</summary>
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryTimerResolution(out uint maximumResolution, out uint minimumResolution, out uint currentResolution);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    internal static partial uint timeEndPeriod(uint period);

    internal static ulong? AvailablePhysicalMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        return GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys : null;
    }
}
