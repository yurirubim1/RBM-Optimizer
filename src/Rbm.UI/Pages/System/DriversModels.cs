using System.Globalization;
using Rbm.Core;
using Rbm.Drivers;
using Rbm.UI.Infrastructure;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>Linha da grade de dispositivos (página Drivers). Somente leitura, derivada de <see cref="DeviceRecord"/>.</summary>
public sealed class DriverDeviceRow
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public DriverDeviceRow(DeviceRecord d)
    {
        Device = d;
        var drv = d.Driver;
        Name = d.Name;
        ClassName = d.ClassName ?? "—";
        Manufacturer = d.Manufacturer ?? "—";
        Provider = drv?.Provider ?? "—";
        Version = drv?.Version ?? "—";
        ParsedVersion = drv?.ParsedVersion;
        Date = drv?.Date;
        DateText = drv?.Date is { } dt ? dt.ToString("dd/MM/yyyy", Pt) : "—";
        InfName = drv?.InfName ?? "—";
        IsThirdParty = drv?.IsThirdParty == true;
        IsUnsigned = drv?.Signature.State is DriverSignatureState.Unsigned or DriverSignatureState.Invalid;
        SignatureText = drv is null ? "Sem driver" : drv.Signature.State == DriverSignatureState.Signed && drv.Signature.Signer is { Length: > 0 } s
            ? s : drv.Signature.SignerKindPt;
        HasProblem = d.HasProblem || d.IsUnknown;
        StatusText = !d.IsPresent ? "Não conectado"
            : d.IsUnknown ? "Sem driver"
            : d.IsDisabled ? "Desativado"
            : d.HasProblem ? $"Problema (código {d.ProblemCode})"
            : "Funcionando";
        StatusLevel = HasProblem && !d.IsDisabled ? (object)(d.Problem?.Severity ?? Severity.Medium)
            : d.IsDisabled || !d.IsPresent ? (object)Severity.Info
            : (object)true;
        SearchBlob = string.Join('\u0001', new[] { d.Name, d.ClassName, d.Manufacturer, drv?.Provider, drv?.InfName, drv?.Version, d.InstanceId, d.Service }
            .Concat(d.HardwareIds).Where(x => !string.IsNullOrEmpty(x)));
    }

    public DeviceRecord Device { get; }
    public string Name { get; }
    public string ClassName { get; }
    public string Manufacturer { get; }
    public string Provider { get; }
    public string Version { get; }
    public DriverVersion? ParsedVersion { get; }
    public DateTime? Date { get; }
    public string DateText { get; }
    public string InfName { get; }
    public bool IsThirdParty { get; }
    public bool IsUnsigned { get; }
    public string SignatureText { get; }
    public bool HasProblem { get; }
    public string StatusText { get; }
    /// <summary>Severity (problema) ou bool true (OK) — interpretado pelo conversor LevelBrush.</summary>
    public object StatusLevel { get; }
    public string SearchBlob { get; }
}

/// <summary>Pacote do Driver Store com o estado de uso calculado pela análise.</summary>
public sealed class DriverPackageRow
{
    public DriverPackageRow(DriverPackage p, int deviceCount, bool oldDuplicate)
    {
        Package = p;
        DeviceCount = deviceCount;
        IsOldDuplicate = oldDuplicate;
        StatusText = deviceCount > 0 ? (deviceCount == 1 ? "Em uso (1 dispositivo)" : $"Em uso ({deviceCount} dispositivos)")
            : oldDuplicate ? "Versão antiga, sem uso"
            : "Sem dispositivo conectado";
        StatusLevel = deviceCount > 0 ? (object)true : oldDuplicate ? (object)Severity.Low : (object)Severity.Info;
        DateText = p.Date is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) : "—";
    }

    public DriverPackage Package { get; }
    public string PublishedName => Package.PublishedName;
    public string OriginalName => Package.OriginalName ?? "—";
    public string Provider => Package.Provider ?? "—";
    public string ClassName => Package.ClassName ?? "—";
    public string Version => Package.Version ?? "—";
    public DateTime? Date => Package.Date;
    public string DateText { get; }
    public string Signer => Package.Signer ?? "—";
    public string Kind => Package.IsExtension ? "Extensão" : "Base";
    public int DeviceCount { get; }
    public bool IsOldDuplicate { get; }
    public string StatusText { get; }
    public object StatusLevel { get; }
}

