namespace Rbm.Processes;

/// <summary>Comando decomposto: executável resolvido, argumentos e módulo hospedado (DLL de rundll32/regsvr32).</summary>
public sealed record ParsedCommand(string? ExecutablePath, string Arguments, string? HostedModulePath, bool ExecutableExists)
{
    /// <summary>Arquivo que efetivamente representa a entrada (a DLL, quando hospedada; senão o executável).</summary>
    public string? EffectivePath => HostedModulePath ?? ExecutablePath;
}

/// <summary>
/// Extrai o executável de linhas de comando de autostart (Run, serviços, tarefas), no estilo do Autoruns:
/// aspas, caminhos com espaços sem aspas (regra do CreateProcess), variáveis de ambiente, prefixos de driver
/// (\SystemRoot\, \??\, System32\...) e hosts (rundll32/regsvr32). Funções puras com <c>fileExists</c> injetável.
/// </summary>
public static class CommandLineParser
{
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32Dir = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string SysWow64Dir = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

    public static ParsedCommand Parse(string? command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= SafeFileExists;
        if (string.IsNullOrWhiteSpace(command)) return new ParsedCommand(null, "", null, false);
        string s = NormalizeImagePath(command);

        string exe, args;
        if (s.StartsWith('"'))
        {
            int close = s.IndexOf('"', 1);
            if (close < 0) { exe = s[1..]; args = ""; }
            else { exe = s[1..close]; args = s[(close + 1)..].Trim(); }
        }
        else
        {
            (exe, args) = SplitUnquoted(s, fileExists);
        }

        exe = exe.Trim();
        string? resolved = ResolveExecutable(exe, fileExists);
        string? hosted = null;
        if (resolved is not null)
        {
            string file = Path.GetFileName(resolved);
            if (file.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)) hosted = HostedDllFromRundll(args, fileExists);
            else if (file.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase)) hosted = HostedDllFromRegsvr(args, fileExists);
        }
        bool exists = resolved is not null && fileExists(resolved);
        return new ParsedCommand(resolved ?? (exe.Length > 0 ? exe : null), args, hosted, exists);
    }

    /// <summary>
    /// Normaliza caminhos no formato usado por serviços/drivers: expande variáveis, remove "\??\",
    /// converte "\SystemRoot\" e "System32\..." relativos ao Windows.
    /// </summary>
    public static string NormalizeImagePath(string raw)
    {
        string s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WindowsDir, s[12..]);
        else if (s.StartsWith(@"SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WindowsDir, s[11..]);
        else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || s.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(WindowsDir, s);
        // "C:/Program Files/..." → barras invertidas (só para caminhos absolutos de unidade).
        if (s.Length > 2 && s[1] == ':' && s[2] == '/') s = s.Replace('/', '\\');
        return s;
    }

    /// <summary>Resolve um nome/caminho de executável: absoluto, ou procurado em System32, Windows e SysWOW64 (com ".exe" implícito).</summary>
    public static string? ResolveExecutable(string exe, Func<string, bool>? fileExists = null)
    {
        fileExists ??= SafeFileExists;
        if (string.IsNullOrWhiteSpace(exe)) return null;
        exe = NormalizeImagePath(exe.Trim().Trim('"'));
        bool hasExt = Path.HasExtension(exe);
        if (Path.IsPathFullyQualified(exe))
        {
            if (fileExists(exe)) return exe;
            if (!hasExt && fileExists(exe + ".exe")) return exe + ".exe";
            return exe;
        }
        if (exe.Contains('\\') || exe.Contains('/')) return exe; // relativo com diretório: não há como resolver com segurança
        foreach (var dir in new[] { System32Dir, WindowsDir, SysWow64Dir })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var c = Path.Combine(dir, exe);
            if (fileExists(c)) return c;
            if (!hasExt && fileExists(c + ".exe")) return c + ".exe";
        }
        return exe;
    }

    // Regra do CreateProcess para caminhos sem aspas: tenta prefixos crescentes até achar um arquivo.
    private static (string exe, string args) SplitUnquoted(string s, Func<string, bool> fileExists)
    {
        int idx = 0;
        while (true)
        {
            int space = s.IndexOf(' ', idx);
            string candidate = space < 0 ? s : s[..space];
            if (Path.IsPathFullyQualified(candidate) && (fileExists(candidate) || (!Path.HasExtension(candidate) && fileExists(candidate + ".exe"))))
                return (candidate, space < 0 ? "" : s[(space + 1)..].Trim());
            if (space < 0) break;
            idx = space + 1;
        }
        int first = s.IndexOf(' ');
        return first < 0 ? (s, "") : (s[..first], s[(first + 1)..].Trim());
    }

    private static string? HostedDllFromRundll(string args, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        string a = args.Trim();
        // Ignora opções antes da DLL (ex.: "rundll32.exe /d acproxy.dll,Entry").
        while ((a.StartsWith('/') || a.StartsWith('-')) && a.IndexOf(' ') is int sp && sp > 0) a = a[(sp + 1)..].TrimStart();
        if (a.StartsWith('/') || a.StartsWith('-')) return null;
        string dll;
        if (a.StartsWith('"'))
        {
            int close = a.IndexOf('"', 1);
            dll = close < 0 ? a[1..] : a[1..close];
        }
        else
        {
            int comma = a.IndexOf(',');
            int space = a.IndexOf(' ');
            int end = comma >= 0 && (space < 0 || comma < space) ? comma : space;
            dll = end < 0 ? a : a[..end];
        }
        dll = dll.TrimEnd(',').Trim();
        return dll.Length == 0 ? null : ResolveExecutable(dll, fileExists);
    }

    private static string? HostedDllFromRegsvr(string args, Func<string, bool> fileExists)
    {
        foreach (var tok in Tokenize(args))
            if (!tok.StartsWith('/') && !tok.StartsWith('-')) return ResolveExecutable(tok, fileExists);
        return null;
    }

    /// <summary>Divide argumentos respeitando aspas (sem interpretar escapes).</summary>
    public static IReadOnlyList<string> Tokenize(string args)
    {
        var list = new List<string>();
        int i = 0;
        while (i < args.Length)
        {
            while (i < args.Length && char.IsWhiteSpace(args[i])) i++;
            if (i >= args.Length) break;
            if (args[i] == '"')
            {
                int close = args.IndexOf('"', i + 1);
                if (close < 0) { list.Add(args[(i + 1)..]); break; }
                list.Add(args[(i + 1)..close]);
                i = close + 1;
            }
            else
            {
                int start = i;
                while (i < args.Length && !char.IsWhiteSpace(args[i])) i++;
                list.Add(args[start..i]);
            }
        }
        return list;
    }

    private static bool SafeFileExists(string p)
    {
        try { return File.Exists(p); } catch { return false; }
    }
}
