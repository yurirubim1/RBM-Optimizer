using System.Runtime.InteropServices;
using Rbm.Core;
using Rbm.Core.Tweaks;

namespace Rbm.Optimization.Providers;

/// <summary>Configurações de energia via PowrProf (mesma API do powercfg, sem parsing de texto).</summary>
public sealed class PowerSettingProvider : ISettingProvider
{
    public bool CanHandle(SettingTarget target) => target is PowerSettingTarget;

    public SettingTarget Resolve(SettingTarget target)
    {
        var t = (PowerSettingTarget)target;
        if (!string.Equals(t.Scheme, "active", StringComparison.OrdinalIgnoreCase)) return t;
        return new PowerSettingTarget { Scheme = PowerApi.GetActiveScheme().ToString(), Subgroup = t.Subgroup, Setting = t.Setting, Ac = t.Ac, Dc = t.Dc, FriendlyName = t.FriendlyName };
    }

    public SettingValue Read(SettingTarget target)
    {
        var t = (PowerSettingTarget)Resolve(target);
        var scheme = Guid.Parse(t.Scheme);
        var ac = PowerApi.ReadAc(scheme, t.Subgroup, t.Setting);
        var dc = PowerApi.ReadDc(scheme, t.Subgroup, t.Setting);
        if (ac is null && dc is null) return SettingValue.Absent; // configuração inexistente nesta máquina
        return new SettingValue(true, "power", PowerValue.Format(ac, dc));
    }

    public void Write(SettingTarget target, SettingValue value)
    {
        var t = (PowerSettingTarget)Resolve(target);
        if (!value.Exists || value.Data is null) return; // não há como "remover" um índice; nada a fazer
        var scheme = Guid.Parse(t.Scheme);
        if (!PowerApi.SchemeExists(scheme)) throw new InvalidOperationException($"Esquema de energia {scheme} não existe.");
        var (ac, dc) = PowerValue.Parse(value.Data);
        if (ac is { } a) PowerApi.WriteAc(scheme, t.Subgroup, t.Setting, a);
        if (dc is { } d) PowerApi.WriteDc(scheme, t.Subgroup, t.Setting, d);
        // Reaplica o esquema para que a mudança entre em vigor imediatamente.
        if (PowerApi.GetActiveScheme() == scheme) PowerApi.SetActive(scheme);
    }
}

public static class PowerApi
{
    public static Guid GetActiveScheme()
    {
        uint r = PowerGetActiveScheme(IntPtr.Zero, out IntPtr p);
        if (r != 0) throw new System.ComponentModel.Win32Exception((int)r);
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static uint? ReadAc(Guid scheme, Guid sub, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == 0 ? v : null;

    public static uint? ReadDc(Guid scheme, Guid sub, Guid setting) =>
        PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == 0 ? v : null;

    public static void WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
    {
        uint r = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (r != 0) throw new System.ComponentModel.Win32Exception((int)r, $"PowerWriteACValueIndex falhou ({r}).");
    }

    public static void WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
    {
        uint r = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (r != 0) throw new System.ComponentModel.Win32Exception((int)r, $"PowerWriteDCValueIndex falhou ({r}).");
    }

    public static void SetActive(Guid scheme)
    {
        uint r = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (r != 0) throw new System.ComponentModel.Win32Exception((int)r);
    }

    public static bool SchemeExists(Guid scheme) => ListSchemes().Any(s => s.Guid == scheme);

    public static string FriendlyName(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return scheme.ToString();
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0
                ? Marshal.PtrToStringUni(buf) ?? scheme.ToString()
                : scheme.ToString();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static IReadOnlyList<(Guid Guid, string Name)> ListSchemes()
    {
        var list = new List<(Guid, string)>();
        uint index = 0;
        var buffer = new byte[16];
        while (true)
        {
            uint size = 16;
            uint r = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16 /* ACCESS_SCHEME */, index, buffer, ref size);
            if (r != 0) break;
            var g = new Guid(buffer);
            list.Add((g, FriendlyName(g)));
            index++;
        }
        return list;
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid, IntPtr Buffer, ref uint BufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, byte[] Buffer, ref uint BufferSize);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
}
