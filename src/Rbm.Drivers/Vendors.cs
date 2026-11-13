using System.Text.RegularExpressions;

namespace Rbm.Drivers;

public enum HardwareBus { Unknown, Pci, Usb, HdAudio, Acpi, Hid, Bluetooth, Root, Software, Other }

/// <summary>Campos extraídos de um ID de hardware (ex.: PCI\VEN_10DE&amp;DEV_2484&amp;SUBSYS_...&amp;REV_A1).</summary>
public sealed record HardwareIdInfo(
    string Raw,
    HardwareBus Bus,
    string Enumerator,
    string? VendorId,
    string? DeviceId,
    string? SubsystemVendorId,
    string? SubsystemDeviceId,
    string? Revision,
    string? AcpiId)
{
    /// <summary>Fabricante do chip (tabela embutida). Null se o código não estiver na tabela.</summary>
    public string? VendorName => VendorDatabase.Lookup(Bus, VendorId, AcpiId);
    /// <summary>Fabricante da placa/sistema (subsystem PCI, ex.: MSI, ASUS). Null se ausente/não catalogado.</summary>
    public string? SubsystemVendorName => SubsystemVendorId is null ? null : VendorDatabase.LookupPci(SubsystemVendorId);

    /// <summary>Forma curta útil para buscas: "VEN_10DE&amp;DEV_2484" / "VID_046D&amp;PID_C52B".</summary>
    public string? ShortId => (Bus, VendorId, DeviceId) switch
    {
        (HardwareBus.Usb or HardwareBus.Hid or HardwareBus.Bluetooth, { } v, { } d) => $"VID_{v}&PID_{d}",
        (_, { } v, { } d) => $"VEN_{v}&DEV_{d}",
        _ => AcpiId,
    };
}

public static partial class HardwareIdParser
{
    [GeneratedRegex(@"(?:^|[\\&_])VEN_(?<v>[0-9A-F]{4})(?=&|\\|$)", RegexOptions.IgnoreCase)] private static partial Regex Ven();
    [GeneratedRegex(@"(?:^|[\\&])DEV_(?<v>[0-9A-F]{4})(?=&|\\|$)", RegexOptions.IgnoreCase)] private static partial Regex Dev();
    [GeneratedRegex(@"(?:^|[\\&_])VID_(?<v>[0-9A-F]{4})(?=&|\\|$)", RegexOptions.IgnoreCase)] private static partial Regex Vid();
    [GeneratedRegex(@"(?:^|[\\&])PID_(?<v>[0-9A-F]{4})(?=&|\\|$)", RegexOptions.IgnoreCase)] private static partial Regex Pid();
    [GeneratedRegex(@"SUBSYS_(?<d>[0-9A-F]{4})(?<v>[0-9A-F]{4})", RegexOptions.IgnoreCase)] private static partial Regex Subsys();
    [GeneratedRegex(@"(?:REV_)(?<v>[0-9A-F]{2,4})", RegexOptions.IgnoreCase)] private static partial Regex Rev();
    [GeneratedRegex(@"^ACPI\\(?<v>[A-Z0-9_]{3,8})", RegexOptions.IgnoreCase)] private static partial Regex Acpi();

    public static HardwareIdInfo Parse(string hardwareId)
    {
        var raw = hardwareId?.Trim() ?? "";
        int slash = raw.IndexOf('\\');
        string enumr = (slash > 0 ? raw[..slash] : raw).ToUpperInvariant();
        var bus = enumr switch
        {
            "PCI" => HardwareBus.Pci,
            "USB" or "USBSTOR" => HardwareBus.Usb,
            "HDAUDIO" => HardwareBus.HdAudio,
            "ACPI" => HardwareBus.Acpi,
            "HID" => HardwareBus.Hid,
            "BTH" or "BTHENUM" or "BTHLE" or "BTHLEDEVICE" => HardwareBus.Bluetooth,
            "ROOT" => HardwareBus.Root,
            "SWD" or "SW" => HardwareBus.Software,
            "" => HardwareBus.Unknown,
            _ => HardwareBus.Other,
        };

        string? G(Regex r) { var m = r.Match(raw); return m.Success ? m.Groups["v"].Value.ToUpperInvariant() : null; }
        string? vendor = G(Ven()) ?? G(Vid());
        string? device = G(Dev()) ?? G(Pid());
        string? subV = null, subD = null;
        var sm = Subsys().Match(raw);
        if (sm.Success)
        {
            // PCI: SUBSYS_ddddvvvv (dispositivo, depois fabricante). HD Audio: SUBSYS_vvvvdddd (fabricante primeiro).
            bool vendorFirst = bus == HardwareBus.HdAudio;
            subV = (vendorFirst ? sm.Groups["d"] : sm.Groups["v"]).Value.ToUpperInvariant();
            subD = (vendorFirst ? sm.Groups["v"] : sm.Groups["d"]).Value.ToUpperInvariant();
        }
        string? acpi = bus == HardwareBus.Acpi ? G(Acpi()) : null;
        return new HardwareIdInfo(raw, bus, enumr, vendor, device, subV, subD, G(Rev()), acpi);
    }
}

