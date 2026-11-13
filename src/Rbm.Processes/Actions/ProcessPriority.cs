using Rbm.Security;

namespace Rbm.Processes;

/// <summary>Classe de prioridade. <see cref="RealTime"/> existe só para LEITURA — nunca é aplicada pelo RBM Optimizer.</summary>
public enum ProcessPriority { Idle, BelowNormal, Normal, AboveNormal, High, RealTime }

public static class ProcessPriorityMap
{
    public const uint IDLE_PRIORITY_CLASS = 0x40;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;
    public const uint HIGH_PRIORITY_CLASS = 0x80;
    public const uint REALTIME_PRIORITY_CLASS = 0x100;

    public static uint ToWin32(ProcessPriority p) => p switch
    {
        ProcessPriority.Idle => IDLE_PRIORITY_CLASS,
        ProcessPriority.BelowNormal => BELOW_NORMAL_PRIORITY_CLASS,
        ProcessPriority.Normal => NORMAL_PRIORITY_CLASS,
        ProcessPriority.AboveNormal => ABOVE_NORMAL_PRIORITY_CLASS,
        ProcessPriority.High => HIGH_PRIORITY_CLASS,
        ProcessPriority.RealTime => REALTIME_PRIORITY_CLASS,
        _ => NORMAL_PRIORITY_CLASS,
    };

    public static ProcessPriority? FromWin32(uint value) => value switch
    {
        IDLE_PRIORITY_CLASS => ProcessPriority.Idle,
        BELOW_NORMAL_PRIORITY_CLASS => ProcessPriority.BelowNormal,
        NORMAL_PRIORITY_CLASS => ProcessPriority.Normal,
        ABOVE_NORMAL_PRIORITY_CLASS => ProcessPriority.AboveNormal,
        HIGH_PRIORITY_CLASS => ProcessPriority.High,
        REALTIME_PRIORITY_CLASS => ProcessPriority.RealTime,
        _ => null,
    };

    public static string Display(ProcessPriority p) => p switch
    {
        ProcessPriority.Idle => "Baixa",
        ProcessPriority.BelowNormal => "Abaixo do normal",
        ProcessPriority.Normal => "Normal",
        ProcessPriority.AboveNormal => "Acima do normal",
        ProcessPriority.High => "Alta",
        ProcessPriority.RealTime => "Tempo real",
        _ => p.ToString(),
    };

    /// <summary>Valida uma prioridade a ser APLICADA (rejeita tempo real e valores fora da allow-list de <see cref="Protection"/>).</summary>
    public static bool IsAllowedToSet(ProcessPriority p, out string? error)
    {
        if (p == ProcessPriority.RealTime || !Protection.AllowedPriorities.Contains(p.ToString()))
        {
            error = "Prioridade \"Tempo real\" é bloqueada: pode congelar o sistema (entrada, áudio e o próprio Windows).";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Interpreta uma string vinda do canal (payload do agente). Só aceita nomes exatos da allow-list.</summary>
    public static bool TryParseAllowed(string? value, out ProcessPriority priority, out string? error)
    {
        priority = ProcessPriority.Normal;
        if (string.IsNullOrWhiteSpace(value)) { error = "Prioridade não informada."; return false; }
        string v = value.Trim();
        if (v.Equals("RealTime", StringComparison.OrdinalIgnoreCase) || v.Equals("Realtime", StringComparison.OrdinalIgnoreCase))
        {
            error = "Prioridade \"Tempo real\" é bloqueada: pode congelar o sistema.";
            return false;
        }
        if (!Protection.AllowedPriorities.Contains(v) || !Enum.TryParse(v, ignoreCase: true, out priority) || int.TryParse(v, out _))
        {
            error = $"Prioridade inválida: \"{v}\". Valores aceitos: {string.Join(", ", Protection.AllowedPriorities)}.";
            return false;
        }
        error = null;
        return true;
    }
}
