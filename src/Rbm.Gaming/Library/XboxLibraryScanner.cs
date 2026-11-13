using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Rbm.Gaming.Library;

/// <summary>Dados de um MicrosoftGame.config (jogos PC do Xbox app / Microsoft Store via GDK).</summary>
public sealed record MicrosoftGameConfig(string? IdentityName, string? DisplayName, string? PublisherDisplayName, IReadOnlyList<string> Executables, string? StoreId);

/// <summary>
/// Xbox / Microsoft Store (melhor esforço): em cada unidade fixa, lê o arquivo ".GamingRoot" (formato "RGBX" +
/// contagem uint32 + caminhos UTF-16 terminados em nulo, relativos à raiz) ou usa "\XboxGames"; cada subpasta com
/// Content\MicrosoftGame.config é um jogo. Jogos UWP antigos (WindowsApps) não são listados.
/// </summary>
public sealed class XboxLibraryScanner : IGameLibraryScanner
{
    private readonly IReadOnlyList<string>? _rootsOverride;

    /// <param name="rootsOverride">Para testes: pastas-raiz de jogos a varrer (ignora as unidades).</param>
    public XboxLibraryScanner(IReadOnlyList<string>? rootsOverride = null) => _rootsOverride = rootsOverride;

    public GameSource Source => GameSource.Xbox;

    /// <summary>Faz o parse do conteúdo binário de um ".GamingRoot". Retorna caminhos relativos à raiz da unidade.</summary>
    public static IReadOnlyList<string> ParseGamingRoot(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        if (data.Length < 8 || data[0] != (byte)'R' || data[1] != (byte)'G' || data[2] != (byte)'B' || data[3] != (byte)'X') return list;
        uint count = BitConverter.ToUInt32(data.Slice(4, 4));
        int pos = 8;
        for (uint i = 0; i < count && i < 64 && pos + 1 < data.Length; i++)
        {
            int start = pos;
            while (pos + 1 < data.Length && (data[pos] != 0 || data[pos + 1] != 0)) pos += 2;
            string s = Encoding.Unicode.GetString(data[start..pos]);
            pos += 2;
            s = s.Trim().TrimStart('\\', '/');
            if (s.Length > 0 && s.IndexOfAny(Path.GetInvalidPathChars()) < 0 && !s.Contains("..")) list.Add(s);
        }
        return list;
    }

    /// <summary>Faz o parse de um MicrosoftGame.config (XML). Retorna null se não for um documento &lt;Game&gt;.</summary>
    public static MicrosoftGameConfig? ParseMicrosoftGameConfig(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var game = doc.Root;
        if (game is null || game.Name.LocalName != "Game") return null;
        XElement? El(XElement parent, string name) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        string? identity = El(game, "Identity")?.Attribute("Name")?.Value;
        var visuals = El(game, "ShellVisuals");
        string? display = visuals?.Attribute("DefaultDisplayName")?.Value;
        if (display is not null && display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = null;
        var exes = new List<string>();
        var exeList = El(game, "ExecutableList");
        if (exeList is not null)
        {
            // Preferência: Id="Game" primeiro.
            foreach (var e in exeList.Elements().Where(e => e.Name.LocalName == "Executable")
                         .OrderByDescending(e => string.Equals(e.Attribute("Id")?.Value, "Game", StringComparison.OrdinalIgnoreCase)))
            {
                var n = e.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(n) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !n.Contains("..")) exes.Add(n.Replace('/', '\\'));
            }
        }
        return new MicrosoftGameConfig(identity, display, visuals?.Attribute("PublisherDisplayName")?.Value, exes, El(game, "StoreId")?.Value);
    }

    public Task<GameScanOutput> ScanAsync(CancellationToken ct = default) => Task.Run(() => Scan(ct), ct);

    private IEnumerable<string> Roots(List<string> warnings)
    {
        if (_rootsOverride is not null) { foreach (var r in _rootsOverride) yield return r; yield break; }
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.DriveType == DriveType.Fixed && drive.IsReady; } catch (IOException) { ready = false; }
            if (!ready) continue;
            string rootDir = drive.RootDirectory.FullName;
            var found = new List<string>();
            string gr = Path.Combine(rootDir, ".GamingRoot");
            try
            {
                if (File.Exists(gr))
                {
                    var bytes = File.ReadAllBytes(gr);
                    if (bytes.Length <= 64 * 1024)
                        foreach (var rel in ParseGamingRoot(bytes)) found.Add(Path.Combine(rootDir, rel));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{gr}: {ex.Message}"); }
            string def = Path.Combine(rootDir, "XboxGames");
            if (!found.Contains(def, StringComparer.OrdinalIgnoreCase)) found.Add(def);
            foreach (var f in found) yield return f;
        }
    }

    private GameScanOutput Scan(CancellationToken ct)
    {
        var warnings = new List<string>();
        var games = new List<DetectedGame>();
        bool any = false;
        foreach (var root in Roots(warnings).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            any = true;
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{root}: {ex.Message}"); continue; }
            foreach (var dir in dirs)
            {
                ct.ThrowIfCancellationRequested();
                string content = Path.Combine(dir, "Content");
                string cfgPath = Path.Combine(content, "MicrosoftGame.config");
                MicrosoftGameConfig? cfg = null;
                try { if (File.Exists(cfgPath)) cfg = ParseMicrosoftGameConfig(File.ReadAllText(cfgPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
                { warnings.Add($"{cfgPath}: {ex.Message} (a pasta Content pode ser protegida pelo sistema)."); }
                if (cfg is null && !Directory.Exists(content)) continue;

                string name = cfg?.DisplayName ?? Path.GetFileName(dir);
                var notes = new List<string> { "Detecção Xbox/Microsoft Store é de melhor esforço." };
                string installDir = Directory.Exists(content) ? content : dir;
                ExecutableResolution exe;
                if (cfg is { Executables.Count: > 0 })
                {
                    var first = Path.Combine(content, cfg.Executables[0]);
                    exe = ExecutableResolver.ResolveWithManifest(first, installDir, "Xbox (MicrosoftGame.config)", name, ct);
                }
                else exe = ExecutableResolver.ResolveFromDirectory(installDir, name, ct: ct);
                string sid = cfg?.IdentityName ?? Path.GetFileName(dir);
                string? launch = cfg is { Executables.Count: > 0 } ? Path.Combine(content, cfg.Executables[0]) : null;
                games.Add(new DetectedGame($"xbox:{sid}", name, GameSource.Xbox, sid, installDir, exe,
                    launch is not null && File.Exists(launch) ? launch : null, null, [], notes));
            }
        }
        return new GameScanOutput(Source, games, warnings, any);
    }
}