/// <summary>
/// Tabela COMPACTA de IDs de fabricante (PCI-SIG / USB-IF / ACPI). Cobre fabricantes comuns em PCs; não é exaustiva.
/// Quando um código não está aqui, a busca retorna null (nunca um palpite).
/// </summary>
public static class VendorDatabase
{
    private static readonly Dictionary<string, string> Pci = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8086"] = "Intel", ["1022"] = "AMD", ["1002"] = "AMD (ATI/Radeon)", ["10DE"] = "NVIDIA", ["10EC"] = "Realtek",
        ["168C"] = "Qualcomm Atheros", ["17CB"] = "Qualcomm", ["1969"] = "Qualcomm Atheros (Killer/Attansic)", ["14E4"] = "Broadcom",
        ["14C3"] = "MediaTek", ["11AB"] = "Marvell", ["1B4B"] = "Marvell", ["1D6A"] = "Marvell (Aquantia)", ["1B21"] = "ASMedia",
        ["1106"] = "VIA", ["144D"] = "Samsung", ["2646"] = "Kingston", ["1C5C"] = "SK hynix", ["15B7"] = "Western Digital (SanDisk)",
        ["1E0F"] = "KIOXIA", ["1179"] = "Toshiba", ["126F"] = "Silicon Motion", ["1987"] = "Phison", ["1CC1"] = "ADATA",
        ["1344"] = "Micron (Crucial)", ["C0A9"] = "Micron (Crucial)", ["1BB1"] = "Seagate", ["1912"] = "Renesas", ["104C"] = "Texas Instruments",
        ["1217"] = "O2 Micro", ["1180"] = "Ricoh", ["1414"] = "Microsoft", ["15AD"] = "VMware", ["80EE"] = "Oracle (VirtualBox)",
        ["1AF4"] = "Red Hat (VirtIO)", ["1B36"] = "Red Hat (QEMU)", ["1102"] = "Creative Labs", ["13F6"] = "C-Media", ["14F1"] = "Conexant (Synaptics)",
        ["111D"] = "IDT", ["1013"] = "Cirrus Logic", ["1000"] = "Broadcom (LSI)", ["9005"] = "Microchip (Adaptec)", ["1D17"] = "Zhaoxin",
        // Fabricantes de placas (aparecem como subsystem vendor)
        ["1043"] = "ASUS", ["1458"] = "Gigabyte", ["1462"] = "MSI", ["1849"] = "ASRock", ["1028"] = "Dell", ["103C"] = "HP",
        ["17AA"] = "Lenovo", ["1025"] = "Acer", ["1558"] = "Clevo", ["196E"] = "PNY", ["19DA"] = "Zotac", ["1682"] = "XFX",
        ["174B"] = "Sapphire (PC Partner)", ["1DA2"] = "Sapphire", ["148C"] = "PowerColor (TUL)", ["1569"] = "Palit", ["3842"] = "EVGA",
        ["10B0"] = "Gainward", ["1B4C"] = "Galax/KFA2",
    };

    private static readonly Dictionary<string, string> Usb = new(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = "Logitech", ["1532"] = "Razer", ["1B1C"] = "Corsair", ["1038"] = "SteelSeries", ["045E"] = "Microsoft",
        ["05AC"] = "Apple", ["0BDA"] = "Realtek", ["8087"] = "Intel", ["8086"] = "Intel", ["0CF3"] = "Qualcomm Atheros", ["0A5C"] = "Broadcom",
        ["0E8D"] = "MediaTek", ["13D3"] = "IMC Networks (AzureWave)", ["0489"] = "Foxconn / Hon Hai", ["04CA"] = "Lite-On",
        ["0B05"] = "ASUS", ["1462"] = "MSI", ["26CE"] = "ASRock", ["048D"] = "ITE Tech (controlador RGB/EC)", ["04F2"] = "Chicony",
        ["054C"] = "Sony", ["057E"] = "Nintendo", ["28DE"] = "Valve", ["2DC8"] = "8BitDo", ["0951"] = "Kingston", ["0781"] = "SanDisk",
        ["04E8"] = "Samsung", ["0930"] = "Toshiba", ["1058"] = "Western Digital", ["0BC2"] = "Seagate", ["174C"] = "ASMedia",
        ["2109"] = "VIA Labs", ["05E3"] = "Genesys Logic", ["1A40"] = "Terminus Technology", ["0424"] = "Microchip (SMSC)",
        ["10C4"] = "Silicon Labs", ["0403"] = "FTDI", ["1A86"] = "QinHeng (WCH)", ["067B"] = "Prolific", ["0D8C"] = "C-Media",
        ["1395"] = "Sennheiser / EPOS", ["1E7D"] = "ROCCAT", ["3434"] = "Keychron", ["258A"] = "Sino Wealth", ["1E71"] = "NZXT",
        ["0C45"] = "Microdia", ["2516"] = "Cooler Master", ["0738"] = "Mad Catz", ["0E6F"] = "PDP (Logic3)", ["03F0"] = "HP",
        ["413C"] = "Dell", ["17EF"] = "Lenovo", ["04A9"] = "Canon", ["04B8"] = "Epson", ["04F9"] = "Brother", ["2357"] = "TP-Link",
        ["0846"] = "NETGEAR", ["2001"] = "D-Link", ["0B95"] = "ASIX", ["1050"] = "Yubico", ["18D1"] = "Google", ["2717"] = "Xiaomi",
        ["22B8"] = "Motorola", ["1004"] = "LG", ["04D9"] = "Holtek", ["0FD9"] = "Elgato", ["1FC9"] = "NXP", ["0483"] = "STMicroelectronics",
        ["2341"] = "Arduino", ["1915"] = "Nordic Semiconductor", ["046A"] = "Cherry", ["093A"] = "PixArt", ["0E0F"] = "VMware",
        ["80EE"] = "Oracle (VirtualBox)", ["2833"] = "Meta (Oculus)", ["0BB4"] = "HTC", ["3842"] = "EVGA", ["1022"] = "AMD", ["1002"] = "AMD",
        ["10DE"] = "NVIDIA", ["1D6B"] = "Linux Foundation (hub raiz virtual)", ["0B0E"] = "Jabra (GN Netcom)", ["047F"] = "Plantronics (Poly)",
    };

    /// <summary>Prefixos de ID ACPI (PNP ID registry da UEFI Forum) mais comuns.</summary>
    private static readonly (string Prefix, string Name)[] AcpiPrefixes =
    {
        ("AMDI", "AMD"), ("AMD", "AMD"), ("INTC", "Intel"), ("INT", "Intel"), ("MSFT", "Microsoft"), ("NVDA", "NVIDIA"),
        ("QCOM", "Qualcomm"), ("SYNA", "Synaptics"), ("ELAN", "ELAN"), ("RTK", "Realtek"), ("10EC", "Realtek"), ("ASUS", "ASUS"),
        ("ATK", "ASUS"), ("MSI", "MSI"), ("DELL", "Dell"), ("HPQ", "HP"), ("LEN", "Lenovo"), ("IFX", "Infineon"), ("NTC", "Nuvoton"),
        ("PNP", "Dispositivo padrão ACPI/PnP (Microsoft)"), ("ACPI", "Dispositivo padrão ACPI"), ("FTDI", "FTDI"), ("GOOG", "Google"),
    };

    public static string? LookupPci(string? id) => id is not null && Pci.TryGetValue(id, out var n) ? n : null;
    public static string? LookupUsb(string? id) => id is not null && Usb.TryGetValue(id, out var n) ? n : null;

    public static string? LookupAcpi(string? acpiId)
    {
        if (string.IsNullOrEmpty(acpiId)) return null;
        foreach (var (p, n) in AcpiPrefixes)
            if (acpiId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    public static string? Lookup(HardwareBus bus, string? vendorId, string? acpiId) => bus switch
    {
        HardwareBus.Pci or HardwareBus.HdAudio => LookupPci(vendorId),
        HardwareBus.Usb or HardwareBus.Hid or HardwareBus.Bluetooth => LookupUsb(vendorId) ?? LookupPci(vendorId),
        HardwareBus.Acpi => LookupAcpi(acpiId),
        _ => LookupPci(vendorId) ?? LookupUsb(vendorId),
    };

    public static int PciCount => Pci.Count;
    public static int UsbCount => Usb.Count;
}
