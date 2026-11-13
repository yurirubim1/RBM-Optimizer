using System.Text.Json;
using Rbm.Core.Agent;
using Rbm.Network.Adapters;

namespace Rbm.Network.Agent;

/// <summary>
/// Operações elevadas deste módulo (PROPOSTA — os nomes ainda precisam ser adicionados a <see cref="AgentOps"/> em Rbm.Core).
/// Requer elevação — executado via RBM.Agent.
/// </summary>
public static class NetworkAgentOps
{
    /// <summary>Payload: <see cref="AdvancedPropertySetRequest"/>; resposta: <see cref="AdvancedPropertySetResponse"/>.</summary>
    public const string AdvancedPropertySet = "net.advprop.set";

    /// <summary>
    /// Leitura elevada (payload: <see cref="AdvancedPropertyReadRequest"/>; resposta: <see cref="AdapterAdvancedSettings"/>).
    /// Útil porque MSFT_NetAdapterPowerManagementSettingData pode falhar sem elevação.
    /// </summary>
    public const string AdvancedPropertyRead = "net.advprop.read";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { AdvancedPropertySet, AdvancedPropertyRead };
}

public sealed record AdvancedPropertyReadRequest(string InterfaceGuid);

/// <summary>Despachante para o RBM.Agent: valida o payload e executa a operação. Payload/operação inválidos lançam ArgumentException (o agente converte em AgentResponse com erro).</summary>
public static class NetworkAgentHandler
{
    public static async Task<object?> HandleAsync(string op, JsonElement? payload, CancellationToken ct = default)
    {
        switch (op)
        {
            case NetworkAgentOps.AdvancedPropertySet:
            {
                var req = Deserialize<AdvancedPropertySetRequest>(payload);
                var shape = AdvancedPropertyValidator.ValidateShape(req);
                if (!shape.Success)
                    return new AdvancedPropertySetResponse(false, req?.InterfaceGuid ?? "", null, req?.RegistryKeyword ?? "", null, null, null, null, false, shape.Message, shape.ErrorCode);
                return await AdvancedPropertyService.SetAsync(req!, ct).ConfigureAwait(false);
            }
            case NetworkAgentOps.AdvancedPropertyRead:
            {
                var req = Deserialize<AdvancedPropertyReadRequest>(payload);
                if (req is null || !Guid.TryParse(req.InterfaceGuid, out _)) throw new ArgumentException("GUID de interface inválido.");
                return await AdvancedPropertyService.ReadAsync(req.InterfaceGuid, ct).ConfigureAwait(false);
            }
            default:
                throw new ArgumentException($"Operação não suportada pelo módulo de rede: {op}");
        }
    }

    private static T? Deserialize<T>(JsonElement? payload) where T : class
    {
        if (payload is not { } p || p.ValueKind != JsonValueKind.Object) return null;
        try { return p.Deserialize<T>(AgentJson.Options); } catch (JsonException) { return null; }
    }
}
