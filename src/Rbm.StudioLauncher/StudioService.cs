using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rbm.StudioLauncher;

public sealed record RecentPlace(long PlaceId, DateTime LastOpened, int Times);

/// <summary>Tudo que é local: logs do Studio (recentes/última sessão), processos abertos e o protocolo roblox-studio.</summary>
public static partial class StudioService
{
    private static string LogsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

    [GeneratedRegex(@"[Pp]lace[Ii]d[=: ]+(\d{5,})")]
    private static partial Regex PlaceIdRegex();

    /// <summary>Place aberta em um log do Studio (a última mencionada no arquivo).</summary>
    private static long? PlaceOfLog(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            long? last = null;
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (!line.Contains("laceI", StringComparison.Ordinal)) continue;
                var m = PlaceIdRegex().Match(line);
                if (m.Success) last = long.Parse(m.Groups[1].Value);
            }
            return last;
        }
        catch { return null; }
    }

    private static List<FileInfo> StudioLogs(int days) =>
        Directory.Exists(LogsDir)
            ? new DirectoryInfo(LogsDir).GetFiles("*Studio*.log").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-days)).OrderByDescending(f => f.LastWriteTime).Take(300).ToList()
            : new List<FileInfo>();

    /// <summary>Places abertas no Studio nos últimos dias (mais recente primeiro).</summary>
    public static List<RecentPlace> Recent(int days = 45)
    {
        var map = new Dictionary<long, (DateTime last, int n)>();
        foreach (var f in StudioLogs(days))
        {
            if (PlaceOfLog(f.FullName) is not { } id) continue;
            map[id] = map.TryGetValue(id, out var v) ? (v.last > f.LastWriteTime ? v.last : f.LastWriteTime, v.n + 1) : (f.LastWriteTime, 1);
        }
        return map.Select(kv => new RecentPlace(kv.Key, kv.Value.last, kv.Value.n)).OrderByDescending(r => r.LastOpened).ToList();
    }

    /// <summary>Places da última sessão: logs encerrados até 10 min antes do mais recente.</summary>
    public static List<long> LastSession()
    {
        var logs = StudioLogs(30);
        if (logs.Count == 0) return new();
        var newest = logs[0].LastWriteTime;
        return logs.Where(f => (newest - f.LastWriteTime).TotalMinutes <= 10)
                   .Select(f => PlaceOfLog(f.FullName)).OfType<long>().Distinct().ToList();
    }

    private static readonly Dictionary<string, (long len, long? place)> LogCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// IDs das places abertas agora: cada Studio em execução mantém o próprio log aberto para escrita.
    /// Um log que não pode ser aberto com exclusividade pertence a um Studio vivo. (Títulos de janela não servem:
    /// o Roblox traduz o nome do jogo.)
    /// </summary>
    public static HashSet<long> OpenPlaceIds()
    {
        var set = new HashSet<long>();
        if (RunningStudios() == 0 || !Directory.Exists(LogsDir)) return set;
        foreach (var f in new DirectoryInfo(LogsDir).GetFiles("*Studio*.log").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-2)))
        {
            bool locked;
            try { using var probe = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.None); locked = false; }
            catch (IOException) { locked = true; }
            catch { locked = false; }
            if (!locked) continue;
            if (!LogCache.TryGetValue(f.FullName, out var c) || c.len != f.Length)
                LogCache[f.FullName] = c = (f.Length, PlaceOfLog(f.FullName));
            if (c.place is { } p) set.Add(p);
        }
        return set;
    }

    /// <summary>Títulos das janelas do Studio abertas agora ("Nome do jogo - Roblox Studio").</summary>
    public static List<string> OpenWindowTitles()
    {
        var titles = new List<string>();
        foreach (var p in Process.GetProcessesByName("RobloxStudioBeta"))
        {
            try { if (!string.IsNullOrWhiteSpace(p.MainWindowTitle)) titles.Add(p.MainWindowTitle); } catch { }
            finally { p.Dispose(); }
        }
        return titles;
    }

    public static int RunningStudios()
    {
        var ps = Process.GetProcessesByName("RobloxStudioBeta");
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    public static bool IsStudioInstalled() =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-studio") is not null;

    /// <summary>Abre a place direto no modo de edição (mesmo link do botão "Editar no Studio" do site).</summary>
    public static void OpenPlace(long placeId, long universeId) =>
        Process.Start(new ProcessStartInfo($"roblox-studio:1+launchmode:edit+task:EditPlace+placeId:{placeId}+universeId:{universeId}") { UseShellExecute = true });
}

/// <summary>Preferências e cache em %APPDATA%\RbmPlaces.</summary>
public sealed class LauncherSettings
{
    public string? Username { get; set; }
    public long? UserId { get; set; }
    public List<long> ExtraGroups { get; set; } = new();
    public List<long> Favorites { get; set; } = new();
    public int OpenDelaySeconds { get; set; } = 5;
    public bool OpenTogether { get; set; } = true;
    public string? ProjectsRoot { get; set; }
    public Dictionary<string, long> FolderLinks { get; set; } = new();
    public bool ChatNotifications { get; set; } = true;
    public Dictionary<long, string> UniverseOfPlaceCache { get; set; } = new();

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RbmPlaces");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static LauncherSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath), Opts) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
