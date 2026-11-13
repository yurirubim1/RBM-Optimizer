using Microsoft.Win32;

namespace Rbm.Processes;

/// <summary>Categoria de autostart (visão estilo Autoruns).</summary>
public enum AutorunCategory { Winlogon, ShellExtension, BootDriver }

/// <summary>Entrada somente leitura de outros locais de autostart.</summary>
public sealed record AutorunEntry(
    AutorunCategory Category,
    string Location,
    string Name,
    string? Command,
    string? ImagePath,
    bool TargetExists,
    PublisherInfo? Publisher,
    // true quando o valor difere do padrão do Windows (ex.: Winlogon Shell ≠ explorer.exe).
    bool IsDeviation,
    AutostartRecommendation Recommendation,
    string? Note)
{
    public bool IsVerifiedMicrosoft => Publisher?.IsVerifiedMicrosoft == true;
}

public sealed record AutorunScanOptions
{
    public bool IncludeWinlogon { get; init; } = true;
    public bool IncludeShellExtensions { get; init; } = true;
    public bool IncludeBootDrivers { get; init; } = true;
    /// <summary>Necessário para o filtro "ocultar entradas da Microsoft".</summary>
    public bool VerifySignatures { get; init; } = true;
}

/// <summary>
/// Leitura (nunca escrita) de locais adicionais: Winlogon Shell/Userinit, extensões de menu de contexto
/// (HKCR\*, Directory, Directory\Background, Folder, Drive → CLSID → InprocServer32) e drivers de boot/sistema
/// (serviços tipo kernel/sistema de arquivos com Start 0/1).
/// </summary>
public sealed class AutorunsScanner
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private static readonly string[] ShellExRoots = [@"*", "Directory", @"Directory\Background", "Folder", "Drive"];

    public async Task<IReadOnlyList<AutorunEntry>> ScanAsync(AutorunScanOptions? options = null, CancellationToken ct = default)
    {
        options ??= new AutorunScanOptions();
        var raw = await Task.Run(() =>
        {
            var l = new List<RawEntry>();
            if (options.IncludeWinlogon) l.AddRange(Winlogon());
            ct.ThrowIfCancellationRequested();
            if (options.IncludeShellExtensions) l.AddRange(ShellExtensions());
            ct.ThrowIfCancellationRequested();
            if (options.IncludeBootDrivers) l.AddRange(BootDrivers());
            return l;
        }, ct).ConfigureAwait(false);

        var meta = await FileMetadataCache.GetManyAsync(raw.Select(r => r.Image).Where(p => p is not null).Cast<string>(),
            options.VerifySignatures, 2, ct).ConfigureAwait(false);
        return raw.Select(r =>
        {
            PublisherInfo? pub = r.Image is not null && meta.TryGetValue(r.Image, out var m) ? m : null;
            bool exists = r.Image is not null && (pub?.Exists ?? File.Exists(r.Image));
            var kind = r.Category switch { AutorunCategory.BootDriver => AutostartKind.Driver, AutorunCategory.Winlogon => AutostartKind.Winlogon, _ => AutostartKind.ShellExtension };
            var rec = AutostartAdvisor.Recommend(new AutostartFacts(kind, r.Name, r.Command, r.Image, exists, pub));
            if (r.Deviation && rec.Kind != AutostartRecommendationKind.InvestigateUnsigned)
                rec = new AutostartRecommendation(AutostartRecommendationKind.InvestigateUnsigned,
                    "Valor diferente do padrão do Windows neste local sensível (usado por malware para persistência). Investigue. " + rec.Reason);
            return new AutorunEntry(r.Category, r.Location, r.Name, r.Command, r.Image, exists, pub, r.Deviation, rec, r.Note);
        }).ToList();
    }

    /// <summary>Filtro "Ocultar entradas da Microsoft" (só remove as com assinatura Microsoft VERIFICADA).</summary>
    public static IEnumerable<AutorunEntry> HideVerifiedMicrosoft(IEnumerable<AutorunEntry> entries) => entries.Where(e => !e.IsVerifiedMicrosoft);

    public static IEnumerable<StartupEntry> HideVerifiedMicrosoft(IEnumerable<StartupEntry> entries) => entries.Where(e => e.Publisher?.IsVerifiedMicrosoft != true);
    public static IEnumerable<ServiceEntry> HideVerifiedMicrosoft(IEnumerable<ServiceEntry> entries) => entries.Where(e => e.IsMicrosoft != true);
    public static IEnumerable<ScheduledTaskEntry> HideVerifiedMicrosoft(IEnumerable<ScheduledTaskEntry> entries) =>
        entries.Where(e => !(e.Publisher?.IsVerifiedMicrosoft == true || (e.Publisher is null && e.IsMicrosoftFolder && e.Actions.All(a => a.Kind != "Exec"))));

    private sealed record RawEntry(AutorunCategory Category, string Location, string Name, string? Command, string? Image, bool Deviation, string? Note);

    private static IEnumerable<RawEntry> Winlogon()
    {
        var list = new List<RawEntry>();
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var (hive, label) in new[] { (RegistryHive.LocalMachine, "HKLM"), (RegistryHive.CurrentUser, "HKCU") })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.OpenSubKey(WinlogonKey);
                if (k is null) continue;
                if (k.GetValue("Shell") is string shell && shell.Length > 0)
                {
                    bool dev = !shell.Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
                    var p = CommandLineParser.Parse(shell);
                    list.Add(new RawEntry(AutorunCategory.Winlogon, $@"{label}\{WinlogonKey}", "Shell", shell, p.EffectivePath, dev,
                        dev ? "Padrão: explorer.exe" : null));
                }
                if (k.GetValue("Userinit") is string ui && ui.Length > 0)
                {
                    foreach (var part in ui.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var p = CommandLineParser.Parse(part);
                        bool dev = p.ExecutablePath is null || !p.ExecutablePath.Equals(Path.Combine(sys32, "userinit.exe"), StringComparison.OrdinalIgnoreCase);
                        list.Add(new RawEntry(AutorunCategory.Winlogon, $@"{label}\{WinlogonKey}", "Userinit", part, p.EffectivePath, dev,
                            dev ? @"Padrão: C:\Windows\system32\userinit.exe," : null));
                    }
                }
            }
            catch { }
        }
        return list;
    }

    private static IEnumerable<RawEntry> ShellExtensions()
    {
        var list = new List<RawEntry>();
        foreach (var rootName in ShellExRoots)
        {
            string loc = $@"HKCR\{rootName}\shellex\ContextMenuHandlers";
            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey($@"{rootName}\shellex\ContextMenuHandlers");
                if (k is null) continue;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var s = k.OpenSubKey(sub);
                    string? clsid = s?.GetValue(null) as string;
                    if (string.IsNullOrWhiteSpace(clsid) || !Guid.TryParse(clsid.Trim('{', '}'), out _))
                        clsid = Guid.TryParse(sub.Trim('{', '}'), out _) ? sub : clsid;
                    if (string.IsNullOrWhiteSpace(clsid) || !Guid.TryParse(clsid.Trim().Trim('{', '}'), out var g))
                    {
                        list.Add(new RawEntry(AutorunCategory.ShellExtension, loc, sub, clsid, null, false, "CLSID ausente ou inválido."));
                        continue;
                    }
                    string braced = g.ToString("B");
                    string? dll = ComRegistry.ResolveInprocServer(braced);
                    string name = ComRegistry.DisplayName(braced) is { Length: > 0 } dn ? $"{sub} ({dn})" : sub;
                    list.Add(new RawEntry(AutorunCategory.ShellExtension, loc, name, braced, dll, false,
                        dll is null ? "CLSID sem InprocServer32 registrado." : null));
                }
            }
            catch { }
        }
        return list;
    }

    private static IEnumerable<RawEntry> BootDrivers()
    {
        var list = new List<RawEntry>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return list;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var s = services.OpenSubKey(name);
                    if (s is null) continue;
                    if (s.GetValue("Type") is not int type || (type & 0x3) == 0) continue; // 1 kernel, 2 FS
                    if (s.GetValue("Start") is not int start || start > 1) continue;      // 0 boot, 1 system
                    string? img = s.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                    string path = string.IsNullOrWhiteSpace(img) ? $@"System32\drivers\{name}.sys" : img;
                    string resolved = CommandLineParser.NormalizeImagePath(path);
                    string display = ServiceManager.LoadIndirect(s.GetValue("DisplayName")?.ToString()) ?? name;
                    // DisplayName de drivers OEM pode vir como "@oem12.inf,%x%;Nome legível".
                    int semi = display.LastIndexOf(';');
                    if (display.StartsWith('@') && semi > 0) display = display[(semi + 1)..];
                    list.Add(new RawEntry(AutorunCategory.BootDriver, $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
                        display == name ? name : $"{name} ({display})", img, resolved, false,
                        $"Início: {(start == 0 ? "Boot" : "Sistema")}; tipo: {((type & 0x2) != 0 ? "sistema de arquivos" : "kernel")}."));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}

/// <summary>Consultas a HKCR\CLSID (somente leitura).</summary>
public static class ComRegistry
{
    public static string? ResolveInprocServer(string? clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid) || !Guid.TryParse(clsid.Trim('{', '}'), out var g)) return null;
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{g:B}\InprocServer32") ?? Registry.ClassesRoot.OpenSubKey($@"CLSID\{g:B}\LocalServer32");
            var v = k?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(v)) return null;
            return CommandLineParser.Parse(v.Contains('"') ? v : $"\"{v.Trim()}\"").ExecutablePath;
        }
        catch { return null; }
    }

    public static string? DisplayName(string clsid)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
            return k?.GetValue(null) as string;
        }
        catch { return null; }
    }
}
