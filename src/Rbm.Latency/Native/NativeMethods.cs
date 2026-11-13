using System.Runtime.InteropServices;

namespace Rbm.Latency.Native;

/// <summary>P/Invokes usados pelo módulo de latência (somente APIs documentadas, exceto NtQueryTimerResolution — ver nota).</summary>
internal static class NativeMethods
{
    // ---------------- psapi: drivers carregados (reserva para o mapa de módulos) ----------------
    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool EnumDeviceDrivers([Out] IntPtr[]? lpImageBase, uint cb, out uint lpcbNeeded);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetDeviceDriverBaseNameW(IntPtr imageBase, [Out] char[] lpBaseName, uint nSize);

    // ---------------- ntdll: resolução do timer ----------------
    // NtQueryTimerResolution não é documentada no SDK, mas é estável desde o NT 4 e amplamente usada (ex.: ClockRes da Sysinternals).
    // Parâmetros (em unidades de 100 ns): MinimumResolution = mais GROSSA (ex.: 156250), MaximumResolution = mais FINA (ex.: 5000).
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

    // ---------------- PDH: contadores de desempenho ----------------
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_CSTATUS_VALID_DATA = 0x0;
    internal const uint PDH_CSTATUS_NEW_DATA = 0x1;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr hQuery);

    // ---------------- powrprof: plano de energia ----------------
    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ---------------- cfgmgr32: árvore de dispositivos ----------------
    internal const uint CR_SUCCESS = 0;
    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    internal const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
    internal const uint CM_DRP_DEVICEDESC = 0x00000001;
    internal const uint CM_DRP_SERVICE = 0x00000005;
    internal const uint CM_DRP_CLASS = 0x00000008;
    internal const uint CM_DRP_FRIENDLYNAME = 0x0000000D;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    internal static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Get_Device_IDW(uint dnDevInst, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, out uint pulRegDataType, [Out] byte[]? buffer, ref uint pulLength, uint ulFlags);

    // ---------------- user32: Raw Input ----------------
    internal const uint RIDEV_REMOVE = 0x00000001;
    internal const uint RIDEV_INPUTSINK = 0x00000100;
    internal const uint RID_INPUT = 0x10000003;
    internal const uint RID_HEADER = 0x10000005;
    internal const uint RIDI_DEVICENAME = 0x20000007;
    internal const uint RIM_TYPEMOUSE = 0;
    internal const uint RIM_TYPEKEYBOARD = 1;
    internal const uint RIM_TYPEHID = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, [Out] char[]? pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);
}
