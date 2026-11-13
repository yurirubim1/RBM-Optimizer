using System.Text.RegularExpressions;

namespace Rbm.Drivers;

public enum DriverCategory { Other, Gpu, Chipset, Network, WiFi, Audio, Bluetooth, Storage, Peripheral, Motherboard }

/// <summary>Página oficial de download/suporte (mapa estático; o RBM Optimizer não faz scraping nem baixa nada).</summary>
public sealed record OfficialSource(string Vendor, DriverCategory Category, string Url, string TitlePt, string? NotesPt = null);

/// <summary>Orientação de fonte oficial para um dispositivo: versão instalada (real) + links oficiais + observações.</summary>
public sealed record DriverSourceGuidance(
    string InstanceId,
    string DeviceName,
    DriverCategory Category,
    string? ChipVendor,
    string? BoardVendor,
    string? InstalledVersion,
    DateTime? InstalledDate,
    string? InstalledProvider,
    IReadOnlyList<OfficialSource> Sources,
    IReadOnlyList<string> NotesPt);

/// <summary>Busca no Catálogo do Microsoft Update (fonte oficial da Microsoft) para um ID de hardware.</summary>
public sealed record CatalogSearchLink(string Query, string Url);

/// <summary>Dispositivo desconhecido/com problema com dados para identificação.</summary>
public sealed record UnknownDeviceReport(
    DeviceRecord Device,
    string? ChipVendor,
    string? BoardVendor,
    string? ShortId,
    IReadOnlyList<CatalogSearchLink> CatalogSearches,
    IReadOnlyList<OfficialSource> Sources,
    string SummaryPt);

/// <summary>
/// Mapa ESTÁTICO de páginas oficiais por fabricante/categoria e geração de buscas no Catálogo do Microsoft Update.
/// Nunca aponta para sites agregadores de drivers. URLs revisadas em 11/2026 — revisar periodicamente.
/// </summary>
public static partial class OfficialSources
{
    public const string CatalogBase = "https://www.catalog.update.microsoft.com/Search.aspx?q=";

    public const string NewestNotAlwaysBestPt =
        "O driver mais novo nem sempre é o melhor: atualize para corrigir um problema concreto, obter suporte a um jogo/recurso " +
        "ou correção de segurança. Se o sistema está estável, manter a versão atual é uma escolha válida. Prefira versões " +
        "WHQL/estáveis às beta e guarde um backup (exportação) antes de trocar.";

    public const string OemFirstPt =
        "Em notebooks e para áudio/rede/chipset de placas-mãe, o pacote do fabricante do computador/placa-mãe costuma incluir " +
        "personalizações (ex.: efeitos de áudio, gerenciamento de energia). Use o driver genérico do fabricante do chip quando o do OEM estiver desatualizado " +
        "e houver um problema a resolver.";

    private static readonly OfficialSource[] All =
    {
        new("NVIDIA", DriverCategory.Gpu, "https://www.nvidia.com/pt-br/drivers/", "Drivers oficiais NVIDIA (GeForce/RTX)",
            "Escolha \"Instalação personalizada\" no instalador oficial para selecionar componentes."),
        new("AMD", DriverCategory.Gpu, "https://www.amd.com/en/support/download/drivers.html", "Drivers AMD Radeon (Adrenalin)",
            "A opção \"Somente driver\"/instalação mínima evita componentes opcionais."),
        new("AMD", DriverCategory.Chipset, "https://www.amd.com/en/support/download/drivers.html", "Drivers de chipset AMD (Ryzen)",
            "Selecione Chipsets → soquete (ex.: AM4/AM5) → chipset da placa."),
        new("Intel", DriverCategory.Gpu, "https://www.intel.com/content/www/us/en/support/detect.html", "Intel Driver & Support Assistant (gráficos Intel)",
            "Em notebooks, o fabricante pode bloquear o driver genérico; nesse caso use o site do fabricante do notebook."),
        new("Intel", DriverCategory.Chipset, "https://www.intel.com/content/www/us/en/download-center/home.html", "Central de downloads Intel (chipset/ME/RST)"),
        new("Intel", DriverCategory.Network, "https://www.intel.com/content/www/us/en/download-center/home.html", "Central de downloads Intel (Ethernet)"),
        new("Intel", DriverCategory.WiFi, "https://www.intel.com/content/www/us/en/support/detect.html", "Intel Driver & Support Assistant (Wi-Fi/Bluetooth)"),
        new("Intel", DriverCategory.Bluetooth, "https://www.intel.com/content/www/us/en/support/detect.html", "Intel Driver & Support Assistant (Bluetooth)"),
        new("Intel", DriverCategory.Storage, "https://www.intel.com/content/www/us/en/download-center/home.html", "Central de downloads Intel (RST/VMD)",
            "RST/VMD: troque somente se souber o modo do controlador (AHCI/RAID/VMD) — o sistema pode não inicializar."),
        new("Realtek", DriverCategory.Audio, "https://www.realtek.com/", "Realtek (site oficial)",
            "A Realtek recomenda os pacotes do fabricante da placa-mãe/notebook para áudio HD; prefira-os."),
        new("Realtek", DriverCategory.Network, "https://www.realtek.com/", "Realtek (site oficial — Communications Network ICs)"),
        new("Samsung", DriverCategory.Storage, "https://semiconductor.samsung.com/consumer-storage/support/tools/", "Samsung Magician / driver NVMe",
            "O driver NVMe nativo da Microsoft (stornvme) já atende a maioria dos casos."),
        new("Logitech", DriverCategory.Peripheral, "https://support.logi.com/", "Suporte Logitech"),
        new("Razer", DriverCategory.Peripheral, "https://mysupport.razer.com/", "Suporte Razer"),
        new("Corsair", DriverCategory.Peripheral, "https://www.corsair.com/us/en/s/downloads", "Downloads Corsair (iCUE)"),
        new("SteelSeries", DriverCategory.Peripheral, "https://steelseries.com/gg", "SteelSeries GG"),
        new("ASUS", DriverCategory.Motherboard, "https://www.asus.com/support/download-center/", "Central de downloads ASUS"),
        new("MSI", DriverCategory.Motherboard, "https://www.msi.com/support", "Suporte MSI"),
        new("Gigabyte", DriverCategory.Motherboard, "https://www.gigabyte.com/Support", "Suporte Gigabyte"),
        new("ASRock", DriverCategory.Motherboard, "https://www.asrock.com/support/index.asp", "Suporte ASRock"),
        new("Dell", DriverCategory.Motherboard, "https://www.dell.com/support/home", "Suporte Dell"),
        new("HP", DriverCategory.Motherboard, "https://support.hp.com/drivers", "Suporte HP"),
        new("Lenovo", DriverCategory.Motherboard, "https://pcsupport.lenovo.com/", "Suporte Lenovo"),
        new("Acer", DriverCategory.Motherboard, "https://www.acer.com/support", "Suporte Acer"),
    };

