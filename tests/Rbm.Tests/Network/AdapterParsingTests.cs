using Rbm.Network.Adapters;
using Xunit;

namespace Rbm.Tests.Network;

public class AdapterParsingTests
{
    private const string NetshPtBr = """

        Há 1 interface no sistema:

            Nome                   : Wi-Fi
            Descrição              : Intel(R) Wi-Fi 6E AX211 160MHz
            GUID                   : 6b0a5a3e-1234-4cde-9f00-112233445566
            Endereço físico        : aa:bb:cc:dd:ee:ff
            Tipo de interface      : Principal
            Estado                 : conectado
            SSID                   : MinhaRede
            AP BSSID               : 11:22:33:44:55:66
            Banda                  : 5 GHz
            Canal                  : 36
            Tipo de rede           : Infraestrutura
            Tipo de rádio          : 802.11ax
            Autenticação           : WPA2-Personal
            Codificação            : CCMP
            Modo de conexão        : Perfil
            Taxa de recepção (Mbps)  : 1200,9
            Taxa de transmissão (Mbps) : 864
            Sinal                  : 78%
            Perfil                 : MinhaRede
            Rssi                   : -58

            Status da rede hospedada : Não disponível
        """;

    private const string NetshEnglishTwo = """
        There are 2 interfaces on the system:

            Name                   : Wi-Fi
            Description            : Realtek 8822CE
            GUID                   : 11111111-2222-3333-4444-555555555555
            State                  : connected
            SSID                   : Home
            BSSID                  : 66:55:44:33:22:11
            Radio type             : 802.11n
            Authentication         : WPA2-Personal
            Cipher                 : CCMP
            Channel                : 6
            Receive rate (Mbps)    : 144.4
            Transmit rate (Mbps)   : 144.4
            Signal                 : 35%
            Profile                : Home

            Name                   : Wi-Fi 2
            Description            : USB adapter
            GUID                   : 99999999-2222-3333-4444-555555555555
            State                  : disconnected
        """;

    [Fact]
    public void Netsh_PtBr_ParsesAllFields()
    {
        var e = Assert.Single(NetshWlanParser.Parse(NetshPtBr));
        var d = e.Details;
        Assert.Equal("Wi-Fi", e.Name);
        Assert.True(d.Available);
        Assert.Equal("MinhaRede", d.Ssid);
        Assert.Equal("11:22:33:44:55:66", d.Bssid);
        Assert.Equal(78, d.SignalQualityPercent);
        Assert.Equal(-58, d.RssiDbm);
        Assert.Equal(36, d.Channel);
        Assert.Equal("5 GHz", d.Band);
        Assert.Equal(1200.9, d.RxRateMbps);
        Assert.Equal(864, d.TxRateMbps);
        Assert.Equal("WPA2-Personal", d.Authentication);
        Assert.Equal("CCMP", d.Cipher);
        Assert.Equal("802.11ax", d.PhyType);
        Assert.Equal("MinhaRede", d.ProfileName);
    }

    [Fact]
    public void Netsh_Mojibake_StillParses()
    {
        // Acentos corrompidos por codificação errada do console.
        var garbled = NetshPtBr.Replace("ç", "‡").Replace("ã", "Æ").Replace("á", " ");
        var d = Assert.Single(NetshWlanParser.Parse(garbled)).Details;
        Assert.Equal(1200.9, d.RxRateMbps);
        Assert.Equal("802.11ax", d.PhyType);
        Assert.Equal("WPA2-Personal", d.Authentication);
    }

