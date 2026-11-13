using System.Runtime.InteropServices;

namespace Rbm.Hardware;

/// <summary>Enumeração de adaptadores DXGI: VRAM dedicada real (64 bits) e LUID para casar contadores de GPU.</summary>
public static class Dxgi
{
    public sealed record AdapterDesc(string Description, uint VendorId, uint DeviceId, uint SubSysId, uint Revision,
        ulong DedicatedVideoMemory, ulong DedicatedSystemMemory, ulong SharedSystemMemory, long Luid, bool IsSoftware);

    public static List<AdapterDesc> EnumerateAdapters()
    {
        var list = new List<AdapterDesc>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryObj) != 0 || factoryObj is not IDXGIFactory1 factory) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = factory.EnumAdapters1(i, out var adapter);
                if (hr != 0 || adapter is null) break; // DXGI_ERROR_NOT_FOUND
                try
                {
                    if (adapter.GetDesc1(out var d) == 0)
                    {
                        long luid = ((long)d.AdapterLuidHigh << 32) | d.AdapterLuidLow;
                        list.Add(new AdapterDesc(d.Description.TrimEnd('\0'), d.VendorId, d.DeviceId, d.SubSysId, d.Revision,
                            (ulong)d.DedicatedVideoMemory, (ulong)d.DedicatedSystemMemory, (ulong)d.SharedSystemMemory, luid, (d.Flags & 2) != 0));
                    }
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return list;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        // IDXGIFactory
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
