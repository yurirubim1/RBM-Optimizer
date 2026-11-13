using System.Text.Json;

namespace Rbm.Gaming.Library;

/// <summary>Dados relevantes de um manifesto .item do Epic Games Launcher.</summary>
public sealed record EpicManifest(
    string DisplayName, string InstallLocation, string? LaunchExecutable, string? AppName, string? CatalogItemId,
    bool IsIncompleteInstall, bool IsApplication, IReadOnlyList<string> Categories)
{
    /// <summary>true quando AppCategories está presente e não contém "games" (ex.: Unreal Engine, plugins).</summary>
    public bool IsNonGame => Categories.Count > 0 && !Categories.Any(c => c.Equals("games", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Epic: lê %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON).
/// LaunchExecutable é relativo a InstallLocation → executável com confiança alta.
/// </summary>
public sealed class EpicLibraryScanner : IGameLibraryScanner
{
    private readonly string _manifestDir;

    public EpicLibraryScanner(string? manifestDirOverride = null) =>
        _manifestDir = manifestDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public GameSource Source => GameSource.Epic;

    /// <summary>Faz o parse de um .item. Retorna null se faltar DisplayName/InstallLocation.</summary>
    public static EpicManifest? ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        string? display = Str("DisplayName");
        string? loc = Str("InstallLocation");
        if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(loc)) return null;
        var cats = new List<string>();
        if (r.TryGetProperty("AppCategories", out var ac) && ac.ValueKind == JsonValueKind.Array)
            foreach (var c in ac.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String && c.GetString() is { } s) cats.Add(s);
        bool isApp = !r.TryGetProperty("bIsApplication", out var ia) || ia.ValueKind != JsonValueKind.False;
        return new EpicManifest(display, loc.Replace('/', '\\'), Str("LaunchExecutable")?.Replace('/', '\\'), Str("AppName"), Str("CatalogItemId"),
            Bool("bIsIncompleteInstall"), isApp, cats);
    }

    public Task<GameScanOutput> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private GameScanOutput Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<DetectedGame>();
        if (!Directory.Exists(_manifestDir)) return new GameScanOutput(Source, games, warnings, false);
        List<string> files;
        try { files = Directory.EnumerateFiles(_manifestDir, "*.item").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { warnings.Add($"Falha ao listar manifestos: {ex.Message}"); return new GameScanOutput(Source, games, warnings, true); }

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            EpicManifest? m;
            try { m = ParseManifest(File.ReadAllText(f)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { warnings.Add($"{Path.GetFileName(f)}: {ex.Message}"); continue; }
            if (m is null || m.IsNonGame || !m.IsApplication) continue;

            var notes = new List<string>();
            if (m.IsIncompleteInstall) notes.Add("A Epic indica instalação incompleta.");
            bool dirOk = Directory.Exists(m.InstallLocation);
            string? launch = string.IsNullOrWhiteSpace(m.LaunchExecutable) ? null : Path.Combine(m.InstallLocation, m.LaunchExecutable);
            ExecutableResolution exe;
            if (!dirOk) { exe = ExecutableResolution.None("Pasta de instalação não encontrada."); notes.Add("Pasta de instalação não encontrada."); }
            else exe = ExecutableResolver.ResolveWithManifest(launch, m.InstallLocation, "Epic", m.DisplayName, ct);

            string sid = m.AppName ?? m.CatalogItemId ?? m.DisplayName;
            games.Add(new DetectedGame($"epic:{sid}", m.DisplayName, GameSource.Epic, sid, dirOk ? Path.GetFullPath(m.InstallLocation) : null,
                exe, launch is not null && File.Exists(launch) ? Path.GetFullPath(launch) : null, null, [], notes));
        }
        return new GameScanOutput(Source, games, warnings, true);
    }
}
