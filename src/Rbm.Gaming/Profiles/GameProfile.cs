using Rbm.Gaming.Library;
using Rbm.Gaming.Sessions;

namespace Rbm.Gaming.Profiles;

/// <summary>Perfil persistido de um jogo (games.json).</summary>
/// <param name="Id">Mesmo Id do <see cref="DetectedGame"/> (ex.: "steam:730").</param>
/// <param name="PreferredGpu">Última preferência de GPU definida pelo app (o valor real é sempre relido do registro via <see cref="GpuPreferenceStore"/>).</param>
/// <param name="Sessions">Histórico de sessões de captura (mais antiga primeiro).</param>
/// <param name="AppliedChangeIds">Referências a alterações aplicadas (ids do diário de alterações).</param>
public sealed record GameProfile(
    string Id,
    string Name,
    string? ExecutablePath,
    string? InstallDirectory,
    GameSource Source,
    string? SourceId,
    GpuPreference? PreferredGpu,
    IReadOnlyList<FrameSessionSummary> Sessions,
    string? Notes,
    IReadOnlyList<string> AppliedChangeIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static GameProfile FromDetected(DetectedGame g) => new(
        g.Id, g.Name, g.Executable.Path, g.InstallDirectory, g.Source, g.SourceId, null, [], null, [], DateTimeOffset.Now, DateTimeOffset.Now);

    /// <summary>Nome do processo para captura (ex.: "game.exe").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProcessName => ExecutablePath is { } p ? Path.GetFileName(p) : null;
}

/// <summary>Documento raiz de games.json.</summary>
public sealed record GameProfilesDocument(int Version, IReadOnlyList<GameProfile> Games)
{
    public const int CurrentVersion = 1;
    public static readonly GameProfilesDocument Empty = new(CurrentVersion, []);
}
