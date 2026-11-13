using System.Text;

namespace Rbm.Network.Adapters;

/// <summary>Conversões e nomes pt-BR para dados de Wi-Fi (lógica pura, testável).</summary>
public static class WifiFormat
{
    /// <summary>Banda a partir da frequência central (MHz).</summary>
    public static string? BandFromFrequencyMHz(double? mhz) => mhz switch
    {
        null or <= 0 => null,
        < 3000 => "2,4 GHz",
        >= 5150 and < 5925 => "5 GHz",
        >= 5925 and <= 7125 => "6 GHz",
        >= 57000 => "60 GHz",
        _ => null,
    };

    /// <summary>
    /// Banda inferida do canal (sem frequência). Canais 1–14 podem ser 2,4 GHz ou 6 GHz; por isso a inferência
    /// é marcada como incerta quando o PHY é Wi-Fi 6/6E/7 (HE/EHT), que também opera em 6 GHz.
    /// </summary>
    public static string? BandFromChannel(int? channel, int phyType = 0)
    {
        if (channel is not > 0) return null;
        bool sixGhzCapablePhy = phyType is 10 or 11;
        if (channel <= 14) return sixGhzCapablePhy ? "2,4 GHz ou 6 GHz (inferido do canal)" : "2,4 GHz (inferido do canal)";
        if (channel is >= 32 and <= 177) return sixGhzCapablePhy ? "5 GHz ou 6 GHz (inferido do canal)" : "5 GHz (inferido do canal)";
        if (channel <= 233) return "6 GHz (inferido do canal)";
        return null;
    }

    public static string PhyName(int dot11PhyType) => dot11PhyType switch
    {
        1 => "FHSS (802.11)",
        2 => "DSSS (802.11)",
        3 => "Infravermelho",
        4 => "802.11a (OFDM)",
        5 => "802.11b (HR-DSSS)",
        6 => "802.11g (ERP)",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        9 => "802.11ad (DMG)",
        10 => "802.11ax (Wi-Fi 6/6E)",
        11 => "802.11be (Wi-Fi 7)",
        _ => $"Desconhecido ({dot11PhyType})",
    };

    public static string AuthName(int algo) => algo switch
    {
        1 => "Aberta",
        2 => "Chave compartilhada (WEP)",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        5 => "WPA-None",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 => "WPA3-Enterprise 192-bit",
        9 => "WPA3-Personal (SAE)",
        10 => "OWE",
        11 => "WPA3-Enterprise",
        _ => $"Outro ({algo})",
    };

    public static string CipherName(int algo) => algo switch
    {
        0x00 => "Nenhuma",
        0x01 => "WEP-40",
        0x02 => "TKIP",
        0x04 => "CCMP (AES)",
        0x05 => "WEP-104",
        0x06 => "BIP",
        0x08 => "GCMP",
        0x09 => "GCMP-256",
        0x0a => "CCMP-256",
        0x100 => "Grupo WPA",
        0x101 => "WEP",
        _ => $"Outra (0x{algo:X})",
    };

    public static string InterfaceStateName(int s) => s switch
    {
        0 => "Não pronto",
        1 => "Conectado",
        2 => "Rede ad hoc formada",
        3 => "Desconectando",
        4 => "Desconectado",
        5 => "Associando",
        6 => "Procurando redes",
        7 => "Autenticando",
        _ => $"Desconhecido ({s})",
    };

    public static string FormatMac(ReadOnlySpan<byte> mac)
    {
        if (mac.IsEmpty) return "";
        var sb = new StringBuilder(mac.Length * 3);
        for (int i = 0; i < mac.Length; i++) { if (i > 0) sb.Append(':'); sb.Append(mac[i].ToString("x2")); }
        return sb.ToString();
    }
}
