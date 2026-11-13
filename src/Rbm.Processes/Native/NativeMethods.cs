using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rbm.Processes.Native;

/// <summary>Handle de processo (OpenProcess retorna NULL em falha).</summary>
internal sealed class ProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ProcessHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

/// <summary>Handle do Service Control Manager / de um serviço.</summary>
internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ServiceHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}

/// <summary>
/// Layout x64 de SYSTEM_PROCESS_INFORMATION (ntdll). Offsets naturais (Sequential) — validados por teste
/// (tamanho 256, ReadTransferCount em 232).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_PROCESS_INFORMATION
{
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public ushort ImageNameLength;
    public ushort ImageNameMaximumLength;
    public nint ImageNameBuffer;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UNICODE_STRING
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS_PROCESS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
    public uint dwProcessId;
    public uint dwServiceFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_STATUS
{
    public uint dwServiceType;
    public uint dwCurrentState;
    public uint dwControlsAccepted;
    public uint dwWin32ExitCode;
    public uint dwServiceSpecificExitCode;
    public uint dwCheckPoint;
    public uint dwWaitHint;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ENUM_SERVICE_STATUS_PROCESS
{
    public nint lpServiceName;
    public nint lpDisplayName;
    public SERVICE_STATUS_PROCESS ServiceStatusProcess;
}

[StructLayout(LayoutKind.Sequential)]
internal struct QUERY_SERVICE_CONFIG
{
    public uint dwServiceType;
    public uint dwStartType;
    public uint dwErrorControl;
    public nint lpBinaryPathName;
    public nint lpLoadOrderGroup;
    public uint dwTagId;
    public nint lpDependencies;
    public nint lpServiceStartName;
    public nint lpDisplayName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_DELAYED_AUTO_START_INFO
{
    public int fDelayedAutostart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_TRIGGER_INFO
{
    public uint cTriggers;
    public nint pTriggers;
    public nint pReserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
{
    public nint szName;
    public uint CStatus;
    public uint Padding;
    public double Value;
}

internal static unsafe class NativeMethods
{
    // ---- ntdll ----
    internal const int SystemProcessInformationClass = 5;
    internal const int ProcessCommandLineInformation = 60;
    internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    internal const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint length, out uint returnLength);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(ProcessHandle processHandle, int processInformationClass, void* processInformation, uint length, out uint returnLength);

    // ---- kernel32: processos ----
    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SYNCHRONIZE = 0x00100000;

    internal const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern ProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(ProcessHandle process, uint flags, char* exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(ProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(ProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(ProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(ProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetPriorityClass(ProcessHandle process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessAffinityMask(ProcessHandle process, out nuint processAffinityMask, out nuint systemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessAffinityMask(nint process, out nuint processAffinityMask, out nuint systemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetProcessAffinityMask(ProcessHandle process, nuint processAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessIoCounters(ProcessHandle process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    internal static extern uint GetActiveProcessorCount(ushort groupNumber);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    // ---- advapi32: token ----
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenUser = 1;
    internal const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(ProcessHandle process, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, void* tokenInformation, uint tokenInformationLength, out uint returnLength);

    // ---- advapi32: SCM ----
    internal const uint SC_MANAGER_CONNECT = 0x0001;
    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_CHANGE_CONFIG = 0x0002;
    internal const uint SERVICE_QUERY_STATUS = 0x0004;
    internal const uint SERVICE_START = 0x0010;
    internal const uint SERVICE_STOP = 0x0020;
    internal const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    internal const uint SERVICE_CONTROL_STOP = 0x00000001;
    internal const int SC_ENUM_PROCESS_INFO = 0;
    internal const int SC_STATUS_PROCESS_INFO = 0;
    internal const uint SERVICE_TYPE_ALL = 0x000003FF;
    internal const uint SERVICE_STATE_ALL = 0x00000003;
    internal const uint SERVICE_CONFIG_DESCRIPTION = 1;
    internal const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    internal const uint SERVICE_CONFIG_TRIGGER_INFO = 8;
    internal const int ERROR_MORE_DATA = 234;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    internal static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    internal static extern ServiceHandle OpenService(ServiceHandle scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CloseServiceHandle(nint handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    internal static extern bool EnumServicesStatusEx(ServiceHandle scManager, int infoLevel, uint serviceType, uint serviceState,
        byte* services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    internal static extern bool QueryServiceConfig(ServiceHandle service, byte* serviceConfig, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    internal static extern bool QueryServiceConfig2(ServiceHandle service, uint infoLevel, byte* buffer, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool QueryServiceStatusEx(ServiceHandle service, int infoLevel, out SERVICE_STATUS_PROCESS buffer, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfigW")]
    internal static extern bool ChangeServiceConfig(ServiceHandle service, uint serviceType, uint startType, uint errorControl,
        string? binaryPathName, string? loadOrderGroup, nint tagId, string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    internal static extern bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "StartServiceW")]
    internal static extern bool StartService(ServiceHandle service, uint numArgs, nint args);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool ControlService(ServiceHandle service, uint control, out SERVICE_STATUS status);

    // ---- PDH ----
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_CSTATUS_VALID_DATA = 0;
    internal const uint PDH_CSTATUS_NEW_DATA = 1;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    internal static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    internal static extern uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* itemBuffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(nint query);

    // ---- shlwapi ----
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
    internal static extern int SHLoadIndirectString(string source, StringBuilder outBuf, uint cchOutBuf, nint reserved);

    internal static string? PtrToString(nint p) => p == 0 ? null : Marshal.PtrToStringUni(p);

    /// <summary>Lê uma lista MULTI_SZ (strings terminadas em NUL, finalizada por NUL duplo).</summary>
    internal static List<string> PtrToMultiString(nint p)
    {
        var list = new List<string>();
        if (p == 0) return list;
        char* c = (char*)p;
        while (*c != '\0')
        {
            var s = new string(c);
            list.Add(s);
            c += s.Length + 1;
        }
        return list;
    }
}
