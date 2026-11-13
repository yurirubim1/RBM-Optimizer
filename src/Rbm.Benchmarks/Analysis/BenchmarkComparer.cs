using Rbm.Core;

namespace Rbm.Benchmarks;

/// <summary>Comparação de uma métrica secundária.</summary>
public sealed record MetricComparison(string Key, string Name, string Unit, bool HigherIsBetter, ComparisonResult Comparison);

/// <summary>Resultado da comparação antes/depois de dois resultados do mesmo benchmark.</summary>
public sealed record BenchmarkComparison
{
    public string BenchmarkId { get; init; } = "";
    /// <summary>False quando a comparação não tem validade (benchmark/parâmetros/máquina diferentes, execução incompleta).</summary>
    public bool Comparable { get; init; }
    /// <summary>Comparação da métrica principal (null se não comparável).</summary>
    public ComparisonResult? Primary { get; init; }
    public IReadOnlyList<MetricComparison> Metrics { get; init; } = Array.Empty<MetricComparison>();
    /// <summary>Diferenças de condições que podem explicar variações (pt-BR).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Conclusão em pt-BR.</summary>
    public string Verdict { get; init; } = "";
    /// <summary>True somente se a diferença é estatisticamente significativa, acima do mínimo, e sem avisos de condições.</summary>
    public bool ConfidentChange { get; init; }
}

/// <summary>
/// Compara dois <see cref="BenchmarkResult"/> do mesmo benchmark usando <see cref="Stats.Compare"/> (teste t de Welch),
/// respeitando HigherIsBetter, e verifica a comparabilidade das condições. Pequenas flutuações nunca são declaradas ganho.
/// </summary>
public static class BenchmarkComparer
{
    /// <summary>Parâmetros ignorados na verificação de igualdade (nenhum: todos definem a carga; dados informativos ficam em Info).</summary>
    private static readonly HashSet<string> s_ignoredParameters = new(StringComparer.Ordinal);

    public static BenchmarkComparison Compare(BenchmarkResult before, BenchmarkResult after, double minMeaningfulPercent = 2.0)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        BenchmarkComparison NotComparable(string why, IEnumerable<string>? extra = null) => new()
        {
            BenchmarkId = after.BenchmarkId,
            Comparable = false,
            Warnings = (extra ?? Array.Empty<string>()).ToArray(),
            Verdict = $"Não comparável: {why}",
        };

        if (!string.Equals(before.BenchmarkId, after.BenchmarkId, StringComparison.Ordinal))
            return NotComparable($"benchmarks diferentes ('{before.BenchmarkId}' × '{after.BenchmarkId}').");
        if (before.Status != BenchmarkStatus.Completed || after.Status != BenchmarkStatus.Completed)
            return NotComparable("uma das execuções não foi concluída (cancelada, com falha ou não implementada).");
        if (!string.Equals(before.EngineVersion, after.EngineVersion, StringComparison.Ordinal))
            return NotComparable($"versões diferentes da metodologia ({before.EngineVersion} × {after.EngineVersion}).");

        var paramDiffs = ParameterDifferences(before.Parameters, after.Parameters).ToList();
        if (paramDiffs.Count > 0)
            return NotComparable("parâmetros de carga diferentes — " + string.Join("; ", paramDiffs) + ".");

        var warnings = new List<string>();
        var bc = before.Conditions;
        var ac = after.Conditions;
        if (bc is null || ac is null)
            warnings.Add("Condições de teste não registradas em uma das execuções; não é possível verificar se o ambiente era o mesmo.");
        else
        {
            if (!string.IsNullOrEmpty(bc.CpuName) && !string.IsNullOrEmpty(ac.CpuName) && !string.Equals(bc.CpuName, ac.CpuName, StringComparison.Ordinal))
                return NotComparable($"processadores diferentes ('{bc.CpuName}' × '{ac.CpuName}'). Compare apenas na mesma máquina.");
            if (bc.LogicalProcessors != ac.LogicalProcessors)
                warnings.Add($"Número de processadores lógicos mudou ({bc.LogicalProcessors} → {ac.LogicalProcessors}) — SMT/núcleos alterados na BIOS?");
            if (bc.PowerPlanGuid != ac.PowerPlanGuid)
                warnings.Add($"Plano de energia mudou ('{bc.PowerPlanName ?? "?"}' → '{ac.PowerPlanName ?? "?"}'). A diferença pode ser causada pelo plano.");
            if (bc.OnAcPower != ac.OnAcPower)
                warnings.Add($"Fonte de energia mudou ({Ac(bc.OnAcPower)} → {Ac(ac.OnAcPower)}).");
            if (bc.WindowsBuild != ac.WindowsBuild || bc.WindowsUbr != ac.WindowsUbr)
                warnings.Add($"Versão do Windows mudou ({bc.WindowsVersion} → {ac.WindowsVersion}); atualizações podem alterar o desempenho.");
            if (!string.Equals(bc.RuntimeVersion, ac.RuntimeVersion, StringComparison.Ordinal))
                warnings.Add($"Versão do .NET mudou ({bc.RuntimeVersion} → {ac.RuntimeVersion}); o JIT pode gerar código diferente.");
            if (before.Category == BenchmarkCategory.Responsiveness && bc.TimerResolutionCurrentMs != ac.TimerResolutionCurrentMs)
                warnings.Add($"Resolução do timer do sistema diferente ({bc.TimerResolutionCurrentMs:0.###} ms → {ac.TimerResolutionCurrentMs:0.###} ms).");
            if (!bc.JitOptimized || !ac.JitOptimized || bc.DebuggerAttached || ac.DebuggerAttached)
                warnings.Add("Uma das execuções foi feita sem otimizações do JIT ou com depurador — não representativa.");
        }
        if (before.UnstableConditions) warnings.Add("A execução 'antes' foi marcada com condições instáveis.");
        if (after.UnstableConditions) warnings.Add("A execução 'depois' foi marcada com condições instáveis.");
        if (before.Samples.Count < 3 || after.Samples.Count < 3)
            warnings.Add("Poucas amostras (menos de 3 por lado): o teste estatístico tem pouco poder; repita com mais iterações.");

