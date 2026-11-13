namespace Rbm.Latency;

/// <summary>
/// Classificação de módulos de kernel conhecidos (por nome de arquivo) e dicas de investigação em pt-BR.
/// É uma heurística por nome: não prova que o módulo seja a causa de um problema.
/// </summary>
public static class DriverHints
{
    public const string Network = "Rede";
    public const string Graphics = "GPU/Vídeo";
    public const string Storage = "Armazenamento";
    public const string Audio = "Áudio";
    public const string UsbInput = "USB/Entrada";
    public const string Platform = "Plataforma/ACPI/Energia";
    public const string Framework = "Framework de drivers (KMDF)";
    public const string Kernel = "Kernel do Windows";

    private static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ndis.sys"] = Network, ["tcpip.sys"] = Network, ["netio.sys"] = Network, ["nwifi.sys"] = Network,
        ["wdiwifi.sys"] = Network, ["vwififlt.sys"] = Network, ["pacer.sys"] = Network, ["wfplwfs.sys"] = Network,
        ["nvlddmkm.sys"] = Graphics, ["amdkmdag.sys"] = Graphics, ["atikmdag.sys"] = Graphics, ["atikmpag.sys"] = Graphics,
        ["amdkmpfd.sys"] = Graphics, ["igdkmd64.sys"] = Graphics, ["igdkmdn64.sys"] = Graphics, ["igdkmdnd64.sys"] = Graphics,
        ["dxgkrnl.sys"] = Graphics, ["dxgmms2.sys"] = Graphics, ["basicdisplay.sys"] = Graphics, ["watchdog.sys"] = Graphics,
        ["storport.sys"] = Storage, ["stornvme.sys"] = Storage, ["storahci.sys"] = Storage, ["ataport.sys"] = Storage,
        ["classpnp.sys"] = Storage, ["disk.sys"] = Storage, ["amd_sata.sys"] = Storage, ["amdsata.sys"] = Storage,
        ["iastorac.sys"] = Storage, ["iastorvd.sys"] = Storage, ["iastora.sys"] = Storage, ["rcraid.sys"] = Storage,
        ["ntfs.sys"] = Storage, ["fltmgr.sys"] = Storage, ["volmgr.sys"] = Storage, ["partmgr.sys"] = Storage,
        ["hdaudbus.sys"] = Audio, ["portcls.sys"] = Audio, ["ks.sys"] = Audio, ["hdaudio.sys"] = Audio,
        ["usbaudio.sys"] = Audio, ["usbaudio2.sys"] = Audio, ["nvhda64v.sys"] = Audio,
        ["usbxhci.sys"] = UsbInput, ["ucx01000.sys"] = UsbInput, ["usbport.sys"] = UsbInput, ["usbhub.sys"] = UsbInput,
        ["usbhub3.sys"] = UsbInput, ["usbehci.sys"] = UsbInput, ["hidusb.sys"] = UsbInput, ["hidclass.sys"] = UsbInput,
        ["mouhid.sys"] = UsbInput, ["kbdhid.sys"] = UsbInput, ["mouclass.sys"] = UsbInput, ["kbdclass.sys"] = UsbInput,
        ["i8042prt.sys"] = UsbInput, ["bthport.sys"] = UsbInput, ["bthusb.sys"] = UsbInput,
        ["acpi.sys"] = Platform, ["intelppm.sys"] = Platform, ["amdppm.sys"] = Platform, ["processr.sys"] = Platform,
        ["pci.sys"] = Platform, ["amdgpio2.sys"] = Platform, ["amdi2c.sys"] = Platform, ["cmbatt.sys"] = Platform,
        ["acpiex.sys"] = Platform, ["amdpsp.sys"] = Platform,
        ["wdf01000.sys"] = Framework, ["wpprecorder.sys"] = Framework,
        ["ntoskrnl.exe"] = Kernel, ["ntkrnlmp.exe"] = Kernel, ["hal.dll"] = Kernel, ["clipsp.sys"] = Kernel,
    };

    private static readonly (string Prefix, string Category)[] Prefixes =
    {
        ("netwtw", Network), ("netwns", Network), ("netwbw", Network), ("rtwlan", Network), ("rt640", Network), ("rtux", Network),
        ("rtcx", Network), ("rtnic", Network), ("rtkfilter", Network), ("e1d", Network), ("e1i", Network), ("e1r", Network), ("e2f", Network),
        ("ixgbe", Network), ("b57nd", Network), ("bcmwl", Network), ("athw", Network), ("qcamain", Network), ("killer", Network), ("mtkwl", Network),
        ("rtkvhd", Audio), ("rtkhda", Audio), ("atihdw", Audio), ("intcaudio", Audio), ("intcdaud", Audio), ("chdrt", Audio), ("cthda", Audio),
        ("nvlddmkm", Graphics), ("igdkmd", Graphics), ("amdkm", Graphics),
        ("iastor", Storage), ("nvme", Storage),
    };

    /// <summary>Categoria provável do módulo ("" se desconhecida).</summary>
    public static string Categorize(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return "";
        if (Exact.TryGetValue(module, out var c)) return c;
        foreach (var (prefix, cat) in Prefixes)
            if (module.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cat;
        return "";
    }

    /// <summary>Dica de investigação (pt-BR) para o módulo/categoria. Sempre redigida como associação, não causa.</summary>
    public static string Hint(string module)
    {
        var m = module.ToLowerInvariant();
        if (m == "wdf01000.sys")
            return "Wdf01000.sys é o framework KMDF: muitos drivers executam suas DPCs através dele, então o driver realmente responsável não é identificável só pelo endereço da rotina. Correlacione com outros módulos do relatório ou use WPA (Windows Performance Analyzer) com pilhas de chamada.";
        if (m is "ntoskrnl.exe" or "ntkrnlmp.exe" or "hal.dll")
            return "Rotinas do próprio kernel (timers, DPCs de manutenção). A atribuição é ambígua: drivers podem agendar DPCs de timer que aparecem como kernel. Normalmente não é acionável diretamente.";
        if (m == "ndis.sys" || m == "tcpip.sys" || m == "netio.sys")
            return "Pilha de rede do Windows: picos aqui costumam estar associados ao driver da placa de rede/Wi-Fi (miniport) ou a tráfego intenso. Atualize o driver da placa de rede pelo site do fabricante e teste com a rede desconectada para comparar.";
        return Categorize(module) switch
        {
            Network => "Driver de rede/Wi-Fi: atualize pelo site do fabricante do chip; compare uma captura com o adaptador desativado. Varreduras de Wi-Fi em segundo plano são associadas a picos periódicos.",
            Graphics => "Driver de vídeo: picos podem ocorrer em trocas de modo, inicialização de jogos ou mudanças de clock. Atualize o driver (instalação limpa) e compare; picos isolados ao abrir aplicativos 3D são comuns.",
            Storage => "Pilha de armazenamento: picos associados a E/S intensa ou a drivers de controlador (SATA/NVMe/RAID). Atualize o driver de chipset/armazenamento e verifique a saúde dos discos.",
            Audio => "Driver de áudio: verifique atualizações do codec/driver HD Audio e desative dispositivos de áudio não usados (ex.: saídas HDMI/DP da GPU).",
            UsbInput => "Pilha USB/HID: dispositivos ou hubs USB problemáticos, controladores de terceiros ou suspensão seletiva podem estar associados. Teste desconectando dispositivos e trocando de porta.",
            Platform => "Plataforma/ACPI/energia: associado a firmware (BIOS/UEFI), gerenciamento de energia do processador ou controlador embarcado. Atualize BIOS e driver de chipset; compare com o plano de energia de alto desempenho.",
            _ => "Módulo sem categoria conhecida: identifique o fabricante pelas propriedades do arquivo (C:\\Windows\\System32\\drivers) e procure atualização ou desative o dispositivo correspondente para comparar.",
        };
    }
}
