using System.Globalization;
using Microsoft.Win32;

namespace Rbm.Gaming.Library;

/// <summary>Dados de um appmanifest_*.acf.</summary>
public sealed record SteamAppManifest(string AppId, string Name, string InstallDir, long? SizeOnDisk, int? StateFlags, string? LauncherPath);

/// <summary>
/// Steam: lê SteamPath (HKCU\Software\Valve\Steam) ou InstallPath (HKLM\SOFTWARE\WOW6432Node\Valve\Steam),
/// steamapps\libraryfolders.vdf (formato novo e antigo) e cada appmanifest_*.acf. O Steam não guarda o
/// executável no .acf (fica no appinfo.vdf binário), então o executável é resolvido por heurística.
/// </summary>
public sealed class SteamLibraryScanner : IGameLibraryScanner
{
    /// <summary>AppIDs que não são jogos (runtimes/redistribuíveis/ferramentas).</summary>
    private static readonly HashSet<string> NonGameAppIds =
    [
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", "1391110", "1628350", "2180100", // Steam Linux Runtime
        "1493710", "1420170", "1887720", "2348590", "2805730", "3658110", // Proton
        "1007",    // Steam Client (SDK)
        "431960",  // Wallpaper Engine (aplicativo, não jogo)
    ];

    private readonly string? _steamRootOverride;

    /// <param name="steamRootOverride">Para testes: pasta raiz do Steam (ignora o registro).</param>
    public SteamLibraryScanner(string? steamRootOverride = null) => _steamRootOverride = steamRootOverride;

    public GameSource Source => GameSource.Steam;

    public static string? FindSteamRoot()
    {
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (hkcu?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (hklm?.GetValue("InstallPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>Extrai as pastas de biblioteca do libraryfolders.vdf (formato novo com "path" e antigo "1" "D:\\Lib").</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var root = VdfParser.Parse(vdfText);
        var lf = root.Child("libraryfolders");
        var result = new List<string>();
        if (lf is null) return result;
        foreach (var entry in lf.Children)
        {
            if (!int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            string? path = entry.IsObject ? entry.GetValue("path") : entry.Value;
            if (!string.IsNullOrWhiteSpace(path)) result.Add(path.Replace('/', '\\'));
        }
        return result;
    }

    /// <summary>Faz o parse de um appmanifest_*.acf. Retorna null se faltar appid/installdir.</summary>
    public static SteamAppManifest? ParseAppManifest(string acfText)
    {
        var root = VdfParser.Parse(acfText);
        var app = root.Child("AppState");
        if (app is null) return null;
        string? id = app.GetValue("appid");
        string? dir = app.GetValue("installdir");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(dir)) return null;
        string name = app.GetValue("name") ?? dir;
        long? size = long.TryParse(app.GetValue("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) ? sz : null;
        int? flags = int.TryParse(app.GetValue("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fl) ? fl : null;
        return new SteamAppManifest(id, name, dir, size, flags, app.GetValue("LauncherPath"));
    }

    public static bool IsNonGame(SteamAppManifest m) =>
        NonGameAppIds.Contains(m.AppId) ||
        m.Name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase) ||
        m.Name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        m.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
        m.Name.EndsWith(" Dedicated Server", StringComparison.OrdinalIgnoreCase);

    public Task<GameScanOutput> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private GameScanOutput Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<DetectedGame>();
        string? root = _steamRootOverride ?? FindSteamRoot();
        if (root is null) return new GameScanOutput(Source, games, warnings, false);

        var libraries = new List<string> { root };
        string lfPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(lfPath))
        {
            try { libraries.AddRange(ParseLibraryFolders(File.ReadAllText(lfPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfParseException)
            { warnings.Add($"Não foi possível ler libraryfolders.vdf: {ex.Message}"); }
        }
        else warnings.Add("libraryfolders.vdf não encontrado; apenas a biblioteca padrão foi verificada.");

        foreach (var lib in libraries.Select(SafeFull).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) { warnings.Add($"Biblioteca inacessível ou removida: {lib}"); continue; }
            List<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"Falha ao listar {steamapps}: {ex.Message}"); continue; }

            foreach (var acf in manifests)
            {
                ct.ThrowIfCancellationRequested();
                SteamAppManifest? m;
                try { m = ParseAppManifest(File.ReadAllText(acf)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VdfParseException)
                { warnings.Add($"{Path.GetFileName(acf)}: {ex.Message}"); continue; }
                if (m is null || IsNonGame(m)) continue;

                string installDir = Path.Combine(steamapps, "common", m.InstallDir);
                var notes = new List<string>();
                bool dirOk = Directory.Exists(installDir);
                ExecutableResolution exe;
                if (dirOk) exe = ExecutableResolver.ResolveFromDirectory(installDir, m.Name, ct: ct);
                else { exe = ExecutableResolution.None("Pasta de instalação ausente (download incompleto ou desinstalado)."); notes.Add("Pasta de instalação não encontrada."); }
                if (m.StateFlags is { } f && (f & 4) == 0) notes.Add("O Steam indica que a instalação não está completa (StateFlags).");

                games.Add(new DetectedGame($"steam:{m.AppId}", m.Name, GameSource.Steam, m.AppId,
                    dirOk ? installDir : null, exe, null, m.SizeOnDisk, [], notes));
            }
        }
        return new GameScanOutput(Source, games, warnings, true);
    }

    private static string? SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
