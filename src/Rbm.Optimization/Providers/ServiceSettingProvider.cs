using System.ComponentModel;
using System.Runtime.InteropServices;
using Rbm.Core;
using Rbm.Core.Tweaks;
using Rbm.Security;
using Microsoft.Win32;

namespace Rbm.Optimization.Providers;

/// <summary>Tipo de inicialização de serviços (leitura via Registro; escrita via SCM — efeito imediato).</summary>
public sealed class ServiceSettingProvider : ISettingProvider
{
    public bool CanHandle(SettingTarget target) => target is ServiceTarget;

    public SettingValue Read(SettingTarget target)
    {
        var t = (ServiceTarget)target;
        var mode = ServiceConfig.ReadStartMode(t.ServiceName);
        return mode is null ? SettingValue.Absent : new SettingValue(true, "start", mode.Value.ToString());
    }

    public void Write(SettingTarget target, SettingValue value)
    {
        var t = (ServiceTarget)target;
        if (!value.Exists) return; // serviço inexistente: nada a restaurar
        if (!Enum.TryParse<ServiceStartMode>(value.Data, true, out var mode)) throw new ArgumentException($"Modo de inicialização inválido: {value.Data}");
        ServiceConfig.SetStartMode(t.ServiceName, mode);
    }
}

public static class ServiceConfig
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '$' or ' ');

    public static ServiceStartMode? ReadStartMode(string serviceName)
    {
        if (!IsValidName(serviceName)) return null;
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{serviceName}");
        if (key?.GetValue("Start") is not int start) return null;
        bool delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
        return start switch
        {
            0 => ServiceStartMode.Boot,
            1 => ServiceStartMode.System,
            2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
            3 => ServiceStartMode.Manual,
            4 => ServiceStartMode.Disabled,
            _ => null,
        };
    }

    /// <summary>Requer elevação — executado via Rbm.Agent. Serviços protegidos são recusados.</summary>
    public static void SetStartMode(string serviceName, ServiceStartMode mode)
    {
        if (!IsValidName(serviceName)) throw new ArgumentException("Nome de serviço inválido.");
        if (mode is ServiceStartMode.Boot or ServiceStartMode.System) throw new ArgumentException("Modos Boot/System não são permitidos.");
        var current = ReadStartMode(serviceName);
        if (Protection.IsProtectedService(serviceName) && mode == ServiceStartMode.Disabled)
            throw new InvalidOperationException($"O serviço '{serviceName}' é protegido e não pode ser desabilitado pelo RBM.");
        if (current is null) throw new InvalidOperationException($"Serviço '{serviceName}' não encontrado.");

        IntPtr scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr svc = OpenService(scm, serviceName, 0x0002 /* SERVICE_CHANGE_CONFIG */ | 0x0001 /* QUERY_CONFIG */);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint startType = mode switch
                {
                    ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2,
                    ServiceStartMode.Manual => 3,
                    ServiceStartMode.Disabled => 4,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode)),
                };
                const uint NoChange = 0xFFFFFFFF;
                if (!ChangeServiceConfig(svc, NoChange, startType, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (startType == 2)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = mode == ServiceStartMode.AutomaticDelayed };
                    if (!ChangeServiceConfig2(svc, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO { [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? db, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint error, string? path, string? group, IntPtr tag, string? deps, string? user, string? pwd, string? display);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr svc, uint level, ref SERVICE_DELAYED_AUTO_START_INFO info);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr h);
}
