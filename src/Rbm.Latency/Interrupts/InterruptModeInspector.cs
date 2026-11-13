using System.Management;
using System.Runtime.InteropServices;
using Rbm.Latency.Native;
using Microsoft.Win32;

namespace Rbm.Latency;

/// <summary>
/// Inspeção somente leitura do modo de interrupção (MSI × linha) de dispositivos PCI:
/// registro <c>HKLM\SYSTEM\CurrentControlSet\Enum\PCI\&lt;id&gt;\&lt;inst&gt;\Device Parameters\Interrupt Management</c>
/// (MessageSignaledInterruptProperties e Affinity Policy) + IRQs alocadas via WMI.
/// Não requer elevação. Não altera nada (a alteração de MSI é responsabilidade do motor de ajustes).
/// </summary>
public static class InterruptModeInspector
{
    private const string MsiSubKey = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
    private const string AffinitySubKey = @"Device Parameters\Interrupt Management\Affinity Policy";

    public static Task<InterruptModeReport> InspectAsync(bool includeBridges = false, bool includeNotPresent = false, CancellationToken ct = default) =>
        Task.Run(() => Inspect(includeBridges, includeNotPresent, ct), ct);

    public static InterruptModeReport Inspect(bool includeBridges = false, bool includeNotPresent = false, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var irqMap = ReadIrqMap(warnings);
        var devices = new List<PciInterruptInfo>();

        using var pciRoot = Registry.LocalMachine.OpenSubKey(DeviceTree.EnumRoot + @"\PCI");
        if (pciRoot is null)
        {
            warnings.Add("Não foi possível abrir HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI.");
            return new InterruptModeReport(devices, warnings, Notes());
        }

        foreach (var hwid in pciRoot.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            RegistryKey? hw = null;
            try { hw = pciRoot.OpenSubKey(hwid); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { continue; }
            if (hw is null) continue;
            using (hw)
            {
                foreach (var inst in hw.GetSubKeyNames())
                {
                    string instanceId = $@"PCI\{hwid}\{inst}";
                    try
                    {
                        var info = Describe(instanceId, hw, inst, irqMap);
                        if (info is null) continue;
                        if (!info.Present && !includeNotPresent) continue;
                        if (info.Category == PciDeviceCategory.Bridge && !includeBridges) continue;
                        devices.Add(info);
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        warnings.Add($"Sem acesso a {instanceId}: {ex.Message}");
                    }
                }
            }
        }

        return new InterruptModeReport(
            devices.OrderBy(d => d.Category == PciDeviceCategory.Other ? 99 : (int)d.Category).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            warnings, Notes());
    }

    private static IReadOnlyList<string> Notes() => new[]
    {
        "Somente leitura. MSI (Message Signaled Interrupts) evita compartilhamento de linhas de IRQ e costuma reduzir latência de interrupção; a maioria dos drivers modernos já o habilita.",
        "IRQs negativas (WMI/Gerenciador de Dispositivos) indicam MSI/MSI-X. Mudanças no registro só valem após reiniciar.",
        "Forçar MSI em dispositivo/driver sem suporte pode causar falha do dispositivo ou tela azul — por isso a alteração fica no motor de ajustes, com reversão.",
    };

    private static PciInterruptInfo? Describe(string instanceId, RegistryKey hw, string inst, Dictionary<string, List<long>> irqMap)
    {
        using var k = hw.OpenSubKey(inst);
        if (k is null) return null;
        bool present = DeviceTree.TryLocate(instanceId, out var devInst);
        // Para dispositivos presentes, o cfgmgr32 devolve nome/classe já resolvidos (sem "@inf,%x%;..." e com parâmetros de formatação aplicados).
        string name = (present ? DeviceTree.GetName(devInst) : null)
                      ?? DeviceTree.CleanDescription(k.GetValue("FriendlyName") as string ?? k.GetValue("DeviceDesc") as string);
        if (string.IsNullOrWhiteSpace(name)) name = instanceId;
        string? cls = (present ? DeviceTree.GetStringProperty(devInst, NativeMethods.CM_DRP_CLASS) : null)
                      ?? k.GetValue("Class") as string
                      ?? ClassFromGuid(k.GetValue("ClassGUID") as string);
        string? service = k.GetValue("Service") as string;

        int? msi = null, limit = null, policy = null, priority = null;
        ulong? mask = null;
        using (var mk = k.OpenSubKey(MsiSubKey))
        {
            if (mk is not null)
            {
                msi = SelectiveSuspendInterpreter.ToInt(mk.GetValue("MSISupported"));
                limit = SelectiveSuspendInterpreter.ToInt(mk.GetValue("MessageNumberLimit"));
            }
        }
        using (var ak = k.OpenSubKey(AffinitySubKey))
        {
            if (ak is not null)
            {
                policy = SelectiveSuspendInterpreter.ToInt(ak.GetValue("DevicePolicy"));
                priority = SelectiveSuspendInterpreter.ToInt(ak.GetValue("DevicePriority"));
                mask = InterruptPolicyInterpreter.ParseAffinityMask(ak.GetValue("AssignmentSetOverride"));
            }
        }

        IReadOnlyList<long> irqs = irqMap.TryGetValue(instanceId, out var l) ? l.OrderBy(x => x).ToList() : Array.Empty<long>();
        var mode = InterruptPolicyInterpreter.EffectiveMode(irqs);
        return new PciInterruptInfo(instanceId, name, Categorize(cls, service, name), cls, service, present,
            msi, limit, policy, InterruptPolicyInterpreter.DevicePolicyName(policy), priority, InterruptPolicyInterpreter.DevicePriorityName(priority), mask,
            irqs, mode, InterruptPolicyInterpreter.Interpret(msi, limit, mode, irqs, policy, mask, priority));
    }

    /// <summary>Nome de classe a partir de GUIDs de classe de instalação conhecidos.</summary>
    public static string? ClassFromGuid(string? classGuid)
    {
        if (!Guid.TryParse(classGuid, out var g)) return null;
        return g.ToString("D").ToLowerInvariant() switch
        {
            "4d36e968-e325-11ce-bfc1-08002be10318" => "Display",
            "4d36e972-e325-11ce-bfc1-08002be10318" => "Net",
            "4d36e97b-e325-11ce-bfc1-08002be10318" => "SCSIAdapter",
            "4d36e96a-e325-11ce-bfc1-08002be10318" => "HDC",
            "36fc9e60-c465-11cf-8056-444553540000" => "USB",
            "4d36e96c-e325-11ce-bfc1-08002be10318" => "MEDIA",
            "4d36e97d-e325-11ce-bfc1-08002be10318" => "System",
            _ => null,
        };
    }

    public static PciDeviceCategory Categorize(string? cls, string? service, string name)
    {
        bool C(string v) => string.Equals(cls, v, StringComparison.OrdinalIgnoreCase);
        bool S(params string[] v) => service is not null && v.Contains(service, StringComparer.OrdinalIgnoreCase);
        if (C("Display")) return PciDeviceCategory.Gpu;
        if (C("Net")) return PciDeviceCategory.Network;
        if (C("SCSIAdapter") || C("HDC") || S("stornvme", "storahci", "amd_sata", "iaStorAC", "iaStorVD", "rcraid")) return PciDeviceCategory.Storage;
        if (C("USB") || S("usbxhci", "usbehci", "usbohci", "usbuhci")) return PciDeviceCategory.UsbController;
        if (C("MEDIA") || S("HDAudBus")) return PciDeviceCategory.Audio;
        if (S("pci") || name.Contains("Root Port", StringComparison.OrdinalIgnoreCase) || name.Contains("Switch Port", StringComparison.OrdinalIgnoreCase)
            || name.Contains("bridge", StringComparison.OrdinalIgnoreCase) || name.Contains("ponte", StringComparison.OrdinalIgnoreCase))
            return PciDeviceCategory.Bridge;
        return PciDeviceCategory.Other;
    }

    private static Dictionary<string, List<long>> ReadIrqMap(List<string> warnings)
    {
        var refs = new List<(string, string)>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_PnPAllocatedResource");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    var a = mo["Antecedent"] as string;
                    var d = mo["Dependent"] as string;
                    if (a is not null && d is not null && a.Contains("Win32_IRQResource", StringComparison.OrdinalIgnoreCase)) refs.Add((a, d));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            warnings.Add("Falha ao ler IRQs via WMI (Win32_PnPAllocatedResource): " + ex.Message);
        }
        return InterruptPolicyInterpreter.ParseAllocatedResources(refs);
    }
}
