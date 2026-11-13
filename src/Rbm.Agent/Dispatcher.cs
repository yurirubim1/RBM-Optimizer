using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Tweaks;
using Rbm.Optimization;
using Rbm.Optimization.Providers;
using Rbm.Restore;
using Serilog;

namespace Rbm.Agent;

/// <summary>
/// Roteia operações da allow-list. Toda validação de parâmetros acontece AQUI (no processo elevado),
/// independentemente do que a UI já tenha validado.
/// </summary>
internal sealed partial class Dispatcher
{
    private readonly CancellationTokenSource _lifetime;
    private readonly TweakCatalog _catalog = TweakCatalog.LoadEmbedded();
    private readonly TweakExecutor _executor = new();

    public Dispatcher(CancellationTokenSource lifetime) => _lifetime = lifetime;

    public async Task<AgentResponse> HandleAsync(AgentRequest req, CancellationToken ct)
    {
        try
        {
            object? result = req.Op switch
            {
                AgentOps.Ping => new { pid = Environment.ProcessId, elevated = true, catalog = _catalog.Tweaks.Count },
                AgentOps.Shutdown => Shutdown(),
                AgentOps.TweakRead => TweakRead(P<TweakReadRequest>(req)),
                AgentOps.TweakApply => TweakApply(P<TweakApplyRequest>(req)),
                AgentOps.TweakRevert => TweakRevert(P<TweakRevertRequest>(req)),
                AgentOps.RestorePointCreate => RestorePointService.Create(P<RestorePointRequest>(req).Description),
                AgentOps.ServiceSetStart => ServiceSetStart(P<ServiceStartRequest>(req)),
                _ => await HandleModuleOpAsync(req, ct),
            };
            if (result is OperationResult { Success: false } fail) return Fail(req, fail.Message);
            // Respostas de módulos com "Success=false" viram falha no protocolo (Ok=false), com a mensagem do módulo.
            if (result is not null && result.GetType().GetProperty("Success")?.GetValue(result) is false)
            {
                var t = result.GetType();
                var msg = t.GetProperty("Error")?.GetValue(result) as string ?? t.GetProperty("Message")?.GetValue(result) as string ?? "A operação falhou.";
                return new AgentResponse(req.Id, false, msg, JsonSerializer.SerializeToElement(result, t, AgentJson.Options));
            }
            return new AgentResponse(req.Id, true, null, result is null ? null : JsonSerializer.SerializeToElement(result, result.GetType(), AgentJson.Options));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or FormatException or NotSupportedException)
        {
            return Fail(req, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro inesperado na operação {Op}", req.Op);
            return Fail(req, "Erro inesperado: " + ex.Message);
        }
    }

    private static AgentResponse Fail(AgentRequest req, string msg) => new(req.Id, false, msg, null);

    private static T P<T>(AgentRequest req) =>
        req.Payload is { } p ? p.Deserialize<T>(AgentJson.Options) ?? throw new ArgumentException("Payload vazio.") : throw new ArgumentException("Payload ausente.");

    private object Shutdown()
    {
        _ = Task.Delay(200).ContinueWith(_ => _lifetime.Cancel());
        return new { shuttingDown = true };
    }

    private TweakManifest Manifest(string id) => _catalog.Get(id) ?? throw new ArgumentException($"Tweak desconhecido: {id}");

    private TweakStatus TweakRead(TweakReadRequest r) => _executor.ReadStatus(Manifest(r.TweakId), elevated: true);

    private TweakApplyResponse TweakApply(TweakApplyRequest r)
    {
        var m = Manifest(r.TweakId);
        if (m.Grade == EvidenceGrade.E) throw new InvalidOperationException("Técnicas grau E (obsoletas/inseguras) são apenas documentadas e não podem ser aplicadas.");
        if (m.GetOption(r.OptionKey) is null) throw new ArgumentException($"Opção inválida: {r.OptionKey}");
        // O agente só toca alvos que exigem privilégio. Alvos HKCU são aplicados pela UI no contexto do usuário.
        return _executor.Apply(m, r.OptionKey, TweakExecutor.IsElevatedScope, elevatedContext: true);
    }

    private TweakRevertResponse TweakRevert(TweakRevertRequest r)
    {
        var m = Manifest(r.TweakId);
        var changes = r.Changes ?? new List<TargetChange>();
        if (changes.Any(c => c.Target is null || !TweakExecutor.IsElevatedScope(c.Target)))
            throw new ArgumentException("A reversão elevada só aceita alvos privilegiados.");
        var err = CatalogValidator.ValidateRevertTargets(m, changes);
        if (err is not null) throw new ArgumentException(err);
        return _executor.Revert(changes, r.Force);
    }

    private ServiceStartResponse ServiceSetStart(ServiceStartRequest r)
    {
        var prev = ServiceConfig.ReadStartMode(r.ServiceName) ?? throw new ArgumentException("Serviço não encontrado.");
        ServiceConfig.SetStartMode(r.ServiceName, r.Mode);
        var now = ServiceConfig.ReadStartMode(r.ServiceName) ?? r.Mode;
        if (now != r.Mode) throw new InvalidOperationException($"Verificação falhou: modo atual {now}.");
        return new ServiceStartResponse(prev, now);
    }

    /// <summary>Operações dos módulos (drivers, processos, latência, etc.) — ver Dispatcher.Modules.cs.</summary>
    private partial Task<object?> HandleModuleOpAsync(AgentRequest req, CancellationToken ct);
}