    public static IReadOnlyList<OfficialSource> Catalog => All;

    public static OfficialSource MicrosoftUpdateCatalog { get; } = new("Microsoft", DriverCategory.Other, "https://www.catalog.update.microsoft.com/",
        "Catálogo do Microsoft Update", "Drivers distribuídos via Windows Update, publicados pelos próprios fabricantes.");

    /// <summary>Normaliza um nome de fabricante (tabela de IDs ou texto do Windows) para a chave do mapa.</summary>
    public static string? NormalizeVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string n = name.ToUpperInvariant();
        (string Key, string Value)[] map =
        {
            ("NVIDIA", "NVIDIA"), ("ADVANCED MICRO", "AMD"), ("AMD", "AMD"), ("ATI ", "AMD"), ("INTEL", "Intel"), ("REALTEK", "Realtek"),
            ("SAMSUNG", "Samsung"), ("LOGITECH", "Logitech"), ("RAZER", "Razer"), ("CORSAIR", "Corsair"), ("STEELSERIES", "SteelSeries"),
            ("ASUS", "ASUS"), ("MICRO-STAR", "MSI"), ("MSI", "MSI"), ("GIGABYTE", "Gigabyte"), ("ASROCK", "ASRock"), ("DELL", "Dell"),
            ("HEWLETT", "HP"), ("HP", "HP"), ("LENOVO", "Lenovo"), ("ACER", "Acer"),
        };
        foreach (var (k, v) in map)
            if (n.StartsWith(k, StringComparison.Ordinal) || n.Contains(" " + k, StringComparison.Ordinal) || n.Contains("(" + k, StringComparison.Ordinal)) return v;
        return null;
    }

    public static IEnumerable<OfficialSource> For(string? vendor, DriverCategory category)
    {
        var v = NormalizeVendor(vendor);
        if (v is null) return [];
        var exact = All.Where(s => s.Vendor == v && s.Category == category).ToArray();
        if (exact.Length > 0) return exact;
        // Wi-Fi/Bluetooth/rede costumam compartilhar a página
        return All.Where(s => s.Vendor == v && category is DriverCategory.WiFi or DriverCategory.Bluetooth or DriverCategory.Network &&
                              s.Category is DriverCategory.WiFi or DriverCategory.Bluetooth or DriverCategory.Network).Take(1);
    }

    [GeneratedRegex(@"wi-?fi|wireless|wlan|802\.11", RegexOptions.IgnoreCase)] private static partial Regex WifiRx();

    /// <summary>Categoria a partir da classe de dispositivo do Windows (GUIDs padrão) e do nome.</summary>
    public static DriverCategory Categorize(DeviceRecord d)
    {
        string cls = (d.ClassName ?? "").ToUpperInvariant();
        var hw = d.PrimaryHardwareId;
        return cls switch
        {
            "DISPLAY" => DriverCategory.Gpu,
            "NET" => WifiRx().IsMatch(d.Name) ? DriverCategory.WiFi : DriverCategory.Network,
            "MEDIA" or "AUDIOENDPOINT" => DriverCategory.Audio,
            "BLUETOOTH" => DriverCategory.Bluetooth,
            "HDC" or "SCSIADAPTER" or "DISKDRIVE" or "STORAGEVOLUME" => DriverCategory.Storage,
            "HIDCLASS" or "MOUSE" or "KEYBOARD" => DriverCategory.Peripheral,
            "SYSTEM" or "USB" or "SMBUS" when hw is { Bus: HardwareBus.Pci or HardwareBus.Acpi } &&
                NormalizeVendor(hw.VendorName) is "AMD" or "Intel" => DriverCategory.Chipset,
            _ => DriverCategory.Other,
        };
    }

    /// <summary>
    /// Orientação para um dispositivo. Mostra somente a versão/data INSTALADAS (reais) e links oficiais;
    /// não compara com "última versão" (o RBM Optimizer não consulta sites de fabricantes).
    /// </summary>
    public static DriverSourceGuidance GuidanceFor(DeviceRecord device, string? boardManufacturer = null)
    {
        var cat = Categorize(device);
        var hw = device.PrimaryHardwareId;
        string? chip = hw?.VendorName ?? device.Manufacturer;
        string? board = NormalizeVendor(boardManufacturer) ?? NormalizeVendor(hw?.SubsystemVendorName);
        var sources = new List<OfficialSource>(For(chip, cat));
        var notes = new List<string> { NewestNotAlwaysBestPt };

        if (cat is DriverCategory.Audio or DriverCategory.Network or DriverCategory.WiFi or DriverCategory.Bluetooth or DriverCategory.Chipset or DriverCategory.Storage)
        {
            if (board is not null) sources.AddRange(All.Where(s => s.Vendor == board && s.Category == DriverCategory.Motherboard));
            notes.Add(OemFirstPt);
        }
        if (cat == DriverCategory.Gpu)
        {
            notes.Add("GPU: compare a versão instalada abaixo com a mostrada na página oficial; o RBM Optimizer não consulta a internet para não exibir dados não verificados.");
            if (NormalizeVendor(chip) == "NVIDIA") notes.Add("Para escolher componentes do pacote NVIDIA, use o Assistente de pacote NVIDIA do RBM Optimizer + \"Instalação personalizada\" do instalador oficial.");
        }
        if (sources.Count == 0) notes.Add("Fabricante sem página mapeada: use o Catálogo do Microsoft Update (busca pelo ID de hardware) ou o site do fabricante do computador.");
        sources.Add(MicrosoftUpdateCatalog);

        return new DriverSourceGuidance(device.InstanceId, device.Name, cat, chip, board,
            device.Driver?.Version, device.Driver?.Date, device.Driver?.Provider,
            sources.DistinctBy(s => s.Url + s.Category).ToArray(), notes);
    }

    /// <summary>Links de busca no Catálogo do Microsoft Update: ID de hardware completo e forma curta (VEN/DEV ou VID/PID).</summary>
    public static IReadOnlyList<CatalogSearchLink> CatalogSearches(DeviceRecord device)
    {
        var queries = new List<string>();
        if (device.HardwareIds.Count > 0) queries.Add(device.HardwareIds[0]);
        if (device.PrimaryHardwareId?.ShortId is { } s) queries.Add(s);
        if (device.HardwareIds.Count > 1 && device.HardwareIds[^1] is { } last && !queries.Contains(last, StringComparer.OrdinalIgnoreCase) &&
            last.Contains('&')) queries.Add(last);
        return queries.Distinct(StringComparer.OrdinalIgnoreCase).Select(q => new CatalogSearchLink(q, CatalogSearchUrl(q))).ToArray();
    }

    public static string CatalogSearchUrl(string query) => CatalogBase + Uri.EscapeDataString(query.Trim());

    /// <summary>Relatório de dispositivos desconhecidos/com problema, com identificação do fabricante e buscas oficiais.</summary>
    public static IReadOnlyList<UnknownDeviceReport> UnknownAndProblemDevices(DeviceInventory inventory, string? boardManufacturer = null) =>
        inventory.Devices.Where(d => d.IsUnknown || (d.HasProblem && !d.IsDisabled))
            .Select(d =>
            {
                var hw = d.PrimaryHardwareId;
                var g = GuidanceFor(d, boardManufacturer);
                string vendorTxt = hw?.VendorName is { } v ? $"fabricante do chip: {v}" : hw?.VendorId is { } id ? $"ID de fabricante {id} (não catalogado)" : "fabricante não identificado";
                string summary = d.IsUnknown
                    ? $"Sem driver instalado — {vendorTxt}."
                    : $"Código {d.ProblemCode}: {d.Problem?.DescriptionPt} ({vendorTxt}).";
                return new UnknownDeviceReport(d, hw?.VendorName, hw?.SubsystemVendorName, hw?.ShortId, CatalogSearches(d), g.Sources, summary);
            })
            .ToArray();
}
