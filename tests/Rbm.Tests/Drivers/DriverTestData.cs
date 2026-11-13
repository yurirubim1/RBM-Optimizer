using Rbm.Drivers;

namespace Rbm.Tests.Drivers;

/// <summary>Amostras capturadas de um Windows 11 26200 pt-BR (pnputil) e construtores de registros sintéticos.</summary>
internal static class DriverTestData
{
    public const string PnpUtilXml = """
<?xml version="1.0" encoding="utf-8"?>
<PnpUtil Version="10.0.26200" Command="/enum-drivers /format xml /ids /devices">
    <Driver DriverName="oem47.inf">
        <OriginalName>amdafd.inf</OriginalName>
        <ProviderName>AMD</ProviderName>
        <ClassName>System</ClassName>
        <ClassGuid>{4d36e97d-e325-11ce-bfc1-08002be10318}</ClassGuid>
        <DriverVersion>08/21/2025 25.30.0.1</DriverVersion>
        <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
        <CatalogAttributes>
            <Attribute>Declarative</Attribute>
        </CatalogAttributes>
        <WhcpVersion>Unknown</WhcpVersion>
        <DriverPackageId>amdafd.inf_amd64_d6709807244bfcf6</DriverPackageId>
        <FamilyId>AMD-amdafd.inf</FamilyId>
        <Devices>
            <Device InstanceId="PCI\VEN_1002&amp;DEV_AB28&amp;SUBSYS_AB281002&amp;REV_00\6&amp;2aa02770&amp;0&amp;01000009">
                <DeviceDescription>High Definition Audio Bus</DeviceDescription>
                <Status>Started</Status>
            </Device>
        </Devices>
    </Driver>
    <Driver DriverName="oem9.inf">
        <OriginalName>amdafd.inf</OriginalName>
        <ProviderName>AMD</ProviderName>
        <ClassName>System</ClassName>
        <ClassGuid>{4d36e97d-e325-11ce-bfc1-08002be10318}</ClassGuid>
        <DriverVersion>04/20/2022 22.20.0.0</DriverVersion>
        <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
        <CatalogAttributes>
            <Attribute>Declarative</Attribute>
        </CatalogAttributes>
        <WhcpVersion>Unknown</WhcpVersion>
        <DriverPackageId>amdafd.inf_amd64_f13ad03f2871bb72</DriverPackageId>
        <FamilyId>AMD-amdafd.inf</FamilyId>
    </Driver>
    <Driver DriverName="oem23.inf">
        <OriginalName>hdx_msiext_rtk.inf</OriginalName>
        <ProviderName>Realtek Semiconductor Corp.</ProviderName>
        <ClassName>Extension</ClassName>
        <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
        <ExtensionId>{c3a63edd-2d27-4b66-b155-5e94b43d926a}</ExtensionId>
        <DriverVersion>02/23/2026 6.0.9956.1</DriverVersion>
        <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
        <CatalogAttributes>
            <Attribute>Universal</Attribute>
        </CatalogAttributes>
        <WhcpVersion>Unknown</WhcpVersion>
        <DriverPackageId>hdx_msiext_rtk.inf_amd64_0123456789abcdef</DriverPackageId>
        <FamilyId>Realtek-hdx_msiext_rtk.inf</FamilyId>
    </Driver>
</PnpUtil>
""";

    /// <summary>Saída texto pt-BR real (rótulos localizados).</summary>
    public const string PnpUtilTextPtBr = """
Utilitário PnP da Microsoft

Nome Publicado:     oem47.inf
Nome Original:      amdafd.inf
Nome do Provedor:      AMD
Nome da Classe:         System
GUID de Classe:         {4d36e97d-e325-11ce-bfc1-08002be10318}
Versão do Driver:     08/21/2025 25.30.0.1
Nome do Signatário:        Microsoft Windows Hardware Compatibility Publisher
Atributos:         Declarative
Versão do WHCP:       Desconhecido

Nome Publicado:     oem23.inf
Nome Original:      hdx_msiext_rtk.inf
Nome do Provedor:      Realtek Semiconductor Corp.
Nome da Classe:         Extension
GUID de Classe:         {e2f84ce7-8efa-411c-aa69-97454ca4cb57}
IDs de Extensão:       {c3a63edd-2d27-4b66-b155-5e94b43d926a}
Versão do Driver:     02/23/2026 6.0.9956.1
Nome do Signatário:        Microsoft Windows Hardware Compatibility Publisher
Atributos:         Universal
Versão do WHCP:       Desconhecido

""";

    /// <summary>Formato texto em inglês de builds antigos do Windows 10 (sem "Original Name").</summary>
    public const string PnpUtilTextLegacyEn = """
Microsoft PnP Utility

Published name :            oem3.inf
Driver package provider :   Realtek
Class :                     Network adapters
Driver date and version :   07/11/2019 10.35.711.2019
Signer name :               Microsoft Windows Hardware Compatibility Publisher

Published name :            oem7.inf
Driver package provider :   NVIDIA
Class :                     Display adapters
Driver date and version :   12/05/2020 27.21.14.5671
Signer name :               Microsoft Windows Hardware Compatibility Publisher
""";

    public static readonly Guid SystemClass = new("4d36e97d-e325-11ce-bfc1-08002be10318");
    public static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static InstalledDriver Driver(string inf, string? version, DateTime? date, string provider = "Vendor",
        DriverSignatureState sig = DriverSignatureState.Signed) =>
        new(inf, @"C:\Windows\INF\" + inf, version, DriverVersion.ParseOrNull(version), date, provider, "desc", "sec", null, null,
            new DriverSignature(sig, sig == DriverSignatureState.Signed ? "Microsoft Windows Hardware Compatibility Publisher" : null, null, null, null));

    public static DeviceRecord Device(string id, string name, InstalledDriver? driver = null, int problem = 0, bool present = true,
        string className = "System", Guid? classGuid = null, string[]? hwids = null, uint? status = 0x180200A) =>
        new(id, name, name, className, classGuid ?? SystemClass, "Vendor", hwids ?? [id.Split('\\')[0] + @"\VEN_1022&DEV_1483"], [],
            null, id.Split('\\')[0], null, present, present ? status | (problem != 0 ? 0x400u : 0u) : null, problem,
            ProblemCodes.Describe(problem), driver, null);

    public static DeviceInventory Inventory(params DeviceRecord[] devices) =>
        new(DateTimeOffset.Now, devices, "test", TimeSpan.Zero, []);
}
