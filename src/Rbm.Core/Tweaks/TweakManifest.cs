using System.Text.Json.Serialization;

namespace Rbm.Core.Tweaks;

/// <summary>
/// Manifesto estruturado de uma otimização. Todo tweak do catálogo é descrito por um destes.
/// Os campos cobrem integralmente a seção 5 do escopo (ID, risco, evidência, leitura/aplicação/verificação/reversão).
/// </summary>
public sealed class TweakManifest
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new("");
    public string Category { get; set; } = "";
    public string? Subcategory { get; set; }

    /// <summary>Descrição técnica (o que exatamente muda).</summary>
    public LocalizedText TechnicalDescription { get; set; } = new("");
    /// <summary>Explicação em linguagem acessível.</summary>
    public LocalizedText PlainExplanation { get; set; } = new("");
    /// <summary>Resumo da evidência técnica que sustenta (ou não) o ajuste.</summary>
    public LocalizedText Evidence { get; set; } = new("");
    public List<string> References { get; set; } = new();
    public EvidenceGrade Grade { get; set; } = EvidenceGrade.C;
    public Confidence Confidence { get; set; } = Confidence.Low;
    public RiskLevel Risk { get; set; } = RiskLevel.Low;
    public ImpactLevel Impact { get; set; } = ImpactLevel.Unknown;
    public LocalizedText SideEffects { get; set; } = new("");
    public RestartRequirement Restart { get; set; } = RestartRequirement.None;
    public List<string> DependsOn { get; set; } = new();
    public List<string> ConflictsWith { get; set; } = new();
    public TweakCompatibility Compatibility { get; set; } = new();

    /// <summary>Estados possíveis. Cada opção define os valores de todos os alvos.</summary>
    public List<TweakOption> Options { get; set; } = new();
    /// <summary>Opção equivalente ao padrão do Windows.</summary>
    public string DefaultOption { get; set; } = "default";
    /// <summary>Opção recomendada. Null = manter o padrão (sem evidência de benefício).</summary>
    public string? RecommendedOption { get; set; }

    /// <summary>Faz parte do "Advanced Tweaks Lab" (aplicação somente individual).</summary>
    public bool Experimental { get; set; }
    /// <summary>Ajuste de privacidade (separado de desempenho).</summary>
    public bool Privacy { get; set; }
    public List<string> Tags { get; set; } = new();
    /// <summary>Como medir o efeito (procedimento de benchmark sugerido).</summary>
    public LocalizedText? BenchmarkProcedure { get; set; }
    /// <summary>Notas sobre a origem do tweak (ex.: guias da comunidade).</summary>
    public LocalizedText? Origin { get; set; }

    [JsonIgnore] public bool IsRecommendable => Grade <= EvidenceGrade.B && RecommendedOption is not null;
    [JsonIgnore] public bool IsDocumentationOnly => Grade >= EvidenceGrade.D;

    public TweakOption? GetOption(string key) => Options.FirstOrDefault(o => o.Key == key);

    /// <summary>Todos os alvos distintos (por chave) declarados em qualquer opção.</summary>
    public IEnumerable<SettingTarget> AllTargets() => Options.SelectMany(o => o.Targets).GroupBy(t => t.IdentityKey).Select(g => g.First());
}

public sealed class TweakOption
{
    public string Key { get; set; } = "";
    public LocalizedText Label { get; set; } = new("");
    public LocalizedText? Notes { get; set; }
    public List<SettingTarget> Targets { get; set; } = new();
}

public sealed class TweakCompatibility
{
    public int MinBuild { get; set; }
    public int MaxBuild { get; set; }
    public CpuVendor? CpuVendor { get; set; }
    public GpuVendor? GpuVendor { get; set; }
    public FormFactor? FormFactor { get; set; }
    public bool RequiresHybridCpu { get; set; }
    public bool RequiresBattery { get; set; }
    /// <summary>Requisito expresso em texto (exibido ao usuário).</summary>
    public LocalizedText? Notes { get; set; }
}

/// <summary>Estado efetivo atual de um tweak, determinado por leitura real.</summary>
public enum TweakStateKind { Unknown, MatchesOption, Custom, NeedsElevationToRead, NotApplicable, Error }

public sealed class TweakStatus
{
    public string TweakId { get; init; } = "";
    public TweakStateKind Kind { get; set; }
    /// <summary>Chave da opção que corresponde ao estado lido (quando <see cref="Kind"/> = MatchesOption).</summary>
    public string? CurrentOption { get; set; }
    public List<TargetReading> Readings { get; set; } = new();
    public string? Message { get; set; }
    public DateTimeOffset ReadAt { get; set; } = DateTimeOffset.Now;
}

public sealed record TargetReading(string IdentityKey, string Display, SettingValue Value, string? Error);