    [Fact]
    public void Netsh_English_MultipleInterfaces()
    {
        var list = NetshWlanParser.Parse(NetshEnglishTwo);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].Details.Available);
        Assert.Equal(35, list[0].Details.SignalQualityPercent);
        Assert.Equal("66:55:44:33:22:11", list[0].Details.Bssid);
        Assert.Equal("2,4 GHz (inferido do canal)", list[0].Details.Band);
        Assert.False(list[1].Details.Available);
        Assert.Equal("99999999-2222-3333-4444-555555555555", list[1].Guid);
    }

    [Fact]
    public void Netsh_NoWirelessInterface_ReturnsEmpty()
    {
        Assert.Empty(NetshWlanParser.Parse("Não há nenhuma interface sem fio no sistema."));
        Assert.Empty(NetshWlanParser.Parse(""));
    }

    [Theory]
    [InlineData(2437.0, "2,4 GHz")]
    [InlineData(5180.0, "5 GHz")]
    [InlineData(5955.0, "6 GHz")]
    [InlineData(null, null)]
    public void BandFromFrequency(double? mhz, string? band) => Assert.Equal(band, WifiFormat.BandFromFrequencyMHz(mhz));

    [Fact]
    public void BandFromChannel_FlagsAmbiguityForWifi6E()
    {
        Assert.Equal("2,4 GHz (inferido do canal)", WifiFormat.BandFromChannel(6, phyType: 7));
        Assert.Contains("6 GHz", WifiFormat.BandFromChannel(5, phyType: 10));
        Assert.Equal("6 GHz (inferido do canal)", WifiFormat.BandFromChannel(229));
    }

    [Fact]
    public void WlanStructs_HaveNativeX64Sizes()
    {
        Assert.Equal(532, Rbm.Network.Native.WlanNative.SizeOfInterfaceInfo);
        Assert.Equal(604, Rbm.Network.Native.WlanNative.SizeOfConnectionAttributes);
        Assert.Equal(360, Rbm.Network.Native.WlanNative.SizeOfBssEntry);
    }

    [Fact]
    public void CounterDelta_DetectsIncreaseAndReset()
    {
        var t0 = DateTimeOffset.Now;
        var a = new AdapterCounters(t0, 0, 0, 1000, 1000, 5, 10, 0, 0, "x");
        var b = new AdapterCounters(t0.AddMinutes(5), 0, 0, 2000, 1500, 9, 10, 1, 0, "x");
        var d = AdapterCounterDelta.Compute(a, b);
        Assert.Equal(4, d.ReceivedErrors);
        Assert.Equal(1, d.OutboundErrors);
        Assert.Equal(1500, d.PacketsReceived + d.PacketsSent);
        Assert.False(d.CounterReset);
        Assert.True(AdapterCounterDelta.Compute(b, a).CounterReset);
    }

    [Theory]
    [InlineData("2025-10-03", 2025, 10, 3)]
    [InlineData("10-3-2025", 2025, 10, 3)]
    public void ParseDriverDate(string s, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), Rbm.Network.Adapters.NetworkAdapterInventory.ParseDriverDate(s));

    [Fact]
    public void ClassifyKind_UsesMediumAndVirtualFlag()
    {
        Assert.Equal(AdapterKind.Ethernet, NetworkAdapterInventory.ClassifyKind(false, 14, null));
        Assert.Equal(AdapterKind.WiFi, NetworkAdapterInventory.ClassifyKind(false, 9, null));
        Assert.Equal(AdapterKind.Virtual, NetworkAdapterInventory.ClassifyKind(true, 14, "Radmin VPN"));
    }

    [Theory]
    [InlineData("Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("WAN Miniport (IP)", true)]
    [InlineData("Microsoft Kernel Debug Network Adapter", true)]
    [InlineData("Realtek PCIe GbE Family Controller", false)]
    public void PseudoInterfaceDescription(string d, bool pseudo) => Assert.Equal(pseudo, NetworkAdapterInventory.IsPseudoDescription(d));

    [Fact]
    public void MapStatus()
    {
        Assert.Equal(AdapterStatus.Up, NetworkAdapterInventory.MapStatus(2, 1, 1));
        Assert.Equal(AdapterStatus.Disabled, NetworkAdapterInventory.MapStatus(3, 2, 0));
        Assert.Equal(AdapterStatus.Disconnected, NetworkAdapterInventory.MapStatus(2, 2, 2));
    }
}
