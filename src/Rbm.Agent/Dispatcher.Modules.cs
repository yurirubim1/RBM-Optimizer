using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Drivers;
using Rbm.Gaming.PresentMon;
using Rbm.Optimization.SpaceAnalysis;
using Rbm.Optimization.Cleanup;
using Rbm.Latency;
using Rbm.Network.Agent;
using Rbm.Processes.Agent;

namespace Rbm.Agent;

/// <summary>Operações elevadas dos módulos. Cada handler do módulo revalida o payload por conta própria.</summary>
internal sealed partial class Dispatcher
{
    private partial async Task<object?> HandleModuleOpAsync(AgentRequest req, CancellationToken ct)
    {
        switch (req.Op)
        {
            // ---- Drivers ----
            case AgentOps.DriverExport: return await DriverAgentOperations.ExportAsync(P<DriverExportRequest>(req), ct);
            case AgentOps.DriverInstall: return await DriverAgentOperations.InstallAsync(P<DriverInstallRequest>(req), ct);
            case AgentOps.DriverSwitch: return await DriverAgentOperations.SwitchAsync(P<DriverSwitchRequest>(req), ct);

            // ---- Latência ----
            case AgentOps.LatencyCapture: return await LatencyAgentHandlers.CaptureAsync(P<LatencyCaptureRequest>(req), ct);
            case AgentOps.LatencyTimerRequests: return await LatencyAgentHandlers.TimerRequestsAsync(P<TimerRequestsCaptureRequest>(req), ct);

            // ---- Processos / serviços / inicialização / tarefas ----
            case AgentOps.ProcessSetPriority: return Unwrap(ProcessesAgentHandlers.ProcessSetPriority(P<ProcessPriorityCommand>(req)));
            case AgentOps.ProcessKill: return Unwrap(ProcessesAgentHandlers.ProcessKill(P<ProcessKillCommand>(req)));
            case AgentOps.StartupSetEnabled: return Unwrap(ProcessesAgentHandlers.StartupSetEnabled(P<StartupToggleRequest>(req)));
            case AgentOps.ServiceControl: return Unwrap(await ProcessesAgentHandlers.ServiceControlAsync(P<ServiceControlRequest>(req), ct));
            case AgentOps.TaskSetEnabled: return Unwrap(ProcessesAgentHandlers.TaskSetEnabled(P<TaskToggleRequest>(req)));

            // ---- Gaming (PresentMon/ETW, sem injeção) ----
            case AgentOps.FrameCapture:
                return Unwrap(await PresentMonCapture.CaptureAsync(P<FrameCaptureRequest>(req),
                    RbmPaths.Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RBMOptimizer", "captures")), ct));

            // ---- Análise de espaço (varredura de pastas protegidas) ----
            case AgentOps.StorageScan: return await SpaceScanner.ScanForAgentAsync(P<StorageScanRequest>(req), ct);

            // ---- Limpeza (somente categorias administrativas da allow-list) ----
            case AgentOps.CleanupRun: return CleanupEngine.RunElevated(P<CleanupRequest>(req), ct);

            // ---- Rede ----
            case AgentOps.NetAdvPropSet:
            case AgentOps.NetAdvPropRead:
                return await NetworkAgentHandler.HandleAsync(req.Op, req.Payload, ct);
        }
        throw new NotSupportedException($"Operação '{req.Op}' ainda não integrada neste build do agente.");
    }

    private static T Unwrap<T>(OperationResult<T> r) => r.Success && r.Value is not null ? r.Value : throw new InvalidOperationException(r.Message);
}
