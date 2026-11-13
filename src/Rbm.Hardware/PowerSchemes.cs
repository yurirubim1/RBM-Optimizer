using System.Runtime.InteropServices;

namespace Rbm.Hardware;

/// <summary>Leitura mínima de esquemas de energia (o projeto Optimization tem a API completa de escrita).</summary>
public static class PowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static Guid Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(p); } finally { LocalFree(p); }
    }

    public static string Name(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return scheme.ToString();
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try { return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0 ? Marshal.PtrToStringUni(buf) ?? "" : scheme.ToString(); }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, IntPtr buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
}
