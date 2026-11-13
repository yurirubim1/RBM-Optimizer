namespace Rbm.Core.Tweaks;

public enum ChangeStatus
{
    /// <summary>Aplicado e verificado por releitura.</summary>
    Applied,
    /// <summary>Falhou; alterações parciais foram desfeitas automaticamente.</summary>
    FailedRolledBack,
    /// <summary>Falhou e o rollback automático também falhou (estado precisa de atenção).</summary>
    FailedPartial,
    /// <summary>Revertido pelo usuário.</summary>
    Reverted,
    /// <summary>Aplicado, aguardando reinicialização para vigorar.</summary>
    PendingRestart,
}

/// <summary>Registro transacional de uma aplicação de tweak (persistido no histórico).</summary>
public sealed class TweakChangeRecord
{
    public Guid ChangeId { get; set; } = Guid.NewGuid();
    public Guid? BatchId { get; set; }
    public string TweakId { get; set; } = "";
    public string? FromOption { get; set; }
    public string ToOption { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string User { get; set; } = Environment.UserName;
    public List<TargetChange> Changes { get; set; } = new();
    public ChangeStatus Status { get; set; }
    public string? Error { get; set; }
    public RestartRequirement Restart { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
    public string? ProfileName { get; set; }
}

/// <summary>Alteração de um alvo: valor anterior, valor escrito e se a releitura confirmou.</summary>
public sealed class TargetChange
{
    /// <summary>Alvo resolvido (ex.: esquema de energia "active" substituído pelo GUID real).</summary>
    public SettingTarget Target { get; set; } = null!;
    public SettingValue Before { get; set; } = SettingValue.Absent;
    public SettingValue After { get; set; } = SettingValue.Absent;
    public bool Verified { get; set; }
    public bool Elevated { get; set; }
}

/// <summary>Divergência entre o estado salvo no histórico e o estado atual (alteração externa).</summary>
public sealed record DriftReport(string IdentityKey, string Display, SettingValue Expected, SettingValue Actual);
