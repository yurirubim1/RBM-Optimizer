using Microsoft.Win32;

namespace Rbm.Gaming.Library;

/// <summary>GOG: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\{id} (gameName, path, exe). Somente leitura.</summary>
public sealed class GogLibraryScanner : IGameLibraryScanner
{
    public GameSource Source => GameSource.Gog;

    public Task<GameScanOutput> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private GameScanOutput Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<DetectedGame>();
        RegistryKey? root;
        try { root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games"); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { warnings.Add($"Registro GOG inacessível: {ex.Message}"); return new GameScanOutput(Source, games, warnings, false); }
        if (root is null) return new GameScanOutput(Source, games, warnings, false);

        using (root)
        {
            foreach (var sub in root.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null) continue;
                    string? name = k.GetValue("gameName") as string;
                    string? path = k.GetValue("path") as string;
                    string? exePath = k.GetValue("exe") as string;
                    string id = k.GetValue("gameID") as string ?? sub;
                    // DLCs têm "dependsOn" apontando para o jogo base.
                    if (k.GetValue("dependsOn") is string dep && !string.IsNullOrWhiteSpace(dep)) continue;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;

                    var notes = new List<string>();
                    bool dirOk = Directory.Exists(path);
                    string? launch = string.IsNullOrWhiteSpace(exePath) ? null : exePath;
                    ExecutableResolution exe = dirOk
                        ? ExecutableResolver.ResolveWithManifest(launch, path, "GOG", name, ct)
                        : launch is not null ? ExecutableResolver.FromManifest(launch, "GOG") : ExecutableResolution.None("Registro GOG sem 'exe'.");
                    if (!dirOk) notes.Add("Pasta de instalação não encontrada.");
                    games.Add(new DetectedGame($"gog:{id}", name, GameSource.Gog, id, dirOk ? Path.GetFullPath(path) : null,
                        exe, launch is not null && File.Exists(launch) ? Path.GetFullPath(launch) : null, null, [], notes));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
                { warnings.Add($"GOG {sub}: {ex.Message}"); }
            }
        }
        return new GameScanOutput(Source, games, warnings, true);
    }
}
