using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Processes.Agent;
using Microsoft.Win32;

namespace Rbm.Processes;

/// <summary>Locais de inicialização com o logon (estilo Autoruns "Logon").</summary>
public enum StartupLocation
{
    HkcuRun, HkcuRunOnce, HklmRun, HklmRunOnce, HklmRun32, HklmRunOnce32,
    HkcuPoliciesRun, HklmPoliciesRun, UserStartupFolder, CommonStartupFolder,
}

public enum StartupEntryState { Enabled, Disabled, NotApplicable }

public sealed record StartupEntry(
    string Id,
    StartupLocation Location,
    string LocationDisplay,
    string Name,
    string? Command,
    string? ExecutablePath,
    string? Arguments,
    string? HostedModulePath,
    bool TargetExists,
    StartupEntryState State,
    DateTime? DisabledAtUtc,
    bool CanToggle,
    bool ToggleRequiresElevation,
    PublisherInfo? Publisher,
    AutostartRecommendation Recommendation,
    string? Note);

/// <summary>
/// Entradas de inicialização: Run/RunOnce (HKCU, HKLM, HKLM WOW6432Node), Policies\Explorer\Run e pastas Startup.
/// Habilitar/desabilitar usa SOMENTE o mecanismo StartupApproved do Gerenciador de Tarefas (reversível; nada é apagado).
/// HKCU: sem elevação (<see cref="SetEnabled"/>). HKLM / pasta comum: <see cref="SetEnabledElevated"/> (via RBM.Agent).
/// RunOnce e Policies não são alternáveis (o Windows não consulta StartupApproved para eles).
/// </summary>
public sealed class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnce32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PoliciesRunPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";

    private sealed record LocationInfo(StartupLocation Location, bool Machine, string? KeyPath, Environment.SpecialFolder? Folder, string? ApprovedSubkey, string Display);

    private static readonly LocationInfo[] Locations =
    [
        new(StartupLocation.HkcuRun, false, RunPath, null, "Run", @"HKCU\" + RunPath),
        new(StartupLocation.HkcuRunOnce, false, RunOncePath, null, null, @"HKCU\" + RunOncePath),
        new(StartupLocation.HklmRun, true, RunPath, null, "Run", @"HKLM\" + RunPath),
        new(StartupLocation.HklmRunOnce, true, RunOncePath, null, null, @"HKLM\" + RunOncePath),
        new(StartupLocation.HklmRun32, true, Run32Path, null, "Run32", @"HKLM\" + Run32Path),
        new(StartupLocation.HklmRunOnce32, true, RunOnce32Path, null, null, @"HKLM\" + RunOnce32Path),
        new(StartupLocation.HkcuPoliciesRun, false, PoliciesRunPath, null, null, @"HKCU\" + PoliciesRunPath),
        new(StartupLocation.HklmPoliciesRun, true, PoliciesRunPath, null, null, @"HKLM\" + PoliciesRunPath),
        new(StartupLocation.UserStartupFolder, false, null, Environment.SpecialFolder.Startup, "StartupFolder", "Pasta Inicializar do usuário"),
        new(StartupLocation.CommonStartupFolder, true, null, Environment.SpecialFolder.CommonStartup, "StartupFolder", "Pasta Inicializar de todos os usuários"),
    ];

    private static LocationInfo Info(StartupLocation l) => Locations.First(x => x.Location == l);

    public static bool IsToggleable(StartupLocation l) => Info(l).ApprovedSubkey is not null;
    public static bool RequiresElevation(StartupLocation l) => Info(l).Machine;

    /// <summary>Enumera todas as entradas. Assinaturas são verificadas em paralelo limitado quando <paramref name="verifySignatures"/>.</summary>
    public async Task<IReadOnlyList<StartupEntry>> EnumerateAsync(bool verifySignatures = true, CancellationToken ct = default)
    {
        var raw = await Task.Run(() => EnumerateRaw(ct), ct).ConfigureAwait(false);
        var paths = raw.Select(r => r.parsed.EffectivePath).Where(p => p is not null).Cast<string>();
        var meta = await FileMetadataCache.GetManyAsync(paths, verifySignatures, 2, ct).ConfigureAwait(false);

        var list = new List<StartupEntry>(raw.Count);
        foreach (var (info, name, command, parsed, state, note) in raw)
        {
            string? eff = parsed.EffectivePath;
            PublisherInfo? pub = eff is not null && meta.TryGetValue(eff, out var m) ? m : null;
            bool exists = eff is not null && (pub?.Exists ?? File.Exists(eff));
            var rec = AutostartAdvisor.Recommend(new AutostartFacts(AutostartKind.StartupEntry, name, command, eff, exists, pub));
            list.Add(new StartupEntry($"{info.Location}|{name}", info.Location, info.Display, name, command, parsed.ExecutablePath,
                parsed.Arguments, parsed.HostedModulePath, exists,
                info.ApprovedSubkey is null ? StartupEntryState.NotApplicable : state.Enabled ? StartupEntryState.Enabled : StartupEntryState.Disabled,
                state.DisabledAtUtc, info.ApprovedSubkey is not null, info.Machine, pub, rec, note));
        }
        return list;
    }

    private static List<(LocationInfo info, string name, string? command, ParsedCommand parsed, StartupApprovedState state, string? note)> EnumerateRaw(CancellationToken ct)
    {
        var result = new List<(LocationInfo, string, string?, ParsedCommand, StartupApprovedState, string?)>();
        foreach (var info in Locations)
        {
            ct.ThrowIfCancellationRequested();
            if (info.KeyPath is not null)
            {
                try
                {
                    using var root = OpenRoot(info.Machine);
                    using var key = root.OpenSubKey(info.KeyPath, writable: false);
                    if (key is null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.Length == 0) continue; // valor padrão não é usado por Run
                        string? cmd = key.GetValue(name, null, RegistryValueOptions.None)?.ToString();
                        var state = ReadApproved(info, name);
                        string? note = info.ApprovedSubkey is null
                            ? (info.Location is StartupLocation.HkcuPoliciesRun or StartupLocation.HklmPoliciesRun
                                ? "Definido por política: não pode ser desativado pelo Gerenciador de Tarefas."
                                : "RunOnce: executa uma única vez no próximo logon.")
                            : null;
                        result.Add((info, name, cmd, CommandLineParser.Parse(cmd), state, note));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
            else if (info.Folder is { } folder)
            {
                string dir;
                try { dir = Environment.GetFolderPath(folder); } catch { continue; }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir).ToList(); } catch { continue; }
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var state = ReadApproved(info, name);
                    ParsedCommand parsed;
                    string? command;
                    string? note = null;
                    if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        var link = ShellLinkResolver.Resolve(file);
                        if (link?.TargetPath is { } t)
                        {
                            command = $"\"{t}\"{(string.IsNullOrEmpty(link.Arguments) ? "" : " " + link.Arguments)}";
                            parsed = CommandLineParser.Parse(command);
                        }
                        else
                        {
                            command = file;
                            parsed = new ParsedCommand(null, "", null, false);
                            note = "Atalho sem destino resolvível (pode ser um atalho anunciado do Windows Installer).";
                        }
                    }
                    else
                    {
                        command = file;
                        parsed = new ParsedCommand(file, "", null, true);
                    }
                    result.Add((info, name, command, parsed, state, note));
                }
            }
        }
        return result;
    }

    private static StartupApprovedState ReadApproved(LocationInfo info, string name)
    {
        if (info.ApprovedSubkey is null) return new StartupApprovedState(false, true, null, 0);
        try
        {
            using var root = OpenRoot(info.Machine);
            using var key = root.OpenSubKey($@"{StartupApproved.KeyBase}\{info.ApprovedSubkey}", writable: false);
            return StartupApproved.Decode(key?.GetValue(name) as byte[]);
        }
        catch { return new StartupApprovedState(false, true, null, 0); }
    }

    private static RegistryKey OpenRoot(bool machine) =>
        RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    // ---------------- Alternar ----------------

    /// <summary>
    /// Habilita/desabilita uma entrada do USUÁRIO (HKCU\Run ou pasta Inicializar do usuário) sem elevação.
    /// Para HKLM/pasta comum retorna <see cref="ProcessErrorCodes.ElevationRequired"/>.
    /// </summary>
    public OperationResult<StartupToggleResponse> SetEnabled(StartupLocation location, string name, bool enabled)
    {
        var v = ValidateShape(location.ToString(), name, elevated: false);
        if (!v.Success) return OperationResult<StartupToggleResponse>.Fail(v.Message, v.ErrorCode);
        return Toggle(Info(location), name, enabled, elevated: false);
    }

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (op <c>startup.setEnabled</c>).
    /// Aceita somente locais de máquina (HklmRun, HklmRun32, CommonStartupFolder); a entrada precisa existir.
    /// Escreve apenas em HKLM\...\StartupApproved (reversível).
    /// </summary>
    public static OperationResult<StartupToggleResponse> SetEnabledElevated(StartupToggleRequest request)
    {
        if (request is null) return OperationResult<StartupToggleResponse>.Fail("Requisição vazia.", ProcessErrorCodes.InvalidArgument);
        var v = ValidateShape(request.Location, request.Name, elevated: true);
        if (!v.Success) return OperationResult<StartupToggleResponse>.Fail(v.Message, v.ErrorCode);
        return Toggle(Info(v.Value), request.Name, request.Enabled, elevated: true);
    }

    /// <summary>
    /// Validação de formato (pura): local conhecido e alternável, compatível com o contexto (HKCU sem elevação,
    /// HKLM/pasta comum só no agente) e nome sem caracteres de controle/caminho.
    /// </summary>
    public static OperationResult<StartupLocation> ValidateShape(string? location, string? name, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(location) || !Enum.TryParse<StartupLocation>(location, ignoreCase: false, out var loc)
            || !Enum.IsDefined(loc) || int.TryParse(location, out _))
            return OperationResult<StartupLocation>.Fail($"Local de inicialização desconhecido: \"{location}\".", ProcessErrorCodes.InvalidArgument);
        var info = Info(loc);
        if (info.ApprovedSubkey is null)
            return OperationResult<StartupLocation>.Fail("Este local (RunOnce/Política) não pode ser habilitado/desabilitado de forma reversível.", ProcessErrorCodes.InvalidArgument);
        if (elevated && !info.Machine)
            return OperationResult<StartupLocation>.Fail("Entradas do usuário (HKCU) devem ser alteradas sem elevação, no contexto do próprio usuário.", ProcessErrorCodes.InvalidArgument);
        if (!elevated && info.Machine)
            return OperationResult<StartupLocation>.Fail("Entrada de todos os usuários: requer elevação (agente RBM).", ProcessErrorCodes.ElevationRequired);
        if (string.IsNullOrEmpty(name) || name.Length > 260 || name.Any(char.IsControl))
            return OperationResult<StartupLocation>.Fail("Nome de entrada inválido.", ProcessErrorCodes.InvalidArgument);
        if (info.Folder is not null && (name.IndexOfAny(['\\', '/', ':']) >= 0 || name is "." or ".." || name.Contains("..")))
            return OperationResult<StartupLocation>.Fail("Nome de arquivo inválido para a pasta Inicializar.", ProcessErrorCodes.InvalidArgument);
        return OperationResult<StartupLocation>.Ok(loc);
    }

    private static OperationResult<StartupToggleResponse> Toggle(LocationInfo info, string name, bool enabled, bool elevated)
    {
        // A entrada precisa existir no local de origem: impede gravar valores arbitrários no registro.
        if (!SourceExists(info, name))
            return OperationResult<StartupToggleResponse>.Fail($"Entrada \"{name}\" não encontrada em {info.Display}.", ProcessErrorCodes.NotFound);
        try
        {
            using var root = OpenRoot(info.Machine);
            using var key = root.CreateSubKey($@"{StartupApproved.KeyBase}\{info.ApprovedSubkey}", writable: true);
            var previousBytes = key.GetValue(name) as byte[];
            var previous = StartupApproved.Decode(previousBytes);
            if (previous.Enabled == enabled && previous.Present)
                return OperationResult<StartupToggleResponse>.Ok(new StartupToggleResponse(info.Location.ToString(), name, previous.Enabled, enabled),
                    enabled ? "A entrada já está habilitada." : "A entrada já está desabilitada.");
            key.SetValue(name, StartupApproved.Encode(enabled, DateTime.UtcNow, previousBytes), RegistryValueKind.Binary);
            var current = StartupApproved.Decode(key.GetValue(name) as byte[]);
            return OperationResult<StartupToggleResponse>.Ok(new StartupToggleResponse(info.Location.ToString(), name, previous.Enabled, current.Enabled),
                $"\"{name}\" {(current.Enabled ? "habilitado" : "desabilitado")} na inicialização (reversível).");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return OperationResult<StartupToggleResponse>.Fail(elevated ? "Acesso negado ao registro mesmo com elevação." : "Acesso negado. Use o agente RBM (requer elevação).",
                ProcessErrorCodes.AccessDenied);
        }
        catch (Exception ex)
        {
            return OperationResult<StartupToggleResponse>.Fail("Falha ao alterar a entrada: " + ex.Message, ProcessErrorCodes.Failed);
        }
    }

    private static bool SourceExists(LocationInfo info, string name)
    {
        try
        {
            if (info.KeyPath is not null)
            {
                using var root = OpenRoot(info.Machine);
                using var key = root.OpenSubKey(info.KeyPath, writable: false);
                return key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            }
            if (info.Folder is { } f)
            {
                string dir = Environment.GetFolderPath(f);
                string full = Path.GetFullPath(Path.Combine(dir, name));
                return string.Equals(Path.GetDirectoryName(full), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && File.Exists(full);
            }
        }
        catch { }
        return false;
    }
}
