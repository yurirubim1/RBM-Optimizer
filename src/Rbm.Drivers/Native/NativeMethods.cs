using System.Runtime.InteropServices;

namespace Rbm.Drivers.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;
    public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SP_INF_SIGNER_INFO_V2
{
    public uint cbSize;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DigitalSigner;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DigitalSignerVersion;
    public uint SignerScore;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SP_DEVINSTALL_PARAMS
{
    public uint cbSize;
    public uint Flags;
    public uint FlagsEx;
    public IntPtr hwndParent;
    public IntPtr InstallMsgHandler;
    public IntPtr InstallMsgHandlerContext;
    public IntPtr FileQueue;
    public UIntPtr ClassInstallReserved;
    public uint Reserved;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct SP_DRVINFO_DATA_V2
{
    public uint cbSize;
    public uint DriverType;
    public UIntPtr Reserved;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
    public long DriverDate; // FILETIME
    public ulong DriverVersion;
}

internal static class NativeMethods
{
    public static readonly IntPtr InvalidHandle = new(-1);

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_ALLCLASSES = 0x4;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_NOT_FOUND = 1168;
    public const uint CR_SUCCESS = 0;
    public const uint CR_NO_SUCH_DEVINST = 0x0D;

    public const uint DEVPROP_TYPE_UINT32 = 0x07;
    public const uint DEVPROP_TYPE_GUID = 0x0D;
    public const uint DEVPROP_TYPE_FILETIME = 0x10;
    public const uint DEVPROP_TYPE_BOOLEAN = 0x11;
    public const uint DEVPROP_TYPE_STRING = 0x12;
    public const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    public const uint SPDIT_COMPATDRIVER = 0x2;
    public const uint DI_ENUMSINGLEINF = 0x00010000;
    public const uint DI_FLAGSEX_ALLOWEXCLUDEDDRVS = 0x00000800;
    public const uint DIIDFLAG_NOFINISHINSTALLUI = 0x2;

    // ---- DEVPKEYs (devpkey.h) ----
    private const string DevBase = "a45c254e-df1c-4efd-8020-67d146a850e0";
    private const string DrvBase = "a8b865dd-2e3d-4094-ad97-e593a70c75d6";
    public static DEVPROPKEY DeviceDesc = new(DevBase, 2);
    public static DEVPROPKEY HardwareIds = new(DevBase, 3);
    public static DEVPROPKEY CompatibleIds = new(DevBase, 4);
    public static DEVPROPKEY Service = new(DevBase, 6);
    public static DEVPROPKEY Class = new(DevBase, 9);
    public static DEVPROPKEY ClassGuid = new(DevBase, 10);
    public static DEVPROPKEY Manufacturer = new(DevBase, 13);
    public static DEVPROPKEY FriendlyName = new(DevBase, 14);
    public static DEVPROPKEY LocationInfo = new(DevBase, 15);
    public static DEVPROPKEY EnumeratorName = new(DevBase, 24);
    public static DEVPROPKEY ProblemCode = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 3);
    public static DEVPROPKEY IsPresent = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 5);
    public static DEVPROPKEY LastArrivalDate = new("83da6326-97a6-4088-9453-a1923f573b29", 102);
    public static DEVPROPKEY DriverDate = new(DrvBase, 2);
    public static DEVPROPKEY DriverVersion = new(DrvBase, 3);
    public static DEVPROPKEY DriverDesc = new(DrvBase, 4);
    public static DEVPROPKEY DriverInfPath = new(DrvBase, 5);
    public static DEVPROPKEY DriverInfSection = new(DrvBase, 6);
    public static DEVPROPKEY MatchingDeviceId = new(DrvBase, 8);
    public static DEVPROPKEY DriverProvider = new(DrvBase, 9);
    public static DEVPROPKEY DriverRank = new(DrvBase, 14);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint propertyType,
        byte[]? buffer, uint bufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupVerifyInfFileW(string infName, IntPtr altPlatformInfo, ref SP_INF_SIGNER_INFO_V2 signerInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType, uint index, ref SP_DRVINFO_DATA_V2 driverInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, uint driverType);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DiInstallDevice(IntPtr hwndParent, IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA_V2 driverInfo,
        uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    // ---- Catálogos (wintrust) ----
    public const uint CRYPTCAT_OPEN_EXISTING = 0x00000002;

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CryptCATOpen(string fileName, uint openFlags, IntPtr hProv, uint publicVersion, uint encodingType);

    [DllImport("wintrust.dll", SetLastError = true)]
    public static extern IntPtr CryptCATEnumerateMember(IntPtr hCatalog, IntPtr prevMember);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptCATClose(IntPtr hCatalog);
}
