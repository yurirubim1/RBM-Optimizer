using System.Management;
using System.Runtime.InteropServices;
using Rbm.Latency.Native;

namespace Rbm.Latency;

/// <summary>
/// Enumeração somente leitura de dispositivos de entrada (mouses, teclados, controles) com a cadeia de pais
/// (coleção HID → interface/dispositivo USB → hub → controlador host) e configurações de suspensão seletiva.
/// Não requer elevação.
/// </summary>
public static class InputDeviceInspector
{
    /// <summary>Subgrupo "Configurações de USB" do plano de energia.</summary>
    public static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    /// <summary>Configuração "Suspensão seletiva de USB".</summary>
    public static readonly Guid UsbSelectiveSuspendSetting = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    private static readonly string[] HostControllerServices = { "usbxhci", "usbehci", "usbohci", "usbuhci" };

    public static Task<InputDevicesReport> InspectAsync(bool includeAllHid = false, CancellationToken ct = default) =>
        Task.Run(() => Inspect(includeAllHid, ct), ct);

    /// <param name="includeAllHid">Inclui também coleções HID genéricas (controle de consumidor, definidas pelo fornecedor etc.).</param>
    public static InputDevicesReport Inspect(bool includeAllHid = false, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var devices = new List<InputDeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPClass, Service, Present FROM Win32_PnPEntity " +
                "WHERE PNPClass='Mouse' OR PNPClass='Keyboard' OR PNPClass='HIDClass' OR PNPClass='XnaComposite' OR PNPClass='XboxComposite'");
            foreach (ManagementObject mo in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                using (mo)
                {
                    string id = mo["DeviceID"] as string ?? "";
                    if (id.Length == 0) continue;
                    string name = mo["Name"] as string ?? id;
                    string cls = mo["PNPClass"] as string ?? "";
                    string? service = mo["Service"] as string;
                    bool present = mo["Present"] is not bool b || b;

                    // Nós "USB\...", que hospedam o hidusb, aparecem na cadeia de pais; não são listados como dispositivo de entrada.
                    if (cls.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) && id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                    var category = Categorize(cls, name);
                    if (category == InputDeviceCategory.OtherHid && !includeAllHid) continue;

                    devices.Add(Describe(id, name, cls, service, present, category));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            warnings.Add("Falha ao consultar dispositivos via WMI: " + ex.Message);
        }

        var notes = new List<string>
        {
            "Somente leitura: nenhuma configuração é alterada aqui. Mudanças de energia/USB são feitas pelo motor de ajustes (com reversão).",
            "A suspensão seletiva age apenas com o dispositivo ocioso; durante o uso contínuo ela não interfere. O efeito possível é atraso no primeiro evento após ociosidade.",
            LatencyTexts.NotInputLag,
        };

        return new InputDevicesReport(
            devices.OrderBy(d => d.Category).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ReadGlobalSelectiveSuspend(),
            warnings,
            notes);
    }

