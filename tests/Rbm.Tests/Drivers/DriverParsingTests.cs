using Rbm.Core;
using Rbm.Drivers;
using Xunit;

namespace Rbm.Tests.Drivers;

public class DriverVersionTests
{
    [Theory]
    [InlineData("25.30.0.1", 25, 30, 0, 1)]
    [InlineData("25.30.0.0001", 25, 30, 0, 1)]
    [InlineData("10.0", 10, 0, 0, 0)]
    [InlineData(" 32.0.21045.16010 ", 32, 0, 21045, 16010)]
    public void Parses_valid_versions(string text, int a, int b, int c, int d)
    {
        Assert.True(DriverVersion.TryParse(text, out var v));
        Assert.Equal(new DriverVersion(a, b, c, d), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.a.2")]
    [InlineData("70000.1")]
    [InlineData("-1.0")]
    public void Rejects_invalid_versions(string? text) => Assert.False(DriverVersion.TryParse(text, out _));

    [Fact]
    public void Compares_numerically_not_textually()
    {
        Assert.True(DriverVersion.ParseOrNull("10.0.10")!.Value > DriverVersion.ParseOrNull("10.0.9")!.Value);
        Assert.True(DriverVersion.ParseOrNull("2.2.0.137")!.Value > DriverVersion.ParseOrNull("2.2.0.136")!.Value);
        Assert.Equal(0, DriverVersion.ParseOrNull("1.0")!.Value.CompareTo(DriverVersion.ParseOrNull("1.0.0.0")!.Value));
    }

    [Fact]
    public void FromPacked_decodes_16bit_parts()
    {
        ulong packed = (32UL << 48) | (0UL << 32) | (21045UL << 16) | 16010UL;
        Assert.Equal(new DriverVersion(32, 0, 21045, 16010), DriverVersion.FromPacked(packed));
    }

    [Fact]
    public void Compare_uses_date_when_versions_equal_or_missing()
    {
        var v = DriverVersion.ParseOrNull("1.0.0.0");
        Assert.True(DriverVersionMath.Compare(v, new DateTime(2024, 1, 2), v, new DateTime(2023, 1, 1)) > 0);
        Assert.True(DriverVersionMath.Compare(null, new DateTime(2020, 1, 1), null, new DateTime(2021, 1, 1)) < 0);
        Assert.Equal(0, DriverVersionMath.Compare(null, null, null, null));
        // Versão vence a data na comparação "do usuário"; o PnP usa data primeiro
        var older = DriverVersion.ParseOrNull("1.0.0.1"); var newer = DriverVersion.ParseOrNull("2.0.0.0");
        Assert.True(DriverVersionMath.Compare(newer, new DateTime(2020, 1, 1), older, new DateTime(2022, 1, 1)) > 0);
        Assert.True(DriverVersionMath.CompareLikePnp(newer, new DateTime(2020, 1, 1), older, new DateTime(2022, 1, 1)) < 0);
    }

    [Theory]
    [InlineData("08/21/2025 25.30.0.1", 2025, 8, 21, "25.30.0.1")]
    [InlineData("08/21/2025, 25.30.0.0001", 2025, 8, 21, "25.30.0.0001")]
    [InlineData("1/2/2006 10.0.26100.1", 2006, 1, 2, "10.0.26100.1")]
    public void Parses_us_date_and_version(string text, int y, int m, int d, string ver)
    {
        Assert.True(DriverVersionMath.TryParseDateAndVersion(text, out var date, out var v));
        Assert.Equal(new DateTime(y, m, d), date);
        Assert.Equal(ver, v);
    }

    [Theory]
    [InlineData("21/08/2025 25.30.0.1")] // dia/mês invertido → mês 21 inválido
    [InlineData("02/30/2025 1.0")]
    [InlineData("Microsoft Windows Hardware Compatibility Publisher")]
    [InlineData("08/21/2025")]
    public void Rejects_invalid_date_version(string text) => Assert.False(DriverVersionMath.TryParseDateAndVersion(text, out _, out _));
}

public class PnpUtilParserTests
{
    [Fact]
    public void Parses_xml_with_devices_extensions_and_ids()
    {
        var list = PnpUtilParser.ParseXml(DriverTestData.PnpUtilXml);
        Assert.Equal(3, list.Count);
        var a = list[0];
        Assert.Equal("oem47.inf", a.PublishedName);
        Assert.Equal("amdafd.inf", a.OriginalName);
        Assert.Equal("AMD", a.Provider);
        Assert.Equal(DriverTestData.SystemClass, a.ClassGuid);
        Assert.Equal("25.30.0.1", a.Version);
        Assert.Equal(new DateTime(2025, 8, 21), a.Date);
        Assert.Equal("amdafd.inf_amd64_d6709807244bfcf6", a.DriverPackageId);
        Assert.NotNull(a.StoreDirectory);
        Assert.EndsWith(@"FileRepository\amdafd.inf_amd64_d6709807244bfcf6\amdafd.inf", a.StoreInfPath);
        Assert.Single(a.Devices);
        Assert.StartsWith(@"PCI\VEN_1002&DEV_AB28", a.Devices[0].InstanceId);
        Assert.Equal("Started", a.Devices[0].Status);
        Assert.Equal(["Declarative"], a.CatalogAttributes);

        var ext = list[2];
        Assert.True(ext.IsExtension);
        Assert.Equal(new Guid("c3a63edd-2d27-4b66-b155-5e94b43d926a"), ext.ExtensionId);
        Assert.Empty(ext.Devices);
    }

    [Fact]
    public void Xml_parser_ignores_non_oem_and_rejects_dtd()
    {
        Assert.Empty(PnpUtilParser.ParseXml("""<?xml version="1.0"?><PnpUtil><Driver DriverName="..\evil.inf"/></PnpUtil>"""));
        Assert.ThrowsAny<System.Xml.XmlException>(() => PnpUtilParser.ParseXml(
            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><PnpUtil>&e;</PnpUtil>"""));
    }

    [Fact]
    public void Text_parser_matches_xml_for_ptBR_labels()
    {
        var text = PnpUtilParser.ParseText(DriverTestData.PnpUtilTextPtBr);
        var xml = PnpUtilParser.ParseXml(DriverTestData.PnpUtilXml);
        Assert.Equal(2, text.Count);
        foreach (var t in text)
        {
            var x = xml.Single(p => p.PublishedName == t.PublishedName);
            Assert.Equal(x.OriginalName, t.OriginalName);
            Assert.Equal(x.Provider, t.Provider);
            Assert.Equal(x.ClassName, t.ClassName);
            Assert.Equal(x.ClassGuid, t.ClassGuid);
            Assert.Equal(x.ExtensionId, t.ExtensionId);
            Assert.Equal(x.Version, t.Version);
            Assert.Equal(x.Date, t.Date);
            Assert.Equal(x.Signer, t.Signer);
            Assert.Null(t.DriverPackageId); // o formato texto não traz a pasta do store
        }
    }

    [Fact]
    public void Text_parser_handles_legacy_english_format_without_original_name()
    {
        var list = PnpUtilParser.ParseText(DriverTestData.PnpUtilTextLegacyEn);
        Assert.Equal(2, list.Count);
        Assert.Equal("oem3.inf", list[0].PublishedName);
        Assert.Null(list[0].OriginalName);
        Assert.Equal("Realtek", list[0].Provider);
        Assert.Equal("Network adapters", list[0].ClassName);
        Assert.Equal("10.35.711.2019", list[0].Version);
        Assert.Equal(new DateTime(2019, 7, 11), list[0].Date);
        Assert.Equal("Microsoft Windows Hardware Compatibility Publisher", list[0].Signer);
        Assert.Equal("NVIDIA", list[1].Provider);
    }

    [Fact]
    public void Text_parser_tolerates_garbage() =>
        Assert.Empty(PnpUtilParser.ParseText("Uso: pnputil ...\r\nErro: comando inválido\r\n"));
}

public class InfFileTests
{
    private const string Inf = """
; comentário
[Version]
Signature="$WINDOWS NT$"
Class=Display ; classe
ClassGuid={4D36E968-E325-11CE-BFC1-08002BE10318}
Provider=%ProviderString%
DriverVer=08/21/2025, 25.30.0.0001
CatalogFile=generic.cat
CatalogFile.NTamd64=x64.cat
CatalogFile.NTarm64=arm.cat

[Strings]
ProviderString="Advanced Micro Devices, Inc."
""";

    [Fact]
    public void Reads_header_resolving_strings_and_x64_catalog()
    {
        var h = InfFile.ParseHeader(Inf);
        Assert.Equal("Display", h.Class);
        Assert.Equal(DriverTestData.DisplayClass, h.ClassGuid);
        Assert.Equal("Advanced Micro Devices, Inc.", h.Provider);
        Assert.Equal(new DateTime(2025, 8, 21), h.Date);
        Assert.Equal(new DriverVersion(25, 30, 0, 1), h.ParsedVersion);
        Assert.Equal("x64.cat", h.CatalogFile);
        Assert.Equal(3, h.AllCatalogEntries.Count);
    }

    [Fact]
    public void Missing_catalog_is_null()
    {
        var h = InfFile.ParseHeader("[Version]\nClass=System\n");
        Assert.Null(h.CatalogFile);
        Assert.Null(h.ParsedVersion);
    }
}

public class HardwareIdTests
{
    [Fact]
    public void Parses_pci_with_subsystem_vendor_last()
    {
        var h = HardwareIdParser.Parse(@"PCI\VEN_1002&DEV_73FF&SUBSYS_52171849&REV_C7");
        Assert.Equal(HardwareBus.Pci, h.Bus);
        Assert.Equal("1002", h.VendorId);
        Assert.Equal("73FF", h.DeviceId);
        Assert.Equal("1849", h.SubsystemVendorId);
        Assert.Equal("ASRock", h.SubsystemVendorName);
        Assert.Equal("C7", h.Revision);
        Assert.Equal("AMD (ATI/Radeon)", h.VendorName);
        Assert.Equal("VEN_1002&DEV_73FF", h.ShortId);
    }

    [Fact]
    public void Parses_hdaudio_with_subsystem_vendor_first()
    {
        var h = HardwareIdParser.Parse(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_14629C96&REV_1004");
        Assert.Equal(HardwareBus.HdAudio, h.Bus);
        Assert.Equal("Realtek", h.VendorName);
        Assert.Equal("0897", h.DeviceId);
        Assert.Equal("1462", h.SubsystemVendorId);
        Assert.Equal("MSI", h.SubsystemVendorName);
    }

    [Theory]
    [InlineData(@"USB\VID_046D&PID_C52B&REV_1211&MI_00", "Logitech", "VID_046D&PID_C52B")]
    [InlineData(@"HID\VID_1532&PID_0084&MI_00", "Razer", "VID_1532&PID_0084")]
    [InlineData(@"USB\VID_1B1C&PID_1B2E", "Corsair", "VID_1B1C&PID_1B2E")]
    public void Parses_usb_and_hid(string id, string vendor, string shortId)
    {
        var h = HardwareIdParser.Parse(id);
        Assert.Equal(vendor, h.VendorName);
        Assert.Equal(shortId, h.ShortId);
    }

    [Fact]
    public void Parses_acpi_and_unknown_vendor_is_null_not_guessed()
    {
        Assert.Equal("AMD", HardwareIdParser.Parse(@"ACPI\AMDI0030").VendorName);
        Assert.Equal("Intel", HardwareIdParser.Parse(@"ACPI\INT33A1").VendorName);
        var unknown = HardwareIdParser.Parse(@"PCI\VEN_ABCD&DEV_0001");
        Assert.Equal("ABCD", unknown.VendorId);
        Assert.Null(unknown.VendorName);
        Assert.Equal(HardwareBus.Unknown, HardwareIdParser.Parse("").Bus);
    }
}

public class ProblemCodeTests
{
    [Fact]
    public void Zero_is_no_problem() => Assert.Null(ProblemCodes.Describe(0));

    [Theory]
    [InlineData(28, "CM_PROB_FAILED_INSTALL", Severity.Medium)]
    [InlineData(43, "CM_PROB_FAILED_POST_START", Severity.High)]
    [InlineData(22, "CM_PROB_DISABLED", Severity.Info)]
    [InlineData(52, "CM_PROB_UNSIGNED_DRIVER", Severity.High)]
    public void Known_codes_have_ptBR_text(int code, string symbol, Severity sev)
    {
        var p = ProblemCodes.Describe(code)!;
        Assert.Equal(symbol, p.Symbol);
        Assert.Equal(sev, p.Severity);
        Assert.False(string.IsNullOrWhiteSpace(p.DescriptionPt));
        Assert.False(string.IsNullOrWhiteSpace(p.SuggestedActionPt));
    }

    [Fact]
    public void Table_covers_1_to_57_and_unknown_codes_are_explicit()
    {
        for (int i = 1; i <= 57; i++) Assert.NotEqual($"CM_PROB_{i}", ProblemCodes.Describe(i)!.Symbol);
        Assert.Contains("não catalogado", ProblemCodes.Describe(999)!.DescriptionPt);
    }
}