        var primary = Stats.Compare(before.Samples, after.Samples, after.HigherIsBetter, minMeaningfulPercent);
        var metrics = new List<MetricComparison>();
        foreach (var am in after.Metrics)
        {
            var bm = before.GetMetric(am.Key);
            if (bm is null || !string.Equals(bm.Unit, am.Unit, StringComparison.Ordinal)) continue;
            metrics.Add(new MetricComparison(am.Key, am.Name, am.Unit, am.HigherIsBetter,
                Stats.Compare(bm.Samples, am.Samples, am.HigherIsBetter, minMeaningfulPercent)));
        }

        string verdict = primary.Verdict;
        bool confident = primary.Significant && warnings.Count == 0;
        if (primary.Significant && warnings.Count > 0)
            verdict += " Atenção: as condições de teste diferem ou foram instáveis — a diferença pode não ser causada pela alteração avaliada. Repita em condições equivalentes.";

        return new BenchmarkComparison
        {
            BenchmarkId = after.BenchmarkId,
            Comparable = true,
            Primary = primary,
            Metrics = metrics,
            Warnings = warnings,
            Verdict = verdict,
            ConfidentChange = confident,
        };
    }

    private static string Ac(bool? ac) => ac switch { true => "tomada", false => "bateria", _ => "desconhecida" };

    private static IEnumerable<string> ParameterDifferences(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        foreach (var key in a.Keys.Union(b.Keys).Where(k => !s_ignoredParameters.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var va);
            b.TryGetValue(key, out var vb);
            if (!string.Equals(va, vb, StringComparison.Ordinal)) yield return $"{key}: {va ?? "—"} → {vb ?? "—"}";
        }
    }
}

/// <summary>Um componente do índice agregado: razão orientada (&gt; 1 = melhor que a linha de base).</summary>
public sealed record IndexComponent(string BenchmarkId, double Ratio);

/// <summary>Índice agregado relativo à linha de base do PRÓPRIO usuário.</summary>
public sealed record BenchmarkIndexResult(double Index, IReadOnlyList<IndexComponent> Components, IReadOnlyList<string> Excluded, string Definition);

/// <summary>
/// Índice agregado transparente: média geométrica das razões entre a execução atual e a linha de base do próprio usuário
/// (mesma máquina), orientadas para que &gt; 1 signifique melhor (atual/base se maior é melhor; base/atual se menor é melhor).
/// Nunca é calculado contra máquinas de referência.
/// </summary>
public static class BenchmarkIndex
{
    public const string Definition =
        "Índice = média geométrica, sobre os benchmarks comparáveis, de (média atual / média da linha de base) quando maior é melhor, " +
        "ou (média da linha de base / média atual) quando menor é melhor. 1,00 = igual à sua linha de base; 1,05 = 5% melhor em média geométrica. " +
        "A linha de base é uma execução anterior desta mesma máquina; não há máquinas de referência.";

    public static BenchmarkIndexResult Compute(IEnumerable<BenchmarkResult> baseline, IEnumerable<BenchmarkResult> current)
    {
        var baseMap = baseline.Where(r => r.Status == BenchmarkStatus.Completed)
            .GroupBy(r => r.BenchmarkId).ToDictionary(g => g.Key, g => g.Last());
        var comps = new List<IndexComponent>();
        var excluded = new List<string>();
        foreach (var cur in current)
        {
            if (!baseMap.TryGetValue(cur.BenchmarkId, out var b)) { excluded.Add($"{cur.BenchmarkId}: sem linha de base"); continue; }
            var cmp = BenchmarkComparer.Compare(b, cur);
            if (!cmp.Comparable) { excluded.Add($"{cur.BenchmarkId}: {cmp.Verdict}"); continue; }
            double bm = b.Summary.Mean, cm = cur.Summary.Mean;
            if (!(bm > 0) || !(cm > 0)) { excluded.Add($"{cur.BenchmarkId}: média não positiva"); continue; }
            comps.Add(new IndexComponent(cur.BenchmarkId, cur.HigherIsBetter ? cm / bm : bm / cm));
        }
        double index = comps.Count == 0 ? double.NaN : Math.Exp(comps.Average(c => Math.Log(c.Ratio)));
        return new BenchmarkIndexResult(index, comps, excluded, Definition);
    }
}
