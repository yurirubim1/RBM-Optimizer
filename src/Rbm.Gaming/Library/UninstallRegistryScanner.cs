using Microsoft.Win32;

namespace Rbm.Gaming.Library;

/// <summary>Entrada de desinstalação do Windows (subconjunto relevante).</summary>
public sealed record UninstallEntry(string KeyName, string? DisplayName, string? Publisher, string? InstallLocation, string? DisplayIcon, string? UninstallString);

/// <summary>
/// EA app / Ubisoft Connect / Battle.net (melhor esforço): entradas de desinstalação (HKLM 64/32 bits e HKCU)
/// classificadas por editora/desinstalador, mais HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs.
/// Exclui os próprios launchers e entradas de jogos do Steam/Epic ("Steam App N", steam://, EpicGamesLauncher).
/// A origem é sempre marcada e a confiança do executável é no máximo média.
/// </summary>
public sealed class UninstallRegistryScanner : IGameLibraryScanner
{
    private readonly GameSource _source;

    public UninstallRegistryScanner(GameSource source)
    {
        if (source is not (GameSource.EaApp or GameSource.UbisoftConnect or GameSource.BattleNet))
            throw new ArgumentOutOfRangeException(nameof(source), "Use EaApp, UbisoftConnect ou BattleNet.");
        _source = source;
    }

    public GameSource Source => _source;

    private static readonly string[] LauncherNames =
    [
        "EA app", "EA Desktop", "Origin", "Ubisoft Connect", "Uplay", "Ubisoft Game Launcher", "Battle.net", "Blizzard Battle.net App",
    ];

    /// <summary>Componentes que não são jogos (anti-cheat, runtimes, launchers, overlays, SDKs).</summary>
    private static readonly string[] NonGameNameMarkers =
    [
        "Anti-Cheat", "AntiCheat", "Anti Cheat", "Redistributable", "Runtime", "Launcher", "Overlay", "SDK", "Service", "Update Helper", "Background Services",
    ];

    /// <summary>Classifica uma entrada de desinstalação (função pura). Retorna null se não pertencer a EA/Ubisoft/Battle.net ou for o próprio launcher.</summary>
    public static GameSource? Classify(UninstallEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.DisplayName) || string.IsNullOrWhiteSpace(e.InstallLocation)) return null;
        if (e.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return null;
        string u = e.UninstallString ?? "";
        if (u.Contains("steam://", StringComparison.OrdinalIgnoreCase) || u.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase) || u.Contains("com.epicgames.launcher", StringComparison.OrdinalIgnoreCase))
            return null;
        if (LauncherNames.Any(n => string.Equals(e.DisplayName.Trim(), n, StringComparison.OrdinalIgnoreCase))) return null;
        if (NonGameNameMarkers.Any(m => e.DisplayName.Contains(m, StringComparison.OrdinalIgnoreCase))) return null;
        string pub = e.Publisher ?? "";

        if (u.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase) || u.Contains("Electronic Arts\\EA Desktop", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("Touchup.exe", StringComparison.OrdinalIgnoreCase) ||
            (pub.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) && !pub.Contains("EA Desktop", StringComparison.OrdinalIgnoreCase)))
            return GameSource.EaApp;
        if (u.Contains("uplay://uninstall", StringComparison.OrdinalIgnoreCase) || u.Contains("Ubisoft Game Launcher", StringComparison.OrdinalIgnoreCase) ||
            pub.StartsWith("Ubisoft", StringComparison.OrdinalIgnoreCase))
            return GameSource.UbisoftConnect;
        if (u.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || u.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) ||
            pub.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
            return GameSource.BattleNet;
        return null;
    }

    /// <summary>Lê todas as entradas de desinstalação (HKLM 64, HKLM 32, HKCU).</summary>
    public static IReadOnlyList<UninstallEntry> ReadUninstallEntries(List<string>? warnings = null)
    {
        var list = new List<UninstallEntry>();
        void ReadFrom(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var root = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k is null) continue;
                        list.Add(new UninstallEntry(name, k.GetValue("DisplayName") as string, k.GetValue("Publisher") as string,
                            k.GetValue("InstallLocation") as string, k.GetValue("DisplayIcon") as string, k.GetValue("UninstallString") as string));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { warnings?.Add($"Uninstall ({hive}/{view}): {ex.Message}"); }
        }
        ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry64);
        ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry32);
        ReadFrom(RegistryHive.CurrentUser, RegistryView.Default);
        return list;
    }

    /// <summary>Extrai o caminho do .exe de um DisplayIcon ("C:\x\game.exe,0" ou com aspas).</summary>
    public static string? ExeFromDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string s = displayIcon.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        s = s.Trim().Trim('"');
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    public Task<GameScanOutput> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private GameScanOutput Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<DetectedGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string label = _source switch { GameSource.EaApp => "EA app", GameSource.UbisoftConnect => "Ubisoft Connect", _ => "Battle.net" };
        string prefix = _source switch { GameSource.EaApp => "ea", GameSource.UbisoftConnect => "ubisoft", _ => "battlenet" };

        void Add(string sid, string name, string dir, string? iconExe, string origin)
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try { full = Path.GetFullPath(dir.Trim().Trim('"')); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!Directory.Exists(full) || !seen.Add(full.TrimEnd('\\'))) return;
            string? manifestExe = iconExe is not null && iconExe.StartsWith(full, StringComparison.OrdinalIgnoreCase) && File.Exists(iconExe) ? iconExe : null;
            var exe = ExecutableResolver.ResolveWithManifest(manifestExe, full, label, name, ct);
            // Ícone de desinstalação não é manifesto: limita a confiança a Média.
            if (exe.Confidence == Rbm.Core.Confidence.High) exe = exe with { Confidence = Rbm.Core.Confidence.Medium, Reason = $"Executável indicado pelo ícone da entrada de desinstalação ({label})." };
            games.Add(new DetectedGame($"{prefix}:{sid}", name, _source, sid, full, exe, null, null, [], [$"Detectado via {origin} (melhor esforço)."]));
        }

        // Entradas de desinstalação primeiro (têm DisplayName melhor); depois fontes específicas.
        foreach (var e in ReadUninstallEntries(warnings))
        {
            if (Classify(e) != _source) continue;
            Add(e.KeyName, e.DisplayName!.Trim(), e.InstallLocation!, ExeFromDisplayIcon(e.DisplayIcon), "entrada de desinstalação do Windows");
        }

        if (_source == GameSource.UbisoftConnect)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var inst = bk.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
                if (inst is not null)
                    foreach (var id in inst.GetSubKeyNames())
                    {
                        using var k = inst.OpenSubKey(id);
                        if (k?.GetValue("InstallDir") is string dir && !string.IsNullOrWhiteSpace(dir))
                            Add(id, Path.GetFileName(dir.TrimEnd('/', '\\')), dir.Replace('/', '\\'), null, "registro do Ubisoft Connect");
                    }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            { warnings.Add($"Registro Ubisoft: {ex.Message}"); }
        }
        return new GameScanOutput(_source, games, warnings, games.Count > 0);
    }
}
