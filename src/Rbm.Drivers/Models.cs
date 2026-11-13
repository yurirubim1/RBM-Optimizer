using Rbm.Core;

namespace Rbm.Drivers;

/// <summary>Estado da assinatura do pacote de driver (verificado pelo INF + catálogo, sem rede).</summary>
public enum DriverSignatureState
{
    /// <summary>Verificação não solicitada (ver <see cref="InventoryOptions.ResolveSignatures"/>).</summary>
    NotChecked = 0,
    Signed = 1,
    Unsigned = 2,
    /// <summary>Assinatura presente, porém inválida/não confiável/sem catálogo correspondente.</summary>
    Invalid = 3,
    /// <summary>Não foi possível verificar (INF ausente, erro de API). Ver <see cref="DriverSignature.Detail"/>.</summary>
    Unavailable = 4,
}

/// <summary>Assinatura do driver instalado. <see cref="Signer"/> é o assinante digital informado pelo Windows.</summary>
public sealed record DriverSignature(
    DriverSignatureState State,
    string? Signer,
    string? CatalogFile,
    uint? SignerScore,
    string? Detail)
{
    public static readonly DriverSignature NotChecked = new(DriverSignatureState.NotChecked, null, null, null, null);

    /// <summary>Descrição pt-BR da origem da assinatura, derivada do SignerScore do SetupAPI.</summary>
    public string SignerKindPt => SignerScore switch
    {
        null => State switch
        {
            DriverSignatureState.Signed => "Assinado",
            DriverSignatureState.Unsigned => "Não assinado",
            DriverSignatureState.Invalid => "Assinatura inválida",
            DriverSignatureState.Unavailable => "Assinatura não verificada (indisponível)",
            _ => "Não verificado",
        },
        0x0D000001 or 0x0D000002 => "Assinado (Windows Logo)",
        0x0D000003 => "Driver nativo do Windows (inbox)",
        0x0D000004 => "Assinado pela Microsoft (não classificado)",
        0x0D000005 => "Assinado pelo WHQL (Microsoft Windows Hardware Compatibility Publisher)",
        0x0F000000 => "Assinatura Authenticode de terceiro",
        0x80000000 => "Não assinado",
        _ => $"Assinatura (score 0x{SignerScore:X8})",
    };
}

/// <summary>
/// Driver associado a um dispositivo (propriedades DEVPKEY_Device_Driver*).
/// <para><c>InfName</c>: "oemNN.inf" (terceiro, publicado) ou INF nativo (ex.: "machine.inf");
/// <c>InfPath</c>: caminho completo em %windir%\INF.</para>
/// </summary>
public sealed record InstalledDriver(
    string InfName,
    string InfPath,
    string? Version,
    DriverVersion? ParsedVersion,
    DateTime? Date,
    string? Provider,
    string? Description,
    string? InfSection,
    string? MatchingDeviceId,
    uint? Rank,
    DriverSignature Signature)
{
    /// <summary>Pacote de terceiro publicado no Driver Store (oemNN.inf).</summary>
    public bool IsThirdParty => DriverStoreRules.IsPublishedOemInf(InfName);
}

/// <summary>Problema reportado pelo Configuration Manager (código CM_PROB_* / ConfigManagerErrorCode).</summary>
public sealed record ProblemCodeInfo(int Code, string Symbol, string DescriptionPt, string SuggestedActionPt, Severity Severity);

/// <summary>
/// Um dispositivo PnP (presente ou não) com seu driver.
/// <para><c>StatusFlags</c>: flags DN_* (somente presentes). <c>ProblemCode</c>: 0 = sem problema
/// (para não presentes é o último código registrado, se houver).</para>
/// </summary>
public sealed record DeviceRecord(
    string InstanceId,
    string Name,
    string? DeviceDescription,
    string? ClassName,
    Guid? ClassGuid,
    string? Manufacturer,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? Service,
    string? Enumerator,
    string? LocationInfo,
    bool IsPresent,
    uint? StatusFlags,
    int ProblemCode,
    ProblemCodeInfo? Problem,
    InstalledDriver? Driver,
    DateTime? LastArrival)
{
    public bool HasDriver => Driver is not null;

    /// <summary>Dispositivo presente e iniciado (DN_STARTED).</summary>
    public bool IsStarted => IsPresent && StatusFlags is { } f && (f & 0x8) != 0;

    public bool IsDisabled => ProblemCode == ProblemCodes.Disabled;

    /// <summary>
    /// "Dispositivo desconhecido": presente, sem driver instalado e com código 28/1 ou classe "Unknown"
    /// (ou sem classe E com problema). Nós sem classe e sem problema (ex.: HTREE\ROOT\0, dispositivos raw) não contam.
    /// </summary>
    public bool IsUnknown => IsPresent && Driver is null &&
        !InstanceId.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase) &&
        (ProblemCode is ProblemCodes.FailedInstall or ProblemCodes.NotConfigured ||
         (ClassGuid is null && ProblemCode != 0) ||
         string.Equals(ClassName, "Unknown", StringComparison.OrdinalIgnoreCase));

    public bool HasProblem => IsPresent && ProblemCode != 0;

    public HardwareIdInfo? PrimaryHardwareId => HardwareIds.Count > 0 ? HardwareIdParser.Parse(HardwareIds[0]) : null;
}

