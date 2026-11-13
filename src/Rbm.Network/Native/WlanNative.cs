using System.Runtime.InteropServices;

namespace Rbm.Network.Native;

/// <summary>P/Invoke da Native Wifi API (wlanapi.dll). Somente leitura.</summary>
internal static class WlanNative
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorInvalidState = 5023;
    public const uint ErrorServiceNotActive = 1062;
    public const uint ErrorNotFound = 1168;

    public const int OpcodeChannelNumber = 8;
    public const int OpcodeCurrentConnection = 7;
    public const int OpcodeRssi = 0x10000102;

    [DllImport("wlanapi.dll")]
    public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int opCode, IntPtr pReserved,
        out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, int dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled, IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate; // kbps
        public uint ulTxRate; // kbps
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_SECURITY_ATTRIBUTES
    {
        public int bSecurityEnabled;
        public int bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_RATE_SET
    {
        public uint uRateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] usRateSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid;
        public uint uPhyId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
        public int dot11BssType;
        public int dot11BssPhyType;
        public int lRssi;
        public uint uLinkQuality;
        public byte bInRegDomain;
        public ushort usBeaconPeriod;
        public ulong ullTimestamp;
        public ulong ullHostTimestamp;
        public ushort usCapabilityInformation;
        public uint ulChCenterFrequency; // kHz
        public WLAN_RATE_SET wlanRateSet;
        public uint ulIeOffset;
        public uint ulIeSize;
    }

    /// <summary>Tamanhos nativos esperados (x64) — verificados em teste.</summary>
    public static int SizeOfConnectionAttributes => Marshal.SizeOf<WLAN_CONNECTION_ATTRIBUTES>();
    public static int SizeOfBssEntry => Marshal.SizeOf<WLAN_BSS_ENTRY>();
    public static int SizeOfInterfaceInfo => Marshal.SizeOf<WLAN_INTERFACE_INFO>();
}
