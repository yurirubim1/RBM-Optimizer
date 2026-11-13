using Rbm.Core.Agent;

namespace Rbm.Drivers;

/// <summary>Constantes PROPOSTAS para AgentOps (ainda não existem em Rbm.Core — ver README).</summary>
public static class ProposedDriverAgentOps
{
    /// <summary>Troca o driver de um dispositivo para um pacote já presente no Driver Store (reversão de versão).</summary>
    public const string DriverSwitch = "driver.switch";
}

/// <summary>Proposta de payload para "driver.switch". O caminho do INF NÃO é aceito da UI: o agente resolve oemNN.inf → Driver Store.</summary>
public sealed record DriverSwitchRequest(string InstanceId, string PublishedName);

/// <summary>Resposta de AgentOps.DriverExport.</summary>
public sealed record DriverExportResponse(bool Success, string? BackupFolder, string? ManifestPath, int Exported, int Failed, List<string> Messages);

/// <summary>Resposta de AgentOps.DriverInstall.</summary>
public sealed record DriverInstallResponse(bool Success, bool RebootRequired, int ExitCode, string? PublishedName, List<DriverChange> DeviceChanges, List<string> Messages);

/// <summary>Resposta de "driver.switch".</summary>
public sealed record DriverSwitchResponse(bool Success, bool RebootRequired, InstalledDriver? Before, InstalledDriver? After, List<string> Messages);

/// <summary>
/// Pontos de entrada para o RBM.Agent (processo elevado). Cada método revalida o payload inteiro (não confia na UI)
/// e nunca lança exceção para o chamador: erros viram respostas com Success=false.
/// <code>
/// case AgentOps.DriverExport:  return await DriverAgentOperations.ExportAsync(req.Payload.Deserialize&lt;DriverExportRequest&gt;(AgentJson.Options), ct);
/// case AgentOps.DriverInstall: return await DriverAgentOperations.InstallAsync(req.Payload.Deserialize&lt;DriverInstallRequest&gt;(AgentJson.Options), ct);
/// case "driver.switch":        return await DriverAgentOperations.SwitchAsync(req.Payload.Deserialize&lt;DriverSwitchRequest&gt;(AgentJson.Options), ct);
/// </code>
/// </summary>
public static class DriverAgentOperations
{
    /// <summary>AgentOps.DriverExport — DriverExportRequest(DestinationFolder, PublishedName: null/"*" = todos).</summary>
    public static async Task<DriverExportResponse> ExportAsync(DriverExportRequest? request, CancellationToken ct = default)
    {
        if (request is null) return new(false, null, null, 0, 0, ["Requisição vazia."]);
        try
        {
            var r = await new DriverBackupService().ExportAsync(request.DestinationFolder, request.PublishedName, null, ct).ConfigureAwait(false);
            return new(r.Success, r.BackupFolder, r.ManifestPath, r.ExportedCount, r.FailedCount, r.Messages.ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, null, null, 0, 0, [$"Falha no backup: {ex.Message}"]); }
    }

    /// <summary>AgentOps.DriverInstall — DriverInstallRequest(InfPath). Valida caminho + assinatura do catálogo antes do pnputil.</summary>
    public static async Task<DriverInstallResponse> InstallAsync(DriverInstallRequest? request, CancellationToken ct = default)
    {
        if (request is null) return new(false, false, -1, null, [], ["Requisição vazia."]);
        try
        {
            var r = await new DriverInstallService().InstallAsync(request.InfPath, ct).ConfigureAwait(false);
            return new(r.Success, r.RebootRequired, r.ExitCode, r.PublishedName, r.DeviceChanges.ToList(), r.Messages.ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, false, -1, null, [], [$"Falha na instalação: {ex.Message}"]); }
    }

    /// <summary>"driver.switch" (proposto) — DriverSwitchRequest(InstanceId, PublishedName).</summary>
    public static async Task<DriverSwitchResponse> SwitchAsync(DriverSwitchRequest? request, CancellationToken ct = default)
    {
        if (request is null) return new(false, false, null, null, ["Requisição vazia."]);
        try
        {
            var r = await new DriverInstallService().SwitchToPackageAsync(request.InstanceId, request.PublishedName, ct).ConfigureAwait(false);
            return new(r.Success, r.RebootRequired, r.Before, r.After, r.Messages.ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, false, null, null, [$"Falha na troca de driver: {ex.Message}"]); }
    }
}