/// <summary>Resultado do inventário de dispositivos. <c>Source</c>: "SetupAPI" (preferido) ou "WMI" (fallback).</summary>
public sealed record DeviceInventory(
    DateTimeOffset CollectedAt,
    IReadOnlyList<DeviceRecord> Devices,
    string Source,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings)
{
    public IEnumerable<DeviceRecord> Present => Devices.Where(d => d.IsPresent);
    public IEnumerable<DeviceRecord> ProblemDevices => Devices.Where(d => d.HasProblem || d.IsUnknown);
    public DeviceRecord? Find(string instanceId) => Devices.FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
}

public sealed record InventoryOptions
{
    /// <summary>Incluir dispositivos não presentes ("fantasmas"). Padrão: true.</summary>
    public bool IncludeNotPresent { get; init; } = true;
    /// <summary>Verificar assinatura do INF de cada driver (SetupVerifyInfFile, com cache por INF). Padrão: true.</summary>
    public bool ResolveSignatures { get; init; } = true;
    /// <summary>Permite usar WMI caso o SetupAPI falhe. Padrão: true.</summary>
    public bool AllowWmiFallback { get; init; } = true;

    public static InventoryOptions Default { get; } = new();
}

/// <summary>Dispositivo listado pelo pnputil /devices para um pacote.</summary>
public sealed record PackageDeviceRef(string InstanceId, string? Description, string? Status);

/// <summary>
/// Pacote de driver de terceiro no Driver Store.
/// <c>DriverPackageId</c> é o nome da pasta no FileRepository (ex.: "amdafd.inf_amd64_d6709807244bfcf6"); pode faltar no formato texto.
/// </summary>
public sealed record DriverPackage(
    string PublishedName,
    string? OriginalName,
    string? Provider,
    string? ClassName,
    Guid? ClassGuid,
    Guid? ExtensionId,
    string? Version,
    DriverVersion? ParsedVersion,
    DateTime? Date,
    string? Signer,
    IReadOnlyList<string> CatalogAttributes,
    string? DriverPackageId,
    string? FamilyId,
    IReadOnlyList<PackageDeviceRef> Devices)
{
    public bool IsExtension => ExtensionId is not null;

    /// <summary>Pasta do pacote no Driver Store, quando conhecida.</summary>
    public string? StoreDirectory => DriverPackageId is { Length: > 0 } id && DriverStoreRules.IsSafePackageId(id)
        ? Path.Combine(DriverStoreRules.FileRepository, id) : null;

    /// <summary>Caminho do INF dentro do Driver Store (FileRepository), quando conhecido.</summary>
    public string? StoreInfPath => StoreDirectory is { } dir && OriginalName is { } o && DriverStoreRules.IsSafeFileName(o)
        ? Path.Combine(dir, o) : null;

    /// <summary>Chave de "mesmo driver": INF original + classe (+ extensão). Provedor é ignorado (fabricantes mudam o texto).</summary>
    public string FamilyKey => DriverStoreRules.FamilyKey(OriginalName ?? PublishedName, ClassGuid, ExtensionId);
}

/// <summary>Pacotes de terceiros no Driver Store. <c>Source</c>: "pnputil-xml" ou "pnputil-text".</summary>
public sealed record DriverStoreInventory(
    DateTimeOffset CollectedAt,
    IReadOnlyList<DriverPackage> Packages,
    string Source,
    IReadOnlyList<string> Warnings)
{
    public DriverPackage? Find(string publishedName) =>
        Packages.FirstOrDefault(p => string.Equals(p.PublishedName, publishedName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Grupo de pacotes do mesmo driver (mesmo INF original/classe) com mais de uma versão no store.
/// <c>Packages</c> vai do mais novo para o mais antigo; <c>InUsePublishedNames</c> são os usados por algum dispositivo do inventário.
/// </summary>
public sealed record DuplicatePackageGroup(
    string FamilyKey,
    string OriginalName,
    IReadOnlyList<DriverPackage> Packages,
    IReadOnlyList<string> InUsePublishedNames);

/// <summary>
/// Junção dispositivos × pacotes. <c>Unreferenced</c>: pacotes sem nenhum dispositivo (presente ou não) associado no inventário.
/// </summary>
public sealed record DriverStoreAnalysis(
    IReadOnlyDictionary<string, IReadOnlyList<DeviceRecord>> DevicesByPackage,
    IReadOnlyList<DuplicatePackageGroup> Duplicates,
    IReadOnlyList<DriverPackage> Unreferenced);
