using System.Text;
using System.Text.RegularExpressions;

namespace Rbm.Latency;

/// <summary>
/// Interpretação pura (testável) das configurações de interrupção de dispositivos PCI:
/// <list type="bullet">
/// <item><c>Interrupt Management\MessageSignaledInterruptProperties</c>: MSISupported (REG_DWORD, 1 = MSI habilitado) e
/// MessageNumberLimit (máximo de mensagens; MSI: 1,2,4,8,16; MSI-X: até 2048). Fonte: Microsoft Learn,
/// "Enabling Message-Signaled Interrupts in the Registry".</item>
/// <item><c>Interrupt Management\Affinity Policy</c>: DevicePolicy (IRQ_DEVICE_POLICY), DevicePriority (IRQ_PRIORITY),
/// AssignmentSetOverride (KAFFINITY binário). Fonte: Microsoft Learn, "Interrupt Affinity and Priority".</item>
/// <item>IRQs de WMI (Win32_PnPAllocatedResource → Win32_IRQResource): números ≥ 0x80000000 (negativos como int32) indicam MSI/MSI-X.</item>
/// </list>
/// </summary>
public static class InterruptPolicyInterpreter
{
    private static readonly Regex IrqRef = new(@"Win32_IRQResource\.IRQNumber=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceRef = new(@"Win32_PnPEntity\.DeviceID=""(.*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? DevicePolicyName(int? value) => value switch
    {
        null => null,
        0 => "IrqPolicyMachineDefault (padrão do sistema)",
        1 => "IrqPolicyAllCloseProcessors (todos os processadores próximos)",
        2 => "IrqPolicyOneCloseProcessor (um processador próximo)",
        3 => "IrqPolicyAllProcessorsInMachine (todos os processadores)",
        4 => "IrqPolicySpecifiedProcessors (processadores em AssignmentSetOverride)",
        5 => "IrqPolicySpreadMessagesAcrossAllProcessors (distribui mensagens entre processadores)",
        6 => "IrqPolicyAllProcessorsInMachineWhenSteered",
        _ => $"Valor desconhecido ({value})",
    };

    public static string? DevicePriorityName(int? value) => value switch
    {
        null => null,
        0 => "IrqPriorityUndefined (indefinida)",
        1 => "IrqPriorityLow (baixa)",
        2 => "IrqPriorityNormal (normal)",
        3 => "IrqPriorityHigh (alta)",
        _ => $"Valor desconhecido ({value})",
    };

    /// <summary>Converte o número de IRQ do WMI (uint32) para a convenção do Gerenciador de Dispositivos (MSI = negativo).</summary>
    public static long NormalizeIrq(ulong raw) => raw is >= 0x80000000UL and <= 0xFFFFFFFFUL ? unchecked((int)(uint)raw) : (long)raw;

    /// <summary>MSI se algum IRQ for negativo; linha (INTx) se houver só IRQs positivos; desconhecido se não houver IRQs.</summary>
    public static InterruptMode EffectiveMode(IReadOnlyList<long> irqs) =>
        irqs.Count == 0 ? InterruptMode.Unknown : irqs.Any(i => i < 0) ? InterruptMode.Msi : InterruptMode.LineBased;

    /// <summary>AssignmentSetOverride pode ser REG_BINARY (KAFFINITY little-endian), DWORD ou QWORD.</summary>
    public static ulong? ParseAffinityMask(object? value) => value switch
    {
        null => null,
        int i => unchecked((uint)i),
        long l => unchecked((ulong)l),
        byte[] { Length: > 0 } b => ToUInt64LittleEndian(b),
        _ => null,
    };

    private static ulong ToUInt64LittleEndian(byte[] b)
    {
        ulong v = 0;
        for (int i = Math.Min(b.Length, 8) - 1; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    /// <summary>Lista de CPUs de uma máscara de afinidade (ex.: 0x5 → "0, 2").</summary>
    public static string MaskToCpuList(ulong mask)
    {
        var cpus = Enumerable.Range(0, 64).Where(i => (mask & (1UL << i)) != 0).ToList();
        return cpus.Count == 0 ? "nenhum" : string.Join(", ", cpus);
    }

    /// <summary>
    /// Agrupa referências de Win32_PnPAllocatedResource (Antecedent/Dependent) em IRQs por DeviceID (chave sem diferenciar maiúsculas).
    /// Referências que não sejam de Win32_IRQResource são ignoradas.
    /// </summary>
    public static Dictionary<string, List<long>> ParseAllocatedResources(IEnumerable<(string Antecedent, string Dependent)> refs)
    {
        var map = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ante, dep) in refs)
        {
            var mi = IrqRef.Match(ante ?? "");
            if (!mi.Success || !ulong.TryParse(mi.Groups[1].Value, out var raw)) continue;
            var md = DeviceRef.Match(dep ?? "");
            if (!md.Success) continue;
            string id = md.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<long>();
            long irq = NormalizeIrq(raw);
            if (!list.Contains(irq)) list.Add(irq);
        }
        return map;
    }

    /// <summary>Texto pt-BR explicando o estado (registro × alocação efetiva).</summary>
    public static string Interpret(int? msiSupported, int? messageNumberLimit, InterruptMode effective, IReadOnlyList<long> irqs,
        int? devicePolicy, ulong? assignmentSetOverride, int? devicePriority)
    {
        var sb = new StringBuilder();
        sb.Append(msiSupported switch
        {
            null => "MSISupported ausente no registro (o INF não habilitou MSI por este valor). ",
            0 => "MSISupported = 0: MSI desabilitado no registro. ",
            _ => "MSISupported = 1: MSI habilitado no registro. ",
        });
        if (messageNumberLimit is { } lim) sb.Append($"Limite de mensagens: {lim}. ");

        int msiCount = irqs.Count(i => i < 0);
        sb.Append(effective switch
        {
            InterruptMode.Msi => $"Alocação atual: MSI/MSI-X ({msiCount} mensagem(ns) visível(is) no WMI). ",
            InterruptMode.LineBased => $"Alocação atual: interrupção por linha (IRQ {string.Join(", ", irqs)}), possivelmente compartilhada. ",
            _ => "Nenhuma IRQ alocada visível no WMI (dispositivo sem interrupção, desativado ou informação indisponível). ",
        });
        if (msiSupported == 1 && effective == InterruptMode.LineBased)
            sb.Append("Registro pede MSI mas a alocação é por linha: o dispositivo/driver pode não suportar MSI ou é preciso reiniciar. ");
        if (msiSupported is null or 0 && effective == InterruptMode.Msi)
            sb.Append("Alocação em MSI sem o valor de registro: o driver de barramento (ex.: pci.sys em portas raiz) habilita MSI por conta própria. ");

        if (devicePolicy is { } dp) sb.Append($"Política de afinidade: {DevicePolicyName(dp)}. ");
        if (assignmentSetOverride is { } mask && devicePolicy == 4) sb.Append($"CPUs atribuídas: {MaskToCpuList(mask)}. ");
        if (devicePriority is { } pr) sb.Append($"Prioridade: {DevicePriorityName(pr)}. ");
        sb.Append("Somente leitura — alterações de modo MSI/afinidade são feitas pelo motor de ajustes, com reversão.");
        return sb.ToString().Trim();
    }
}
