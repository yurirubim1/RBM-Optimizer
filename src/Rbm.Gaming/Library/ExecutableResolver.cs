using System.Text.RegularExpressions;
using Rbm.Core;

namespace Rbm.Gaming.Library;

/// <summary>Executável candidato encontrado na pasta do jogo.</summary>
/// <param name="RelativePath">Caminho relativo à pasta de instalação.</param>
/// <param name="SizeBytes">Tamanho em bytes.</param>
/// <param name="HasUnityDataFolder">Existe a pasta irmã "&lt;nome&gt;_Data" (padrão de jogos Unity).</param>
public sealed record ExeCandidate(string RelativePath, long SizeBytes, bool HasUnityDataFolder = false);

/// <summary>
/// Heurística para escolher o executável principal quando o manifesto da loja não informa:
/// descarta launchers, crash reporters, instaladores, redistribuíveis e anti-cheat; dá preferência a
/// executáveis "-Shipping" (Unreal — é o processo que realmente renderiza), nome parecido com a pasta e
/// maior tamanho. A confiança é sempre explicitada.
/// </summary>
public static partial class ExecutableResolver
{
    private static readonly string[] ExcludedDirs =
    [
        "_commonredist", "commonredist", "redist", "redistributables", "redistributable", "directx", "dxsetup", "vcredist",
        "dotnet", "installer", "__installer", "installers", "support", "easyanticheat", "battleye", "eac", "prereq", "prerequisites",
        "crashreporter", "crashpad", "__overlay", "ue4prereqs", "ueprereqs", "thirdparty", "tools", "uninstall", "launcher",
        "engine\\binaries\\thirdparty", "mono", "jre", "java", "python", "steamworks shared", "pbsvc", "punkbuster",
    ];