/// <summary>Grupo de versões duplicadas do mesmo driver no Driver Store.</summary>
public sealed record DriverDuplicateRow(string OriginalName, string Summary, IReadOnlyList<string> Lines);

/// <summary>Versão alternativa (Driver Store) compatível com o dispositivo selecionado.</summary>
public sealed class DriverCandidateRow
{
    public DriverCandidateRow(RollbackCandidate c)
    {
        Candidate = c;
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        Title = $"{c.Package.PublishedName} · v{c.Node.Version}" + (c.Node.Date is { } d ? $" ({d.ToString("dd/MM/yyyy", pt)})" : "");
        Relation = c.CompareToCurrent < 0 ? "Versão ANTERIOR à instalada" : c.CompareToCurrent > 0 ? "Versão MAIS NOVA que a instalada" : "Mesma versão (outro pacote)";
        Detail = $"{c.Node.Description} · provedor {c.Node.Provider} · INF original {c.Package.OriginalName ?? "—"}";
    }
    public RollbackCandidate Candidate { get; }
    public string Title { get; }
    public string Relation { get; }
    public string Detail { get; }
}

/// <summary>Fotografia salva (ou o estado atual) para o comparador do histórico.</summary>
public sealed record DriverSnapshotItem(string Title, DriverSnapshot Snapshot, bool IsCurrent)
{
    public override string ToString() => Title;
}

public sealed class DriverChangeRow
{
    public DriverChangeRow(DriverChange c)
    {
        Change = c;
        KindText = c.Kind switch
        {
            DriverChangeKind.Added => "Adicionado",
            DriverChangeKind.Removed => "Removido",
            DriverChangeKind.Upgraded => "Atualizado",
            DriverChangeKind.Downgraded => "Revertido",
            _ => "Alterado",
        };
        Level = c.Kind switch
        {
            DriverChangeKind.Removed or DriverChangeKind.Downgraded => (object)Severity.Medium,
            DriverChangeKind.Changed => (object)Severity.Low,
            _ => (object)true,
        };
    }
    public DriverChange Change { get; }
    public string KindText { get; }
    public object Level { get; }
    public string DeviceName => Change.DeviceName;
    public string Description => Change.DescriptionPt;
}

public sealed class NvidiaComponentRow
{
    public NvidiaComponentRow(NvidiaComponent c)
    {
        C = c;
        RoleText = c.Info.Role switch
        {
            NvidiaComponentRole.Essential => "Essencial",
            NvidiaComponentRole.Recommended => "Recomendado",
            NvidiaComponentRole.Optional => "Opcional",
            NvidiaComponentRole.Installer => "Instalador",
            _ => "Desconhecido",
        };
        Level = c.Info.Role switch
        {
            NvidiaComponentRole.Essential => (object)true,
            NvidiaComponentRole.Recommended => (object)Severity.Low,
            NvidiaComponentRole.Optional => (object)Severity.Info,
            _ => (object)Severity.Medium,
        };
        SizeText = Fmt.Bytes(c.SizeBytes);
        CatalogText = c.CatalogCount == 0 ? "sem catálogos" : $"{c.TrustedCatalogCount}/{c.CatalogCount} catálogos confiáveis";
    }
    public NvidiaComponent C { get; }
    public string Folder => C.FolderName;
    public string Name => C.Info.DisplayNamePt;
    public string Description => C.Info.DescriptionPt;
    public string Dependencies => C.Info.DependencyNotesPt;
    public string Version => C.NviVersion ?? "—";
    public string RoleText { get; }
    public object Level { get; }
    public string SizeText { get; }
    public string CatalogText { get; }
}
