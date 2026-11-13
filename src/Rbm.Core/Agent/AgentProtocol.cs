using System.Text.Json;
using System.Text.Json.Serialization;
using Rbm.Core.Tweaks;

namespace Rbm.Core.Agent;

/// <summary>
/// Protocolo entre a UI (sem privilégios) e o Rbm.Agent (elevado).
/// Mensagens JSON delimitadas por tamanho (prefixo int32 LE) em named pipe local.
/// O agente só aceita operações listadas em <see cref="AgentOps"/>; payloads são validados no agente.
/// </summary>
public sealed record AgentRequest(string Id, string Op, JsonElement? Payload, string Token);

public sealed record AgentResponse(string Id, bool Ok, string? Error, JsonElement? Payload)
{
    public T? Read<T>() => Payload is { } p ? p.Deserialize<T>(AgentJson.Options) : default;
}

public static class AgentOps
{
    public const string Ping = "ping";
    public const string Shutdown = "shutdown";
    public const string TweakRead = "tweak.read";
    public const string TweakApply = "tweak.apply";
    public const string TweakRevert = "tweak.revert";
    public const string RestorePointCreate = "restore.create";
    public const string ServiceSetStart = "service.setStart";
    public const string StartupSetEnabled = "startup.setEnabled";
    public const string ProcessSetPriority = "process.setPriority";
    public const string ProcessKill = "process.kill";
    public const string DriverExport = "driver.export";
    public const string DriverInstall = "driver.install";
    public const string CleanupRun = "cleanup.run";
    public const string LatencyCapture = "latency.capture";
    public const string FrameCapture = "frames.capture";
    public const string StorageHealth = "storage.health";
    public const string TrimRun = "storage.trim";
    public const string LatencyTimerRequests = "latency.timerRequests";
    public const string DriverSwitch = "driver.switch";
    public const string ServiceControl = "service.control";
    public const string TaskSetEnabled = "task.setEnabled";
    public const string NetAdvPropSet = "net.advprop.set";
    public const string NetAdvPropRead = "net.advprop.read";
    public const string StorageScan = "storage.scan";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Ping, Shutdown, TweakRead, TweakApply, TweakRevert, RestorePointCreate, ServiceSetStart, StartupSetEnabled,
        ProcessSetPriority, ProcessKill, DriverExport, DriverInstall, CleanupRun, LatencyCapture, FrameCapture, StorageHealth, TrimRun,
        LatencyTimerRequests, DriverSwitch, ServiceControl, TaskSetEnabled, NetAdvPropSet, NetAdvPropRead, StorageScan,
    };
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false, AllowOutOfOrderMetadataProperties = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

// ---- Payloads ----
public sealed record TweakReadRequest(string TweakId);
public sealed record TweakApplyRequest(string TweakId, string OptionKey);
public sealed record TweakApplyResponse(List<TargetChange> Changes, bool Success, bool RolledBack, string? Error);
public sealed record TweakRevertRequest(string TweakId, List<TargetChange> Changes, bool Force);
public sealed record TweakRevertResponse(bool Success, List<DriftReport> Drift, string? Error);
public sealed record RestorePointRequest(string Description);
public sealed record ServiceStartRequest(string ServiceName, ServiceStartMode Mode);
public sealed record ServiceStartResponse(ServiceStartMode Previous, ServiceStartMode Current);
public sealed record ProcessPriorityRequest(int Pid, string Priority, long? AffinityMask);
public sealed record ProcessKillRequest(int Pid, bool Tree);
public sealed record DriverExportRequest(string DestinationFolder, string? PublishedName);
public sealed record DriverInstallRequest(string InfPath);
public sealed record CleanupRequest(List<string> CategoryIds, bool DryRun);
public sealed record LatencyCaptureRequest(int Seconds);
public sealed record FrameCaptureRequest(string ProcessName, int Seconds, string PresentMonPath);
public sealed record StartupToggleRequest(string Location, string Name, bool Enabled);
public sealed record ProcessOutputResponse(int ExitCode, string StdOut, string StdErr);

/// <summary>Cliente do agente (implementado na UI).</summary>
public interface IAgentClient
{
    bool IsConnected { get; }
    event EventHandler? ConnectionChanged;
    /// <summary>Inicia o agente (dispara o UAC) se necessário. Retorna false se o usuário recusar.</summary>
    Task<bool> EnsureConnectedAsync(CancellationToken ct = default);
    Task<AgentResponse> InvokeAsync(string op, object? payload, TimeSpan? timeout = null, CancellationToken ct = default);
    Task ShutdownAsync();
}
