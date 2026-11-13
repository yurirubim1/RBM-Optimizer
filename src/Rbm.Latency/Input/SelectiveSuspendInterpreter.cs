using System.Text;

namespace Rbm.Latency;

/// <summary>
/// Interpretação (pura, somente leitura) dos valores de energia em <c>Enum\&lt;instância&gt;\Device Parameters</c>:
/// <list type="bullet">
/// <item><c>SelectiveSuspendEnabled</c> — lido pela pilha HID-USB (hidusb): 1 permite suspender o dispositivo ocioso; 0 impede.</item>
/// <item><c>EnhancedPowerManagementEnabled</c> — definido pelo INF/hub USB: habilita o gerenciamento de energia do dispositivo USB (necessário para suspensão).</item>
/// <item><c>AllowIdleIrpInD3</c> — permite que a solicitação de ociosidade (idle IRP) seja concluída com o dispositivo em D3.</item>
/// <item><c>DeviceSelectiveSuspended</c> / <c>SelectiveSuspendOn</c> — indicadores gravados pelo driver (estado/uso), informativos.</item>
/// </list>
/// A suspensão só ocorre se, além disso, a configuração global "Suspensão seletiva de USB" do plano de energia estiver ativada.
/// Suspensão acontece apenas com o dispositivo OCIOSO: durante o uso contínuo ela não age; o efeito possível é um atraso no primeiro evento após a ociosidade.
/// </summary>
public static class SelectiveSuspendInterpreter
{
    public static UsbPowerSettings Interpret(int? selectiveSuspendEnabled, int? enhancedPowerManagementEnabled, int? allowIdleIrpInD3,
        int? deviceSelectiveSuspended = null, int? selectiveSuspendOn = null)
    {
        SelectiveSuspendState state;
        var sb = new StringBuilder();

        if (selectiveSuspendEnabled is null && enhancedPowerManagementEnabled is null && allowIdleIrpInD3 is null && selectiveSuspendOn is null)
        {
            state = SelectiveSuspendState.NotConfigured;
            sb.Append("Nenhum valor de suspensão seletiva no registro deste nó; vale o padrão do driver/classe.");
        }
        else if (selectiveSuspendEnabled is { } ss)
        {
            bool on = ss != 0;
            state = on ? SelectiveSuspendState.Enabled : SelectiveSuspendState.Disabled;
            if (selectiveSuspendOn is { } sson && (sson != 0) != on) state = SelectiveSuspendState.Mixed;
            sb.Append(on
                ? "SelectiveSuspendEnabled=1: o driver HID pode suspender o dispositivo quando ocioso (se a política global permitir)."
                : "SelectiveSuspendEnabled=0: o driver HID não suspende o dispositivo ocioso.");
            if (enhancedPowerManagementEnabled == 1 && !on)
                sb.Append(" EnhancedPowerManagementEnabled=1 apenas habilita o gerenciamento de energia no nível USB; com SelectiveSuspendEnabled=0 a suspensão não é solicitada pelo driver HID.");
        }
        else if (enhancedPowerManagementEnabled is { } epm)
        {
            state = epm != 0 ? SelectiveSuspendState.Enabled : SelectiveSuspendState.Disabled;
            sb.Append(epm != 0
                ? "EnhancedPowerManagementEnabled=1: gerenciamento de energia USB habilitado — o dispositivo pode ser suspenso quando ocioso, se o driver e a política global permitirem."
                : "EnhancedPowerManagementEnabled=0: gerenciamento de energia USB desabilitado para este dispositivo.");
        }
        else
        {
            state = selectiveSuspendOn is { } sson && sson != 0 ? SelectiveSuspendState.Enabled : SelectiveSuspendState.NotConfigured;
            sb.Append("Apenas indicadores parciais presentes; vale o padrão do driver.");
        }

        if (allowIdleIrpInD3 == 1) sb.Append(" AllowIdleIrpInD3=1 (ociosidade permitida em D3).");
        if (deviceSelectiveSuspended == 1) sb.Append(" DeviceSelectiveSuspended=1: o driver registrou que o dispositivo já entrou em suspensão seletiva.");
        sb.Append(" Interpretação somente leitura; a suspensão age apenas com o dispositivo ocioso.");

        return new UsbPowerSettings(selectiveSuspendEnabled, enhancedPowerManagementEnabled, allowIdleIrpInD3, deviceSelectiveSuspended, selectiveSuspendOn, state, sb.ToString());
    }

    /// <summary>Interpreta a configuração global (índice AC/DC do plano ativo): 0 = desativada, 1 = ativada.</summary>
    public static string InterpretGlobal(int? ac, int? dc)
    {
        static string V(int? v) => v switch { null => "indisponível", 0 => "Desativada", 1 => "Ativada", _ => $"valor {v}" };
        return $"Suspensão seletiva de USB no plano ativo — na tomada: {V(ac)}; na bateria: {V(dc)}. " +
               (ac == 1
                   ? "Com a opção ativada, dispositivos USB ociosos cujo driver permite podem ser suspensos; dispositivos em uso contínuo não são afetados."
                   : ac == 0 ? "Com a opção desativada, o Windows não suspende seletivamente dispositivos USB ociosos (exceto por políticas do próprio driver)." : "");
    }

    /// <summary>Converte um valor de registro (DWORD, QWORD, binário ou texto) em inteiro.</summary>
    public static int? ToInt(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        byte[] { Length: > 0 } b => b.Length >= 4 ? BitConverter.ToInt32(b, 0) : b[0],
        string s when int.TryParse(s.Trim(), out var x) => x,
        _ => null,
    };
}
