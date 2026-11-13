using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rbm.StudioLauncher;

/// <summary>Pastas de projetos e janelas do VS Code.</summary>
public static partial class VsCodeService
{
    public static string? CodeExe =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        }.FirstOrDefault(File.Exists);

    /// <summary>Pasta padrão dos projetos: Documentos\Roblox (se existir).</summary>
    public static string? DefaultProjectsRoot()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Roblox");
        return Directory.Exists(p) ? p : null;
    }

    public static List<DirectoryInfo> ProjectFolders(string root) =>
        Directory.Exists(root)
            ? new DirectoryInfo(root).GetDirectories().Where(d => !d.Name.StartsWith('.') && (d.Attributes & FileAttributes.Hidden) == 0).OrderByDescending(d => d.LastWriteTime).ToList()
            : new List<DirectoryInfo>();

    /// <summary>Pastas abertas na última sessão do VS Code (storage.json → windowsState).</summary>
    public static List<string> LastSessionFolders()
    {
        var list = new List<string>();
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("windowsState", out var ws)) return list;
            void Add(JsonElement w) { if (w.TryGetProperty("folder", out var f) && f.GetString() is { } uri && UriToPath(uri) is { } p) list.Add(p); }
            if (ws.TryGetProperty("lastActiveWindow", out var la)) Add(la);
            if (ws.TryGetProperty("openedWindows", out var ow)) foreach (var w in ow.EnumerateArray()) Add(w);
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? UriToPath(string uri)
    {
        try { return Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? Path.GetFullPath(u.LocalPath).TrimEnd('\\') : null; }
        catch { return null; }
    }

    /// <summary>Nomes das pastas abertas agora (títulos "… - Pasta - Visual Studio Code").</summary>
    public static HashSet<string> OpenFolderNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pids = Process.GetProcessesByName("Code").Select(p => { var id = p.Id; p.Dispose(); return (uint)id; }).ToHashSet();
        if (pids.Count == 0) return names;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            const string suffix = " - Visual Studio Code";
            if (!title.EndsWith(suffix)) return true;
            var parts = title[..^suffix.Length].Split(" - ");
            names.Add(parts[^1].Replace(" [Administrator]", "").Trim());
            return true;
        }, IntPtr.Zero);
        return names;
    }

    /// <summary>Traz para frente a janela do VS Code daquela pasta. Retorna false se não estiver aberta.</summary>
    public static bool FocusFolder(string folderName)
    {
        var pids = Process.GetProcessesByName("Code").Select(p => { var id = p.Id; p.Dispose(); return (uint)id; }).ToHashSet();
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity);
            var t = sb.ToString();
            if (t.EndsWith(" - Visual Studio Code") && (t.Contains(" - " + folderName + " - ") || t.StartsWith(folderName + " - "))) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        if (IsIconic(found)) ShowWindow(found, 9);
        SetForegroundWindow(found);
        return true;
    }

    /// <summary>Abre a pasta numa nova janela do VS Code (o Claude Code restaura a conversa daquele projeto).</summary>
    public static void OpenFolder(string folder)
    {
        var exe = CodeExe ?? throw new InvalidOperationException("VS Code não encontrado neste PC.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add("--new-window");
        psi.ArgumentList.Add(folder);
        Process.Start(psi)?.Dispose();
    }

    // ---------------- Correspondência pasta ↔ jogo ----------------

    [GeneratedRegex(@"[^a-z0-9 ]")]
    private static partial Regex NonAlnum();

    private static readonly HashSet<string> Stop = new() { "a", "an", "the", "for", "to", "your", "you", "of", "and", "with", "vs", "1", "dev", "o", "de", "da", "do" };

    public static List<string> Tokens(string s)
    {
        var norm = NonAlnum().Replace(s.ToLowerInvariant().Replace('-', ' ').Replace('_', ' '), " ");
        return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Stop.Contains(t)).ToList();
    }

    /// <summary>Semelhança entre o nome da pasta e do jogo (0–1), tolerante a prefixos ("basket" ≈ "basketball").</summary>
    public static double Similarity(string folder, string game)
    {
        var a = Tokens(folder); var b = Tokens(game);
        if (a.Count == 0 || b.Count == 0) return 0;
        static bool Same(string x, string y) => x == y || (x.Length >= 4 && y.Length >= 4 && (x.StartsWith(y) || y.StartsWith(x))) || (x.TrimEnd('s') == y.TrimEnd('s'));
        int hits = a.Count(x => b.Any(y => Same(x, y)));
        return hits / (double)Math.Max(a.Count, b.Count);
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
}
