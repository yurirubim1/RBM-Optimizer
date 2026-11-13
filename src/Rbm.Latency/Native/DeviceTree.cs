using System.Text;
using Microsoft.Win32;

namespace Rbm.Latency.Native;

/// <summary>Navegação somente leitura na árvore de dispositivos (cfgmgr32) e no registro Enum. Não requer elevação.</summary>
internal static class DeviceTree
{
    internal const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>Localiza o devnode de uma instância presente (false se ausente/fantasma).</summary>
    public static bool TryLocate(string instanceId, out uint devInst) =>
        NativeMethods.CM_Locate_DevNodeW(out devInst, instanceId, NativeMethods.CM_LOCATE_DEVNODE_NORMAL) == NativeMethods.CR_SUCCESS;

    public static string? GetId(uint devInst)
    {
        var buf = new char[512];
        return NativeMethods.CM_Get_Device_IDW(devInst, buf, (uint)buf.Length, 0) == NativeMethods.CR_SUCCESS
            ? new string(buf).TrimEnd('\0') : null;
    }

    public static uint? GetParent(uint devInst) =>
        NativeMethods.CM_Get_Parent(out var p, devInst, 0) == NativeMethods.CR_SUCCESS ? p : null;

    public static string? GetStringProperty(uint devInst, uint property)
    {
        uint len = 0;
        NativeMethods.CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, null, ref len, 0);
        if (len == 0 || len > 64 * 1024) return null;
        var buf = new byte[len];
        if (NativeMethods.CM_Get_DevNode_Registry_PropertyW(devInst, property, out _, buf, ref len, 0) != NativeMethods.CR_SUCCESS) return null;
        var s = Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    /// <summary>Nome amigável (FriendlyName ou DeviceDesc).</summary>
    public static string? GetName(uint devInst) =>
        GetStringProperty(devInst, NativeMethods.CM_DRP_FRIENDLYNAME) ?? GetStringProperty(devInst, NativeMethods.CM_DRP_DEVICEDESC);

    /// <summary>Ancestrais (pai, avô, ...) até a raiz, sem incluir o próprio dispositivo.</summary>
    public static List<(string Id, uint DevInst)> GetAncestors(uint devInst, int maxDepth = 16)
    {
        var list = new List<(string, uint)>();
        uint cur = devInst;
        for (int i = 0; i < maxDepth; i++)
        {
            if (GetParent(cur) is not { } p) break;
            var id = GetId(p);
            if (id is null || id.StartsWith(@"HTREE\ROOT", StringComparison.OrdinalIgnoreCase)) break;
            list.Add((id, p));
            cur = p;
        }
        return list;
    }

    /// <summary>Abre (somente leitura) uma subchave da instância em HKLM\SYSTEM\CurrentControlSet\Enum.</summary>
    public static RegistryKey? OpenInstanceKey(string instanceId, string? subKey = null)
    {
        try
        {
            var path = EnumRoot + "\\" + instanceId + (string.IsNullOrEmpty(subKey) ? "" : "\\" + subKey);
            return Registry.LocalMachine.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Remove o prefixo de recurso de INF ("@oem10.inf,%x%;Nome") dos textos de DeviceDesc.</summary>
    public static string CleanDescription(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return "";
        int semi = desc.LastIndexOf(';');
        return (desc.StartsWith('@') && semi >= 0 ? desc[(semi + 1)..] : desc).Trim();
    }
}
