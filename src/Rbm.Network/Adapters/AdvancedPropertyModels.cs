namespace Rbm.Network.Adapters;

/// <summary>Como o driver declara os valores aceitos (do INF: enum, int, edit).</summary>
public enum AdvancedValueKind { Enumeration, Numeric, FreeText }

public sealed record AdvancedPropertyOption(string RegistryValue, string DisplayValue);

/// <summary>Faixa numérica declarada pelo driver. <see cref="Base"/> é a base numérica (10 ou 16), não um alinhamento.</summary>
public sealed record NumericRange(long Min, long Max, long Step, int Base);

/// <summary>Descrição factual (pt-BR) de palavras-chave padronizadas NDIS e algumas comuns de fabricantes.</summary>
public sealed record AdvancedKeywordInfo(string Keyword, string Category, string DescriptionPt, bool IsPowerSaving);

/// <summary>Propriedade avançada do driver (MSFT_NetAdapterAdvancedPropertySettingData).</summary>
public sealed record AdvancedProperty
{
    public required string InterfaceGuid { get; init; }
    public required string AdapterName { get; init; }
    public required string InstanceId { get; init; }
    public required string RegistryKeyword { get; init; }
    public required string DisplayName { get; init; }
    public string? DisplayValue { get; init; }
    /// <summary>Valor atual no registro (normalmente um único elemento).</summary>
    public IReadOnlyList<string> RegistryValue { get; init; } = Array.Empty<string>();
    public string? DefaultRegistryValue { get; init; }
    public AdvancedValueKind ValueKind { get; init; }
    public IReadOnlyList<AdvancedPropertyOption> ValidOptions { get; init; } = Array.Empty<AdvancedPropertyOption>();
    public NumericRange? Range { get; init; }
    public AdvancedKeywordInfo? Known { get; init; }

    public string? CurrentRegistryValue => RegistryValue.Count > 0 ? RegistryValue[0] : null;

    /// <summary>Texto de exibição de um valor de registro (pela lista do driver), se existir.</summary>
    public string? DisplayFor(string registryValue) =>
        ValidOptions.FirstOrDefault(o => string.Equals(o.RegistryValue, registryValue, StringComparison.OrdinalIgnoreCase))?.DisplayValue;
}

/// <summary>Estado de "Permitir que o computador desligue este dispositivo para economizar energia".</summary>
public enum PowerSavingState { Unknown, Enabled, Disabled, Unsupported }

public sealed record PowerManagementInfo(
    PowerSavingState AllowComputerToTurnOffDevice,
    string Source,
    int? PnPCapabilitiesRaw,
    string? SelectiveSuspend,
    string? DeviceSleepOnDisconnect,
    string? WakeOnMagicPacket,
    string? UnavailableReason);

/// <summary>Resumo dos recursos de offload/coalescência/energia mais citados em guias de "otimização".</summary>
public sealed record OffloadSummary(
    bool? RssEnabled,
    int? RssReceiveQueues,
    bool? RscIPv4Enabled,
    bool? RscIPv6Enabled,
    string? RscNote,
    string? InterruptModeration,
    string? FlowControl,
    string? EnergyEfficientEthernet,
    string? GreenEthernet,
    string? LargeSendOffloadIPv4,
    string? LargeSendOffloadIPv6,
    string? ReceiveBuffers,
    string? TransmitBuffers,
    string? SpeedDuplex);

public sealed record AdapterAdvancedSettings(
    string InterfaceGuid,
    string AdapterName,
    IReadOnlyList<AdvancedProperty> Properties,
    PowerManagementInfo Power,
    OffloadSummary Summary,
    IReadOnlyList<string> Warnings)
{
    public AdvancedProperty? Find(string keyword) =>
        Properties.FirstOrDefault(p => string.Equals(p.RegistryKeyword, keyword, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Pedido de alteração de UMA propriedade avançada. Requer elevação — executado via RBM.Agent.
/// <see cref="ExpectedCurrentValue"/> (opcional) protege contra alteração concorrente/externa: se o valor atual
/// diferir, nada é escrito. Para reverter, envie o valor anterior com ExpectedCurrentValue = valor aplicado.
/// </summary>
public sealed record AdvancedPropertySetRequest(string InterfaceGuid, string RegistryKeyword, string NewRegistryValue, string? ExpectedCurrentValue = null);

/// <summary>Resposta da alteração. <see cref="PreviousRegistryValue"/> é o valor para rollback.</summary>
public sealed record AdvancedPropertySetResponse(
    bool Success,
    string InterfaceGuid,
    string? AdapterName,
    string RegistryKeyword,
    string? PreviousRegistryValue,
    string? PreviousDisplayValue,
    string? NewRegistryValue,
    string? NewDisplayValue,
    bool Verified,
    string Message,
    string? ErrorCode)
{
    /// <summary>Pedido que desfaz esta alteração (null se nada foi alterado ou não há valor anterior).</summary>
    public AdvancedPropertySetRequest? RollbackRequest =>
        Success && PreviousRegistryValue is not null && NewRegistryValue is not null
            ? new AdvancedPropertySetRequest(InterfaceGuid, RegistryKeyword, PreviousRegistryValue, NewRegistryValue)
            : null;
}