    public static InputDeviceCategory Categorize(string pnpClass, string name)
    {
        if (pnpClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase)) return InputDeviceCategory.Mouse;
        if (pnpClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)) return InputDeviceCategory.Keyboard;
        if (pnpClass.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase) || pnpClass.Equals("XboxComposite", StringComparison.OrdinalIgnoreCase))
            return InputDeviceCategory.GameController;
        var n = name.ToLowerInvariant();
        if (n.Contains("game controller") || n.Contains("controlador de jogo") || n.Contains("gamepad") || n.Contains("joystick")
            || n.Contains("controle de jogo") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("wireless controller"))
            return InputDeviceCategory.GameController;
        return InputDeviceCategory.OtherHid;
    }

    private static InputDeviceInfo Describe(string id, string name, string cls, string? service, bool present, InputDeviceCategory category)
    {
        var chain = new List<DeviceChainNode>();
        string? host = null;
        string bus = BusFromId(id);
        if (DeviceTree.TryLocate(id, out var devInst))
        {
            foreach (var (pid, pinst) in DeviceTree.GetAncestors(devInst))
            {
                string pname = DeviceTree.GetName(pinst) ?? pid;
                string? psvc = DeviceTree.GetStringProperty(pinst, NativeMethods.CM_DRP_SERVICE);
                string? pcls = DeviceTree.GetStringProperty(pinst, NativeMethods.CM_DRP_CLASS);
                string role = Role(pid, psvc, pcls, pname);
                bool isUsbNode = pid.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !pid.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);
                chain.Add(new DeviceChainNode(pid, pname, psvc, pcls, role, isUsbNode ? ReadPower(pid) : null));
                if (pid.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) bus = "USB";
                else if (pid.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) bus = "Bluetooth";
                if (role == RoleHostController) { host = pid; break; }
            }
        }
        return new InputDeviceInfo(id, name, category, cls, service, bus, present, ReadPower(id), chain, host);
    }

    private const string RoleHostController = "Controlador host USB";

    internal static string Role(string id, string? service, string? cls, string name)
    {
        if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) return "Hub raiz USB";
        if (service is not null && HostControllerServices.Contains(service, StringComparer.OrdinalIgnoreCase)) return RoleHostController;
        if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && string.Equals(cls, "USB", StringComparison.OrdinalIgnoreCase)) return RoleHostController;
        if (service is not null && (service.Equals("usbhub", StringComparison.OrdinalIgnoreCase) || service.Equals("usbhub3", StringComparison.OrdinalIgnoreCase)))
            return id.Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? "Interface USB" : (name.Contains("hub", StringComparison.OrdinalIgnoreCase) ? "Hub USB" : "Dispositivo USB");
        if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
            return id.Contains("&MI_", StringComparison.OrdinalIgnoreCase) ? "Interface USB (dispositivo composto)" : "Dispositivo USB";
        if (id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) return "Coleção HID";
        if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) return "Dispositivo/ponte PCI";
        if (id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)) return "ACPI";
        return "Outro";
    }

    internal static string BusFromId(string id)
    {
        if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)) return "ACPI/PS2";
        if (id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return "Virtual/Software";
        int slash = id.IndexOf('\\');
        return slash > 0 ? id[..slash] : id;
    }

    /// <summary>Lê os valores de energia de <c>Device Parameters</c> (null se a chave não existe/não é legível).</summary>
    public static UsbPowerSettings? ReadPower(string instanceId)
    {
        using var k = DeviceTree.OpenInstanceKey(instanceId, "Device Parameters");
        if (k is null) return null;
        try
        {
            return SelectiveSuspendInterpreter.Interpret(
                SelectiveSuspendInterpreter.ToInt(k.GetValue("SelectiveSuspendEnabled")),
                SelectiveSuspendInterpreter.ToInt(k.GetValue("EnhancedPowerManagementEnabled")),
                SelectiveSuspendInterpreter.ToInt(k.GetValue("AllowIdleIrpInD3")),
                SelectiveSuspendInterpreter.ToInt(k.GetValue("DeviceSelectiveSuspended")),
                SelectiveSuspendInterpreter.ToInt(k.GetValue("SelectiveSuspendOn")));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Lê a configuração global "Suspensão seletiva de USB" do plano ativo (PowerReadACValueIndex/DCValueIndex). Sem elevação.</summary>
    public static UsbSelectiveSuspendGlobal ReadGlobalSelectiveSuspend()
    {
        IntPtr pScheme = IntPtr.Zero;
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out pScheme) != 0 || pScheme == IntPtr.Zero)
                return new UsbSelectiveSuspendGlobal(null, null, null, "Não foi possível ler o plano de energia ativo.");
            var scheme = Marshal.PtrToStructure<Guid>(pScheme);
            var sub = UsbSubgroup; var setting = UsbSelectiveSuspendSetting;
            int? ac = NativeMethods.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint a) == 0 ? (int)a : null;
            int? dc = NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint d) == 0 ? (int)d : null;
            return new UsbSelectiveSuspendGlobal(scheme, ac, dc, SelectiveSuspendInterpreter.InterpretGlobal(ac, dc));
        }
        finally
        {
            if (pScheme != IntPtr.Zero) NativeMethods.LocalFree(pScheme);
        }
    }
}
