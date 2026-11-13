using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.StudioLauncher;

public enum ChatState { Working, Done, WaitingYou, Idle }

public sealed record ChatMessage(bool FromUser, string Text, DateTime? At);

/// <summary>Uma conversa do Claude Code (um arquivo .jsonl em ~/.claude/projects/&lt;projeto&gt;/).</summary>
public sealed partial class ChatSession : ObservableObject
{
    public required string File { get; init; }
    public required string SessionId { get; init; }
    [ObservableProperty] private string _project = "";
    [ObservableProperty] private string _cwd = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _lastUser = "";
    [ObservableProperty] private string _lastAssistant = "";
    [ObservableProperty] private ChatState _state = ChatState.Idle;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _ago = "";
    [ObservableProperty] private bool _unread;
    [ObservableProperty] private bool _windowOpen;
    public DateTime LastWrite { get; set; }
    public long LastLength { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    /// <summary>Tipo e stop_reason da última entrada user/assistant.</summary>
    public (string Type, string Stop) LastEntry { get; set; }
}

/// <summary>
/// Lê as conversas do Claude Code direto dos arquivos que ele grava (somente leitura) e deduz o estado:
/// trabalhando (arquivo mudando / ferramenta em execução), terminou (resposta final) ou aguardando você.
/// </summary>
public static class ClaudeSessions
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>Arquivos de conversas principais (não subagentes) alterados nos últimos <paramref name="days"/> dias.</summary>
    public static List<FileInfo> RecentFiles(int days = 3) =>
        Directory.Exists(Root)
            ? new DirectoryInfo(Root).GetDirectories()
                .SelectMany(d => d.GetFiles("*.jsonl", SearchOption.TopDirectoryOnly))
                .Where(f => f.LastWriteTime > DateTime.Now.AddDays(-days) && !f.Name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime).ToList()
            : new List<FileInfo>();

    /// <summary>Atualiza a sessão a partir do arquivo (só relê quando o arquivo mudou).</summary>
    public static bool Update(ChatSession s, FileInfo f)
    {
        f.Refresh();
        bool changed = f.Length != s.LastLength || f.LastWriteTime != s.LastWrite;
        if (changed)
        {
            s.LastLength = f.Length; s.LastWrite = f.LastWriteTime;
            Parse(s, f);
        }
        var prev = s.State;
        s.State = Infer(s);
        s.StateText = s.State switch
        {
            ChatState.Working => "Trabalhando…",
            ChatState.Done => "Terminou",
            ChatState.WaitingYou => "Esperando você",
            _ => "Parado",
        };
        s.Ago = Ago(s.LastWrite);
        return changed || prev != s.State;
    }

    private static ChatState Infer(ChatSession s)
    {
        var age = DateTime.Now - s.LastWrite;
        if (age.TotalSeconds < 6) return ChatState.Working;
        var last = s.LastEntry;
        return last switch
        {
            ("assistant", "end_turn") => ChatState.Done,
            ("assistant", "tool_use") => age.TotalSeconds > 25 ? ChatState.WaitingYou : ChatState.Working, // ferramenta parada há muito: provável pedido de aprovação
            ("user", _) => age.TotalMinutes < 3 ? ChatState.Working : ChatState.Idle,
            _ => ChatState.Idle,
        };
    }

    private static void Parse(ChatSession s, FileInfo f)
    {
        // Lê o começo (título/pasta) e o fim (estado e últimas mensagens) — arquivos podem ter vários MB.
        var head = ReadSlice(f.FullName, 0, 256 * 1024);
        var tail = f.Length > 768 * 1024 ? ReadSlice(f.FullName, f.Length - 768 * 1024, 768 * 1024) : head.Length >= f.Length ? head : ReadSlice(f.FullName, 0, (int)f.Length);
        if (string.IsNullOrEmpty(s.Title))
            foreach (var line in head.Split('\n'))
            {
                if (!TryParse(line, out var j)) continue;
                if (string.IsNullOrEmpty(s.Cwd) && j.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } c) { s.Cwd = c; s.Project = ProjectName(c); }
                if (Str(j, "type") == "user" && UserText(j) is { Length: > 0 } t) { s.Title = Trim(t, 90); break; }
            }

