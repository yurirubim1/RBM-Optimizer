using System.Runtime.InteropServices;
using System.Text;
using Rbm.Core;
using Rbm.Core.Agent;
using ServiceStartMode = Rbm.Core.Tweaks.ServiceStartMode;
using Rbm.Processes.Agent;
using Rbm.Processes.Native;
using Rbm.Security;
using Microsoft.Win32;
using static Rbm.Processes.Native.NativeMethods;

namespace Rbm.Processes;

public enum ServiceRunState { Unknown, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }

public sealed record ServiceEntry(
    string Name,
    string DisplayName,
    string? Description,
    ServiceRunState Status,
    // Null quando não foi possível ler a configuração.
    ServiceStartMode? StartMode,
    bool IsDelayedAutoStart,
    // Null = desconhecido (configuração inacessível).
    bool? IsTriggerStart,
    string? Account,
    string? Command,
    string? ExecutablePath,
    string? ServiceDll,
    uint ServiceType,
    bool IsUserService,
    bool IsSharedProcess,
    int? ProcessId,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents,
    PublisherInfo? Publisher,
    bool IsProtected,
    // Null = assinatura não verificada.
    bool? IsMicrosoft,
    AutostartRecommendation Recommendation,
    string? Note)
{
    /// <summary>Arquivo que representa o serviço (ServiceDll para serviços hospedados no svchost).</summary>
    public string? EffectivePath => ServiceDll ?? ExecutablePath;
}

public sealed record ServiceQueryOptions
{
    /// <summary>Verifica assinaturas (cara: centenas de arquivos; usa cache e 2 threads).</summary>
    public bool VerifySignatures { get; init; } = true;
    /// <summary>Lê FileVersionInfo (empresa/descrição) dos binários.</summary>
    public bool IncludeFileMetadata { get; init; } = true;
}

/// <summary>
/// Serviços Win32 via Service Control Manager (P/Invoke): enumeração (sem elevação), alteração do tipo de
/// inicialização e controle (elevados, via RBM.Agent). Serviços em <see cref="Protection.ProtectedServices"/> nunca
/// têm a inicialização alterada nem são parados.
/// </summary>
public sealed class ServiceManager
{
    private const uint SERVICE_KERNEL_DRIVER = 0x1, SERVICE_FILE_SYSTEM_DRIVER = 0x2, SERVICE_WIN32_OWN_PROCESS = 0x10,
        SERVICE_WIN32_SHARE_PROCESS = 0x20, SERVICE_USER_SERVICE = 0x40, SERVICE_WIN32 = 0x30;
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    // ---------------- Enumeração ----------------

    public async Task<OperationResult<IReadOnlyList<ServiceEntry>>> EnumerateAsync(ServiceQueryOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ServiceQueryOptions();
        List<RawService> raw;
        try { raw = await Task.Run(() => EnumerateRaw(ct), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return OperationResult<IReadOnlyList<ServiceEntry>>.Fail("Falha ao enumerar serviços: " + ex.Message, ProcessErrorCodes.Failed); }

        IReadOnlyDictionary<string, PublisherInfo> meta = new Dictionary<string, PublisherInfo>();
        if (options.IncludeFileMetadata || options.VerifySignatures)
            meta = await FileMetadataCache.GetManyAsync(raw.Select(r => r.ServiceDll ?? r.ExecutablePath).Where(p => p is not null).Cast<string>(),
                options.VerifySignatures, 2, ct).ConfigureAwait(false);

        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw)
            foreach (var d in r.Dependencies)
                (dependents.TryGetValue(d, out var l) ? l : dependents[d] = new List<string>()).Add(r.Name);

        var list = new List<ServiceEntry>(raw.Count);
        foreach (var r in raw)
        {
            string? eff = r.ServiceDll ?? r.ExecutablePath;
            PublisherInfo? pub = eff is not null && meta.TryGetValue(eff, out var m) ? m : null;
            bool isProtected = Protection.IsProtectedService(r.Name) || Protection.IsProtectedService(BaseUserServiceName(r.Name, r.Type));
            bool exists = eff is not null && (pub?.Exists ?? File.Exists(eff));
            var rec = AutostartAdvisor.Recommend(new AutostartFacts(AutostartKind.Service, r.Name, r.Command, eff, exists, pub, isProtected));
            list.Add(new ServiceEntry(r.Name, r.DisplayName, r.Description, r.Status, r.StartMode, r.Delayed, r.Triggers, r.Account, r.Command,
                r.ExecutablePath, r.ServiceDll, r.Type, (r.Type & SERVICE_USER_SERVICE) != 0, (r.Type & SERVICE_WIN32_SHARE_PROCESS) != 0,
                r.Pid, r.Dependencies, dependents.TryGetValue(r.Name, out var deps) ? deps : [],
                pub, isProtected, pub?.Signature is null ? null : pub.IsVerifiedMicrosoft, rec, r.Note));
        }
        return OperationResult<IReadOnlyList<ServiceEntry>>.Ok(list, $"{list.Count} serviço(s).");
    }