    [GeneratedRegex(
        @"^(unins\d*|uninst.*|.*setup.*|.*install.*|.*crash.*|.*report.*|vc_?redist.*|dxsetup|dxwebsetup|dotnet.*|ndp\d.*|ue\d?prereq.*|" +
        @"easyanticheat.*|eac.*launcher|start_protected_game|beservice.*|battleye.*|.*updater.*|.*update\.exe|.*helper.*|cef.*|.*webhelper.*|" +
        @"qtwebengineprocess|.*browser.*|.*launcher.*|.*bootstrap.*|.*touchup.*|.*cleanup.*|.*dump.*|.*diag.*|.*config.*|.*settings.*|" +
        @"unitycrashhandler.*|zfgamebrowser|.*benchmark.*|.*editor.*|.*server.*|.*vrmonitor.*|pnkbstr.*|activation.*|.*register.*|link2ea.*|" +
        @"oalinst|physx.*|.*redist.*|7z.*|.*pak.*tool.*|crs-.*|.*handler.*|.*agent.*|.*service.*|.*_eac(_.*)?|.*_be|.*eac_eos.*)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedNameRegex();

    [GeneratedRegex(@"[^a-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();

    /// <summary>Indica se o nome do executável parece não ser o jogo (launcher, instalador, crash reporter...).</summary>
    public static bool IsExcludedName(string fileName) => ExcludedNameRegex().IsMatch(Path.GetFileName(fileName));

    /// <summary>Indica se o caminho relativo passa por uma pasta típica de redistribuíveis/anti-cheat/instalador.</summary>
    public static bool IsInExcludedDirectory(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath)?.Replace('/', '\\').ToLowerInvariant() ?? "";
        if (dir.Length == 0) return false;
        foreach (var part in dir.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (Array.IndexOf(ExcludedDirs, part) >= 0) return true;
        foreach (var ex in ExcludedDirs)
            if (ex.Contains('\\') && dir.Contains(ex, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Escolhe o executável principal entre candidatos (função pura, testável).
    /// </summary>
    /// <param name="installDirectory">Pasta de instalação (absoluta) — usada para montar o caminho final.</param>
    /// <param name="candidates">Executáveis encontrados (caminho relativo + tamanho).</param>
    /// <param name="gameName">Nome do jogo (opcional) para comparar com o nome do executável.</param>
    public static ExecutableResolution Choose(string installDirectory, IReadOnlyList<ExeCandidate> candidates, string? gameName = null)
    {
        if (candidates.Count == 0) return ExecutableResolution.None("Nenhum executável encontrado na pasta de instalação.");

        var viable = candidates.Where(c => !IsExcludedName(c.RelativePath) && !IsInExcludedDirectory(c.RelativePath)).ToList();
        if (viable.Count == 0)
            return ExecutableResolution.None("Só foram encontrados launchers/instaladores/redistribuíveis; escolha o executável manualmente.");

        string folderKey = Normalize(Path.GetFileName(installDirectory.TrimEnd('\\', '/')));
        string nameKey = Normalize(gameName ?? "");
        long maxSize = viable.Max(c => c.SizeBytes);

        double Score(ExeCandidate c)
        {
            string file = Path.GetFileNameWithoutExtension(c.RelativePath);
            string key = Normalize(file.Replace("-Win64-Shipping", "", StringComparison.OrdinalIgnoreCase)
                                       .Replace("-WinGDK-Shipping", "", StringComparison.OrdinalIgnoreCase));
            double s = maxSize > 0 ? 40.0 * c.SizeBytes / maxSize : 0;              // tamanho relativo
            if (file.EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase)) s += 35; // Unreal: processo real
            if (c.HasUnityDataFolder) s += 35;                                         // Unity: exe pequeno + pasta _Data
            if (key.Length >= 3 && (key == folderKey || (nameKey.Length >= 3 && key == nameKey))) s += 25;
            else if (key.Length >= 3 && (folderKey.Contains(key) || key.Contains(folderKey) || (nameKey.Length >= 3 && (nameKey.Contains(key) || key.Contains(nameKey))))) s += 12;
            int depth = c.RelativePath.Count(ch => ch is '\\' or '/');
            s -= Math.Min(depth, 5) * 1.5;                                             // leve preferência por raiz
            if (c.SizeBytes < 400 * 1024 && !c.HasUnityDataFolder) s -= 15;           // stubs/bootstraps muito pequenos
            return s;
        }

        var ranked = viable.Select(c => (c, score: Score(c))).OrderByDescending(x => x.score).ThenByDescending(x => x.c.SizeBytes).ToList();
        var best = ranked[0];
        string full = Path.Combine(installDirectory, best.c.RelativePath);
        var alternatives = ranked.Skip(1).Take(4).Select(x => Path.Combine(installDirectory, x.c.RelativePath)).ToList();

        Confidence conf;
        string reason;
        if (ranked.Count == 1)
        {
            conf = Confidence.Medium;
            reason = "Único executável plausível na pasta (heurística; o manifesto da loja não informa o executável).";
        }
        else
        {
            double margin = best.score - ranked[1].score;
            conf = margin >= 20 ? Confidence.Medium : Confidence.Low;
            reason = $"Escolhido por heurística (tamanho, nome, padrão Unreal '-Shipping') entre {ranked.Count} candidatos; " +
                     (conf == Confidence.Medium ? "candidato claramente à frente." : "outros candidatos próximos — confirme manualmente.");
        }
        return new ExecutableResolution(full, conf, reason, alternatives);
    }

    /// <summary>
    /// Enumera executáveis da pasta (profundidade e quantidade limitadas, ignora pastas inacessíveis/reparse points)
    /// e aplica <see cref="Choose"/>.
    /// </summary>
    public static ExecutableResolution ResolveFromDirectory(string installDirectory, string? gameName = null, int maxDepth = 5, int maxFiles = 3000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            return ExecutableResolution.None("Pasta de instalação não encontrada.");
        var list = new List<ExeCandidate>();
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((installDirectory, 0));
        while (stack.Count > 0 && list.Count < maxFiles)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        bool unity = Directory.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + "_Data"));
                        list.Add(new ExeCandidate(Path.GetRelativePath(installDirectory, f), fi.Length, unity));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (list.Count >= maxFiles) break;
                }
                if (depth < maxDepth)
                    foreach (var d in Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            var di = new DirectoryInfo(d);
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        stack.Push((d, depth + 1));
                    }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return Choose(installDirectory, list, gameName);
    }

    /// <summary>Executável informado por manifesto da loja (confiança alta se existir no disco).</summary>
    public static ExecutableResolution FromManifest(string fullPath, string sourceLabel)
    {
        if (File.Exists(fullPath))
            return new ExecutableResolution(Path.GetFullPath(fullPath), Confidence.High, $"Informado pelo manifesto da {sourceLabel}.", []);
        return new ExecutableResolution(null, Confidence.Low, $"O manifesto da {sourceLabel} aponta para '{fullPath}', mas o arquivo não existe.", []);
    }

    /// <summary>
    /// Combina o executável de lançamento do manifesto com a heurística: lojas frequentemente apontam para um
    /// launcher/bootstrapper (ex.: Fortnite → FortniteBootstrapper.exe; Rocket League → Launcher.exe), mas o
    /// processo que renderiza (alvo do PresentMon e da preferência de GPU) é outro.
    /// </summary>
    public static ExecutableResolution ResolveWithManifest(string? manifestExeFullPath, string installDirectory, string sourceLabel, string? gameName, CancellationToken ct = default)
    {
        ExecutableResolution? manifest = manifestExeFullPath is null ? null : FromManifest(manifestExeFullPath, sourceLabel);
        if (manifest is { IsResolved: true } && !IsExcludedName(manifest.Path!)) return manifest;

        var heur = ResolveFromDirectory(installDirectory, gameName, ct: ct);
        if (heur.IsResolved)
        {
            string why = manifest is { IsResolved: true }
                ? $"O manifesto da {sourceLabel} aponta para um launcher ('{Path.GetFileName(manifest.Path)}'); o processo do jogo foi escolhido por heurística. "
                : "";
            return heur with { Reason = why + heur.Reason };
        }
        // Nunca devolve um launcher/desinstalador/redistribuível como "executável do jogo".
        if (manifest is { IsResolved: true } && IsExcludedName(manifest.Path!))
            return ExecutableResolution.None($"A {sourceLabel} só indica '{Path.GetFileName(manifest.Path)}' (launcher/utilitário); escolha o executável do jogo manualmente.");
        return manifest ?? heur;
    }

    internal static string Normalize(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");
}
