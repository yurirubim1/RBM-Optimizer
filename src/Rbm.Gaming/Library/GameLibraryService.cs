using System.Security.Cryptography;
using System.Text;
using Rbm.Core;

namespace Rbm.Gaming.Library;

/// <summary>
/// Detecção da biblioteca de jogos local (sem contas/rede): Steam, Epic, GOG, Xbox, EA app, Ubisoft Connect,
/// Battle.net + entradas manuais. Scanners rodam em paralelo; falhas de um scanner viram avisos, nunca exceções.
/// </summary>
public sealed class GameLibraryService
{
    private readonly IReadOnlyList<IGameLibraryScanner> _scanners;

    public GameLibraryService(IEnumerable<IGameLibraryScanner>? scanners = null) =>
        _scanners = scanners?.ToList() ?? DefaultScanners();

    public static IReadOnlyList<IGameLibraryScanner> DefaultScanners() =>
    [
        new SteamLibraryScanner(), new EpicLibraryScanner(), new GogLibraryScanner(), new XboxLibraryScanner(),
        new UninstallRegistryScanner(GameSource.EaApp), new UninstallRegistryScanner(GameSource.UbisoftConnect), new UninstallRegistryScanner(GameSource.BattleNet),
    ];

    /// <summary>Varre todas as fontes e devolve a lista deduplicada (mais entradas manuais informadas pelo chamador).</summary>
    public async Task<GameLibraryScanResult> ScanAsync(IEnumerable<DetectedGame>? manualEntries = null, CancellationToken ct = default)
    {
        var tasks = _scanners.Select(async s =>
        {
            try { return await s.ScanAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new GameScanOutput(s.Source, [], [$"Falha inesperada: {ex.Message}"], false); }
        }).ToList();
        var outputs = await Task.WhenAll(tasks).ConfigureAwait(false);
        var all = outputs.SelectMany(o => o.Games).Concat(manualEntries ?? []);
        return new GameLibraryScanResult(Deduplicate(all), outputs, DateTimeOffset.Now);
    }

    private static int Priority(GameSource s) => s switch
    {
        GameSource.Steam => 0, GameSource.Epic => 1, GameSource.Gog => 2, GameSource.Xbox => 3,
        GameSource.EaApp => 4, GameSource.UbisoftConnect => 5, GameSource.BattleNet => 6, GameSource.Manual => 7, _ => 8,
    };

    private static string? NormPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p.TrimEnd('\\', '/').ToUpperInvariant(); }
    }

    /// <summary>
    /// Remove duplicatas (mesmo executável ou mesma pasta de instalação). Mantém a entrada da fonte mais
    /// confiável (manifesto de loja &gt; registro &gt; manual), herda o executável de maior confiança e registra
    /// as demais origens em <see cref="DetectedGame.AlsoFoundIn"/>. Função pura.
    /// </summary>
    public static IReadOnlyList<DetectedGame> Deduplicate(IEnumerable<DetectedGame> games)
    {
        var result = new List<DetectedGame>();
        var byExe = new Dictionary<string, int>();
        var byDir = new Dictionary<string, int>();
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games.OrderBy(g => Priority(g.Source)).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? exe = NormPath(g.Executable.Path);
            string? dir = NormPath(g.InstallDirectory);
            int idx = -1;
            if (byId.TryGetValue(g.Id, out var i0)) idx = i0;
            else if (exe is not null && byExe.TryGetValue(exe, out var i1)) idx = i1;
            else if (dir is not null && byDir.TryGetValue(dir, out var i2)) idx = i2;

            if (idx < 0)
            {
                result.Add(g);
                idx = result.Count - 1;
            }
            else
            {
                var keep = result[idx];
                var also = keep.AlsoFoundIn.ToList();
                if (g.Source != keep.Source && !also.Contains(g.Source)) also.Add(g.Source);
                var exeRes = keep.Executable;
                if (g.Executable.IsResolved && (!exeRes.IsResolved || g.Executable.Confidence > exeRes.Confidence)) exeRes = g.Executable;
                result[idx] = keep with
                {
                    Executable = exeRes,
                    AlsoFoundIn = also,
                    InstallDirectory = keep.InstallDirectory ?? g.InstallDirectory,
                    LaunchExecutablePath = keep.LaunchExecutablePath ?? g.LaunchExecutablePath,
                    SizeOnDiskBytes = keep.SizeOnDiskBytes ?? g.SizeOnDiskBytes,
                };
            }
            byId.TryAdd(g.Id, idx);
            if (NormPath(result[idx].Executable.Path) is { } e2) byExe.TryAdd(e2, idx);
            if (exe is not null) byExe.TryAdd(exe, idx);
            if (dir is not null) byDir.TryAdd(dir, idx);
        }
        return result.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Cria uma entrada manual a partir de um executável escolhido pelo usuário (validado).</summary>
    public static OperationResult<DetectedGame> CreateManualEntry(string exePath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return OperationResult<DetectedGame>.Fail("Informe o caminho do executável.");
        string full;
        try { full = Path.GetFullPath(exePath.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return OperationResult<DetectedGame>.Fail($"Caminho inválido: {ex.Message}"); }
        if (!Path.IsPathFullyQualified(full)) return OperationResult<DetectedGame>.Fail("O caminho precisa ser absoluto.");
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return OperationResult<DetectedGame>.Fail("O arquivo precisa ser um .exe.");
        if (!File.Exists(full)) return OperationResult<DetectedGame>.Fail("Arquivo não encontrado.");
        string id = "manual:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16];
        string name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(full) : displayName.Trim();
        var exe = new ExecutableResolution(full, Confidence.High, "Escolhido manualmente pelo usuário.", []);
        return OperationResult<DetectedGame>.Ok(new DetectedGame(id, name, GameSource.Manual, null, Path.GetDirectoryName(full), exe, full, null, [], []));
    }
}