    private sealed record RawService(string Name, string DisplayName, string? Description, ServiceRunState Status, ServiceStartMode? StartMode,
        bool Delayed, bool? Triggers, string? Account, string? Command, string? ExecutablePath, string? ServiceDll, uint Type, int? Pid,
        IReadOnlyList<string> Dependencies, string? Note);

    private static unsafe List<RawService> EnumerateRaw(CancellationToken ct)
    {
        using var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm.IsInvalid) throw new InvalidOperationException(Win32Messages.Describe(Marshal.GetLastPInvokeError()));

        var basics = new List<(string name, string display, SERVICE_STATUS_PROCESS st)>();
        uint resume = 0;
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            bool ok;
            uint needed, returned;
            int err;
            fixed (byte* p = buffer)
            {
                ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, p, (uint)buffer.Length, out needed, out returned, ref resume, null);
                err = ok ? 0 : Marshal.GetLastPInvokeError();
                if (ok || err == ERROR_MORE_DATA)
                {
                    var arr = (ENUM_SERVICE_STATUS_PROCESS*)p;
                    for (int i = 0; i < returned; i++)
                        basics.Add((PtrToString(arr[i].lpServiceName) ?? "", PtrToString(arr[i].lpDisplayName) ?? "", arr[i].ServiceStatusProcess));
                }
            }
            if (ok) break;
            if (err != ERROR_MORE_DATA) throw new InvalidOperationException(Win32Messages.Describe(err));
            if (returned == 0) buffer = new byte[Math.Max(buffer.Length * 2, (int)needed + 4096)];
        }

        var result = new List<RawService>(basics.Count);
        foreach (var (name, display, st) in basics)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(ReadOne(scm, name, display, st));
        }
        return result;
    }

    private static unsafe RawService ReadOne(ServiceHandle scm, string name, string display, SERVICE_STATUS_PROCESS st)
    {
        var status = MapState(st.dwCurrentState);
        int? pid = st.dwProcessId != 0 ? (int)st.dwProcessId : null;
        string? serviceDll = ReadServiceDll(name);
        using var svc = OpenService(scm, name, SERVICE_QUERY_CONFIG);
        if (svc.IsInvalid)
            return FromRegistry(name, display, status, pid, st.dwServiceType, serviceDll,
                $"Configuração lida do registro (SCM: {Win32Messages.Describe(Marshal.GetLastPInvokeError())}).");

        var cfgBuf = QueryConfigBuffer(svc);
        if (cfgBuf is null)
            return FromRegistry(name, display, status, pid, st.dwServiceType, serviceDll, "Configuração lida do registro.");
        fixed (byte* p = cfgBuf)
        {
            var cfg = (QUERY_SERVICE_CONFIG*)p;
            string? cmd = PtrToString(cfg->lpBinaryPathName);
            string? account = PtrToString(cfg->lpServiceStartName);
            var deps = PtrToMultiString(cfg->lpDependencies);
            bool delayed = QueryDelayed(svc) ?? false;
            bool? triggers = QueryTriggers(svc);
            string? desc = QueryDescription(svc);
            var mode = MapStart(cfg->dwStartType, delayed);
            return new RawService(name, display, desc, status, mode, delayed && cfg->dwStartType == 2, triggers, account, cmd,
                ResolveServiceExe(cmd), serviceDll, cfg->dwServiceType, pid, deps, null);
        }
    }

    private static unsafe byte[]? QueryConfigBuffer(ServiceHandle svc)
    {
        QueryServiceConfig(svc, null, 0, out uint needed);
        if (needed == 0) return null;
        var buf = new byte[needed];
        fixed (byte* p = buf)
            return QueryServiceConfig(svc, p, needed, out _) ? buf : null;
    }

    private static unsafe byte[]? QueryConfig2(ServiceHandle svc, uint level)
    {
        QueryServiceConfig2(svc, level, null, 0, out uint needed);
        if (needed == 0) return null;
        var buf = new byte[needed];
        fixed (byte* p = buf)
            return QueryServiceConfig2(svc, level, p, needed, out _) ? buf : null;
    }

    private static unsafe string? QueryDescription(ServiceHandle svc)
    {
        var b = QueryConfig2(svc, SERVICE_CONFIG_DESCRIPTION);
        if (b is null || b.Length < IntPtr.Size) return null;
        fixed (byte* p = b) return PtrToString(*(nint*)p);
    }

    private static unsafe bool? QueryDelayed(ServiceHandle svc)
    {
        var b = QueryConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO);
        if (b is null || b.Length < 4) return null;
        fixed (byte* p = b) return ((SERVICE_DELAYED_AUTO_START_INFO*)p)->fDelayedAutostart != 0;
    }

    private static unsafe bool? QueryTriggers(ServiceHandle svc)
    {
        var b = QueryConfig2(svc, SERVICE_CONFIG_TRIGGER_INFO);
        if (b is null || b.Length < 4) return null;
        fixed (byte* p = b) return ((SERVICE_TRIGGER_INFO*)p)->cTriggers > 0;
    }

    private static RawService FromRegistry(string name, string display, ServiceRunState status, int? pid, uint type, string? serviceDll, string note)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey($@"{ServicesKey}\{name}");
            if (k is null) return new RawService(name, display, null, status, null, false, null, null, null, null, serviceDll, type, pid, [], note);
            string? cmd = k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
            int start = k.GetValue("Start") is int s ? s : -1;
            bool delayed = k.GetValue("DelayedAutostart") is int d && d != 0;
            bool triggers;
            using (var t = k.OpenSubKey("TriggerInfo")) triggers = t is not null && t.SubKeyCount > 0;
            var deps = k.GetValue("DependOnService") as string[] ?? [];
            var groups = (k.GetValue("DependOnGroup") as string[] ?? []).Select(g => "+" + g);
            return new RawService(name, display, LoadIndirect(k.GetValue("Description")?.ToString()), status,
                start >= 0 ? MapStart((uint)start, delayed) : null, delayed && start == 2, triggers, k.GetValue("ObjectName")?.ToString(), cmd,
                ResolveServiceExe(cmd), serviceDll, type, pid, deps.Concat(groups).ToList(), note);
        }
        catch
        {
            return new RawService(name, display, null, status, null, false, null, null, null, null, serviceDll, type, pid, [], note);
        }
    }

    private static string? ReadServiceDll(string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var p = hklm.OpenSubKey($@"{ServicesKey}\{name}\Parameters");
            var v = p?.GetValue("ServiceDll")?.ToString();
            if (v is null)
            {
                using var k = hklm.OpenSubKey($@"{ServicesKey}\{name}");
                v = k?.GetValue("ServiceDll")?.ToString();
            }
            return string.IsNullOrWhiteSpace(v) ? null : CommandLineParser.NormalizeImagePath(v);
        }
        catch { return null; }
    }

    private static string? ResolveServiceExe(string? cmd) => string.IsNullOrWhiteSpace(cmd) ? null : CommandLineParser.Parse(cmd).ExecutablePath;

    /// <summary>Resolve strings indiretas ("@%SystemRoot%\system32\x.dll,-101").</summary>
    internal static string? LoadIndirect(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || !s.StartsWith('@')) return s;
        try
        {
            var sb = new StringBuilder(1024);
            return SHLoadIndirectString(s, sb, (uint)sb.Capacity, 0) == 0 ? sb.ToString() : s;
        }
        catch { return s; }
    }

    // Instâncias de serviços por usuário ("OneSyncSvc_1a2b3") herdam a proteção do modelo.
    private static string BaseUserServiceName(string name, uint type)
    {
        if ((type & SERVICE_USER_SERVICE) == 0) return name;
        int i = name.LastIndexOf('_');
        return i > 0 ? name[..i] : name;
    }

    public static ServiceRunState MapState(uint s) => s switch
    {
        1 => ServiceRunState.Stopped,
        2 => ServiceRunState.StartPending,
        3 => ServiceRunState.StopPending,
        4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending,
        6 => ServiceRunState.PausePending,
        7 => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };

    public static ServiceStartMode? MapStart(uint startType, bool delayed) => startType switch
    {
        0 => ServiceStartMode.Boot,
        1 => ServiceStartMode.System,
        2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
        3 => ServiceStartMode.Manual,
        4 => ServiceStartMode.Disabled,
        _ => null,
    };

    public static string Display(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Boot => "Boot",
        ServiceStartMode.System => "Sistema",
        ServiceStartMode.Automatic => "Automático",
        ServiceStartMode.AutomaticDelayed => "Automático (atraso na inicialização)",
        ServiceStartMode.Manual => "Manual",
        ServiceStartMode.Disabled => "Desativado",
        _ => m.ToString(),
    };

    // ---------------- Validação ----------------

    /// <summary>Nome de serviço aceitável vindo do canal: 1..256 caracteres, sem barras nem caracteres de controle.</summary>
    public static bool IsValidServiceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && name.IndexOfAny(['\\', '/']) < 0 && !name.Any(char.IsControl);

    /// <summary>Validação pura de uma alteração de inicialização (antes de tocar no SCM).</summary>
    public static OperationResult ValidateStartModeRequest(ServiceStartRequest? req)
    {
        if (req is null) return OperationResult.Fail("Requisição vazia.", ProcessErrorCodes.InvalidArgument);
        if (!IsValidServiceName(req.ServiceName)) return OperationResult.Fail("Nome de serviço inválido.", ProcessErrorCodes.InvalidArgument);
        if (!Enum.IsDefined(req.Mode)) return OperationResult.Fail("Tipo de inicialização inválido.", ProcessErrorCodes.InvalidArgument);
        if (req.Mode is ServiceStartMode.Boot or ServiceStartMode.System)
            return OperationResult.Fail("Os modos Boot/Sistema são exclusivos de drivers e não são aceitos.", ProcessErrorCodes.InvalidArgument);
        if (Protection.IsProtectedService(req.ServiceName))
            return OperationResult.Fail($"\"{req.ServiceName}\" é um serviço essencial/de segurança protegido; a inicialização não pode ser alterada.", ProcessErrorCodes.Protected);
        return OperationResult.Ok();
    }

    public static OperationResult ValidateControlRequest(ServiceControlRequest? req)
    {
        if (req is null) return OperationResult.Fail("Requisição vazia.", ProcessErrorCodes.InvalidArgument);
        if (!IsValidServiceName(req.ServiceName)) return OperationResult.Fail("Nome de serviço inválido.", ProcessErrorCodes.InvalidArgument);
        if (!Enum.IsDefined(req.Action)) return OperationResult.Fail("Ação inválida.", ProcessErrorCodes.InvalidArgument);
        if (req.Action != ServiceControlAction.Start && Protection.IsProtectedService(req.ServiceName))
            return OperationResult.Fail($"\"{req.ServiceName}\" é um serviço essencial/de segurança protegido; não pode ser parado.", ProcessErrorCodes.Protected);
        return OperationResult.Ok();
    }

    // ---------------- Alterações (elevadas) ----------------

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (op <c>service.setStart</c>).
    /// Altera o tipo de inicialização (ChangeServiceConfig + ChangeServiceConfig2 para o atraso). Recusa serviços
    /// protegidos, drivers e os modos Boot/Sistema. Retorna o modo anterior para reversão.
    /// </summary>
    public static unsafe OperationResult<ServiceStartResponse> SetStartModeElevated(ServiceStartRequest request)
    {
        var v = ValidateStartModeRequest(request);
        if (!v.Success) return OperationResult<ServiceStartResponse>.Fail(v.Message, v.ErrorCode);

        using var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid) return Fail<ServiceStartResponse>("Falha ao abrir o gerenciador de serviços", Marshal.GetLastPInvokeError());
        using var svc = OpenService(scm, request.ServiceName, SERVICE_QUERY_CONFIG | SERVICE_CHANGE_CONFIG);
        if (svc.IsInvalid) return Fail<ServiceStartResponse>($"Falha ao abrir o serviço \"{request.ServiceName}\"", Marshal.GetLastPInvokeError());

        var before = ReadStartMode(svc, out uint type);
        if (before is null) return Fail<ServiceStartResponse>("Falha ao ler a configuração do serviço", Marshal.GetLastPInvokeError());
        if ((type & SERVICE_WIN32) == 0 || (type & (SERVICE_KERNEL_DRIVER | SERVICE_FILE_SYSTEM_DRIVER)) != 0)
            return OperationResult<ServiceStartResponse>.Fail("Somente serviços Win32 podem ser alterados (drivers não).", ProcessErrorCodes.Protected);
        if (before == request.Mode)
            return OperationResult<ServiceStartResponse>.Ok(new ServiceStartResponse(before.Value, before.Value), "Nenhuma alteração necessária.");

        uint startType = request.Mode switch
        {
            ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2,
            ServiceStartMode.Manual => 3,
            _ => 4,
        };
        if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE, null, null, 0, null, null, null, null))
            return Fail<ServiceStartResponse>("Falha ao alterar a inicialização", Marshal.GetLastPInvokeError());
        if (startType == 2)
        {
            var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = request.Mode == ServiceStartMode.AutomaticDelayed ? 1 : 0 };
            if (!ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
            {
                int err = Marshal.GetLastPInvokeError();
                var now = ReadStartMode(svc, out _) ?? before.Value;
                return new OperationResult<ServiceStartResponse>(false, new ServiceStartResponse(before.Value, now),
                    $"Tipo alterado, mas o atraso não pôde ser ajustado: {Win32Messages.Describe(err, true)}", Win32Messages.CodeFor(err));
            }
        }
        var after = ReadStartMode(svc, out _) ?? request.Mode;
        return OperationResult<ServiceStartResponse>.Ok(new ServiceStartResponse(before.Value, after),
            $"Inicialização de \"{request.ServiceName}\": {Display(before.Value)} → {Display(after)}.");
    }

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (op proposta <c>service.control</c>).
    /// Inicia/para/reinicia e aguarda o estado final. Parar/reiniciar serviços protegidos é recusado; dependentes em
    /// execução NÃO são parados automaticamente (retorna <see cref="ProcessErrorCodes.DependentsRunning"/>).
    /// </summary>
    public static Task<OperationResult<ServiceControlResponse>> ControlElevatedAsync(ServiceControlRequest request, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ControlCoreAsync(request, timeout ?? TimeSpan.FromSeconds(30), elevated: true, ct);

    /// <summary>Mesma operação com o token atual (funciona só para serviços com ACL permissiva; senão AccessDenied).</summary>
    public static Task<OperationResult<ServiceControlResponse>> ControlAsync(ServiceControlRequest request, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ControlCoreAsync(request, timeout ?? TimeSpan.FromSeconds(30), elevated: false, ct);

    private static async Task<OperationResult<ServiceControlResponse>> ControlCoreAsync(ServiceControlRequest request, TimeSpan timeout, bool elevated, CancellationToken ct)
    {
        var v = ValidateControlRequest(request);
        if (!v.Success) return OperationResult<ServiceControlResponse>.Fail(v.Message, v.ErrorCode);
        using var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid) return Fail<ServiceControlResponse>("Falha ao abrir o gerenciador de serviços", Marshal.GetLastPInvokeError(), elevated);
        using var svc = OpenService(scm, request.ServiceName, SERVICE_QUERY_STATUS | SERVICE_START | SERVICE_STOP | SERVICE_QUERY_CONFIG);
        if (svc.IsInvalid) return Fail<ServiceControlResponse>($"Falha ao abrir o serviço \"{request.ServiceName}\"", Marshal.GetLastPInvokeError(), elevated);
        _ = ReadStartMode(svc, out uint type);
        if (type != 0 && (type & SERVICE_WIN32) == 0)
            return OperationResult<ServiceControlResponse>.Fail("Somente serviços Win32 podem ser controlados.", ProcessErrorCodes.Protected);

        var previous = QueryState(svc);
        var deadline = DateTime.UtcNow + timeout;

        if (request.Action is ServiceControlAction.Stop or ServiceControlAction.Restart && previous is not ServiceRunState.Stopped)
        {
            if (!ControlService(svc, SERVICE_CONTROL_STOP, out _))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != Win32Messages.ERROR_SERVICE_NOT_ACTIVE)
                    return Fail<ServiceControlResponse>($"Falha ao parar \"{request.ServiceName}\"", err, elevated);
            }
            if (!await WaitForAsync(svc, ServiceRunState.Stopped, deadline, ct).ConfigureAwait(false))
                return new OperationResult<ServiceControlResponse>(false, new ServiceControlResponse(request.ServiceName, previous, QueryState(svc)),
                    "O serviço não parou dentro do tempo limite.", ProcessErrorCodes.Timeout);
        }
        if (request.Action is ServiceControlAction.Start or ServiceControlAction.Restart)
        {
            if (!StartService(svc, 0, 0))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != Win32Messages.ERROR_SERVICE_ALREADY_RUNNING)
                    return Fail<ServiceControlResponse>($"Falha ao iniciar \"{request.ServiceName}\"", err, elevated);
            }
            if (!await WaitForAsync(svc, ServiceRunState.Running, deadline, ct).ConfigureAwait(false))
                return new OperationResult<ServiceControlResponse>(false, new ServiceControlResponse(request.ServiceName, previous, QueryState(svc)),
                    "O serviço não iniciou dentro do tempo limite.", ProcessErrorCodes.Timeout);
        }
        var current = QueryState(svc);
        return OperationResult<ServiceControlResponse>.Ok(new ServiceControlResponse(request.ServiceName, previous, current),
            $"\"{request.ServiceName}\": {previous} → {current}.");
    }

    private static async Task<bool> WaitForAsync(ServiceHandle svc, ServiceRunState target, DateTime deadline, CancellationToken ct)
    {
        while (true)
        {
            var s = QueryState(svc);
            if (s == target) return true;
            if (DateTime.UtcNow >= deadline) return false;
            if (target == ServiceRunState.Running && s == ServiceRunState.Stopped) return false; // falhou ao iniciar
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private static ServiceRunState QueryState(ServiceHandle svc) =>
        QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, out var st, (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _)
            ? MapState(st.dwCurrentState) : ServiceRunState.Unknown;

    private static unsafe ServiceStartMode? ReadStartMode(ServiceHandle svc, out uint type)
    {
        type = 0;
        var buf = QueryConfigBuffer(svc);
        if (buf is null) return null;
        uint start;
        fixed (byte* p = buf) { type = ((QUERY_SERVICE_CONFIG*)p)->dwServiceType; start = ((QUERY_SERVICE_CONFIG*)p)->dwStartType; }
        return MapStart(start, QueryDelayed(svc) ?? false);
    }

    private static OperationResult<T> Fail<T>(string prefix, int err, bool elevated = true) =>
        OperationResult<T>.Fail($"{prefix}: {Win32Messages.Describe(err, elevated)}", Win32Messages.CodeFor(err));
}
