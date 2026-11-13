using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rbm.Drivers;

/// <summary>Driver de um dispositivo em um instante (registro serializável para histórico).</summary>
public sealed record DriverSnapshotEntry(
    string InstanceId,
    string DeviceName,
    string? ClassName,
    string? InfName,
    string? Provider,
    string? Version,
    DateTime? Date,
    bool IsPresent,
    int ProblemCode);

/// <summary>Fotografia "dispositivo → driver" que o app pode persistir e comparar ao longo do tempo.</summary>
public sealed record DriverSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    string MachineName,
    string OsVersion,
    IReadOnlyList<DriverSnapshotEntry> Entries)
{
    public const int CurrentSchema = 1;

    /// <summary>Cria a fotografia a partir do inventário (somente dispositivos com driver ou com problema).</summary>
    public static DriverSnapshot FromInventory(DeviceInventory inventory) => new(
        CurrentSchema,
        inventory.CollectedAt,
        Environment.MachineName,
        Environment.OSVersion.VersionString,
        inventory.Devices
            .Where(d => d.Driver is not null || d.ProblemCode != 0)
            .Select(d => new DriverSnapshotEntry(d.InstanceId, d.Name, d.ClassName, d.Driver?.InfName, d.Driver?.Provider,
                d.Driver?.Version, d.Driver?.Date, d.IsPresent, d.ProblemCode))
            .OrderBy(e => e.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DriverSnapshot? FromJson(string json) => JsonSerializer.Deserialize<DriverSnapshot>(json, JsonOptions);
}

public enum DriverChangeKind { Added, Removed, Upgraded, Downgraded, Changed }

/// <summary>Mudança de driver entre duas fotografias. "Changed" = mesma versão, mas INF/provedor/data diferentes.</summary>
public sealed record DriverChange(
    DriverChangeKind Kind,
    string InstanceId,
    string DeviceName,
    DriverSnapshotEntry? Before,
    DriverSnapshotEntry? After,
    string DescriptionPt);

public sealed record DriverSnapshotDiff(DriverSnapshot Older, DriverSnapshot Newer, IReadOnlyList<DriverChange> Changes)
{
    public int Count(DriverChangeKind kind) => Changes.Count(c => c.Kind == kind);
}

public static class DriverSnapshotComparer
{
    /// <summary>Compara duas fotografias pelo ID de instância (comparação de versão em 4 partes, depois data).</summary>
    public static DriverSnapshotDiff Diff(DriverSnapshot older, DriverSnapshot newer)
    {
        var a = ToMap(older); var b = ToMap(newer);
        var changes = new List<DriverChange>();
        foreach (var (id, after) in b)
        {
            if (!a.TryGetValue(id, out var before))
            {
                if (after.InfName is not null)
                    changes.Add(new(DriverChangeKind.Added, id, after.DeviceName, null, after, $"Novo driver: {Describe(after)}."));
                continue;
            }
            if (before.InfName is null && after.InfName is null) continue;
            if (before.InfName is null)
            { changes.Add(new(DriverChangeKind.Added, id, after.DeviceName, before, after, $"Driver instalado: {Describe(after)}.")); continue; }
            if (after.InfName is null)
            { changes.Add(new(DriverChangeKind.Removed, id, after.DeviceName, before, after, $"Driver removido (antes: {Describe(before)}).")); continue; }

            int c = DriverVersionMath.Compare(DriverVersion.ParseOrNull(after.Version), after.Date, DriverVersion.ParseOrNull(before.Version), before.Date);
            if (c > 0) changes.Add(new(DriverChangeKind.Upgraded, id, after.DeviceName, before, after, $"Atualizado: {before.Version} → {after.Version}."));
            else if (c < 0) changes.Add(new(DriverChangeKind.Downgraded, id, after.DeviceName, before, after, $"Revertido para versão anterior: {before.Version} → {after.Version}."));
            else if (!string.Equals(before.InfName, after.InfName, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(before.Provider, after.Provider, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(before.Version, after.Version, StringComparison.OrdinalIgnoreCase))
                changes.Add(new(DriverChangeKind.Changed, id, after.DeviceName, before, after, $"Pacote alterado: {Describe(before)} → {Describe(after)}."));
        }
        foreach (var (id, before) in a)
            if (!b.ContainsKey(id) && before.InfName is not null)
                changes.Add(new(DriverChangeKind.Removed, id, before.DeviceName, before, null, $"Dispositivo/driver não existe mais (antes: {Describe(before)})."));

        changes.Sort((x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : string.Compare(x.DeviceName, y.DeviceName, StringComparison.OrdinalIgnoreCase));
        return new DriverSnapshotDiff(older, newer, changes);
    }

    private static Dictionary<string, DriverSnapshotEntry> ToMap(DriverSnapshot s)
    {
        var d = new Dictionary<string, DriverSnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in s.Entries) d[e.InstanceId] = e;
        return d;
    }

    private static string Describe(DriverSnapshotEntry e) =>
        $"{e.InfName} v{e.Version ?? "?"}{(e.Date is { } dt ? $" ({dt:dd/MM/yyyy})" : "")}{(e.Provider is { } p ? $", {p}" : "")}";
}