        var msgs = new List<ChatMessage>();
        (string type, string stop) last = ("", "");
        foreach (var line in tail.Split('\n'))
        {
            if (!TryParse(line, out var j)) continue;
            var type = Str(j, "type");
            if (type is not ("user" or "assistant")) continue;
            if (j.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) continue;
            if (string.IsNullOrEmpty(s.Cwd) && Str(j, "cwd") is { Length: > 0 } c2) { s.Cwd = c2; s.Project = ProjectName(c2); }
            var stop = j.TryGetProperty("message", out var m) && m.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";
            last = (type, stop);
            DateTime? at = j.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt) ? dt.ToLocalTime() : null;
            if (type == "user" && UserText(j) is { Length: > 0 } ut) msgs.Add(new ChatMessage(true, ut, at));
            if (type == "assistant" && AssistantText(j) is { Length: > 0 } at2) msgs.Add(new ChatMessage(false, at2, at));
        }
        s.LastEntry = last;
        if (msgs.Count > 0) s.Messages = msgs.TakeLast(60).ToList();
        s.LastUser = Trim(s.Messages.LastOrDefault(x => x.FromUser)?.Text ?? "", 160);
        s.LastAssistant = Trim(s.Messages.LastOrDefault(x => !x.FromUser)?.Text ?? "", 220);
        if (string.IsNullOrEmpty(s.Project)) s.Project = Path.GetFileName(Path.GetDirectoryName(f.FullName) ?? "");
    }

    public static string ProjectName(string cwd)
    {
        // Pasta do projeto = primeira pasta abaixo de Documentos\Roblox (ou a própria pasta).
        var parts = cwd.TrimEnd('\\', '/').Split('\\', '/');
        int i = Array.FindIndex(parts, p => p.Equals("Roblox", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : parts[^1];
    }

    private static string UserText(JsonElement j)
    {
        if (!j.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return "";
        string text = c.ValueKind == JsonValueKind.String ? c.GetString() ?? ""
            : string.Join("\n", c.EnumerateArray().Where(x => Str(x, "type") == "text").Select(x => Str(x, "text")));
        text = text.Trim();
        // Ignora lembretes do sistema, saídas de comandos e resultados de ferramentas.
        if (text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal) || text.StartsWith("Another Claude session", StringComparison.Ordinal)
            || text.Contains("<cross-session-message", StringComparison.Ordinal) || text.Contains("[SYSTEM NOTIFICATION", StringComparison.Ordinal)
            || text.StartsWith("[Request interrupted", StringComparison.Ordinal) || text.StartsWith("[Image:", StringComparison.Ordinal)) return "";
        return text;
    }

    private static string AssistantText(JsonElement j)
    {
        if (!j.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", c.EnumerateArray().Where(x => Str(x, "type") == "text").Select(x => Str(x, "text"))).Trim();
    }

    private static string Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool TryParse(string line, out JsonElement e)
    {
        e = default;
        line = line.Trim();
        if (line.Length < 2 || line[0] != '{') return false;
        try { e = JsonDocument.Parse(line).RootElement; return true; } catch { return false; }
    }

    private static string ReadSlice(string path, long offset, int count)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(Math.Max(0, offset), SeekOrigin.Begin);
            var buf = new byte[Math.Min(count, fs.Length - fs.Position)];
            int read = fs.Read(buf, 0, buf.Length);
            var s = Encoding.UTF8.GetString(buf, 0, read);
            return offset > 0 && s.IndexOf('\n') is var nl and >= 0 ? s[(nl + 1)..] : s; // descarta linha cortada
        }
        catch { return ""; }
    }

    private static string Trim(string s, int max)
    {
        s = s.Replace("\r", "").Replace('\n', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    public static string Ago(DateTime d)
    {
        var s = DateTime.Now - d;
        if (s.TotalSeconds < 60) return "agora";
        if (s.TotalHours < 1) return $"há {(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"há {(int)s.TotalHours} h";
        return s.TotalDays < 2 ? "ontem" : $"há {(int)s.TotalDays} dias";
    }
}
