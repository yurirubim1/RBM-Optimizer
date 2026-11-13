namespace Rbm.Core.Diagnostics;

/// <summary>
/// Problema identificado pelo motor de diagnóstico. Cobre os 10 itens obrigatórios da seção 4:
/// descrição, evidência, causa provável, confiança, gravidade, impacto, solução, riscos, reinício, reversibilidade.
/// </summary>
public sealed class Finding
{
    public string RuleId { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Dados objetivos coletados que sustentam o achado.</summary>
    public List<string> Evidence { get; init; } = new();
    public string ProbableCause { get; init; } = "";
    public Confidence Confidence { get; init; }
    public Severity Severity { get; init; }
    public ImpactLevel ExpectedImpact { get; init; }
    public string Solution { get; init; } = "";
    public string Risks { get; init; } = "";
    public RestartRequirement Restart { get; init; }
    public bool Reversible { get; init; } = true;
    public EffortLevel Effort { get; init; } = EffortLevel.Low;
    /// <summary>Tweaks do catálogo relacionados (ação sugerida).</summary>
    public List<string> RelatedTweaks { get; init; } = new();
    /// <summary>Página do app para investigar (ex.: "Drivers", "Latency").</summary>
    public string? NavigateTo { get; init; }
    /// <summary>Indica que a relação é de associação, não de causa confirmada.</summary>
    public bool CorrelationOnly { get; init; }
}

/// <summary>Contexto passado às regras de diagnóstico.</summary>
public interface IDiagnosticRule
{
    string Id { get; }
    string Category { get; }
    Task<IEnumerable<Finding>> EvaluateAsync(DiagnosticContext context, CancellationToken ct);
}

public sealed class DiagnosticContext
{
    public required Hardware.SystemProfile Profile { get; init; }
    /// <summary>Amostras de telemetria recentes (janela de observação).</summary>
    public required IReadOnlyList<Telemetry.TelemetrySnapshot> Samples { get; init; }
    public IReadOnlyList<Telemetry.ProcessSample> Processes { get; init; } = Array.Empty<Telemetry.ProcessSample>();
    public IDictionary<string, object> Extra { get; } = new Dictionary<string, object>();
}
