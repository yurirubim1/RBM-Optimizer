using Rbm.Core.Hardware;
using Rbm.Security;

namespace Rbm.Processes;

/// <summary>Presets de afinidade calculados a partir da topologia (CpuInfo.Cores).</summary>
public enum AffinityPreset
{
    /// <summary>Não altera a afinidade.</summary>
    None,
    /// <summary>Usa <see cref="ProcessRule.AffinityMask"/>.</summary>
    CustomMask,
    /// <summary>Só núcleos da maior classe de eficiência (P-cores em CPUs híbridas).</summary>
    PerformanceCoresOnly,
    /// <summary>Todos exceto os da menor classe de eficiência (exclui E-cores; mantém P-cores e intermediários).</summary>
    ExcludeEfficiencyCores,
    /// <summary>Um processador lógico por núcleo físico (sem irmãos SMT).</summary>
    OneThreadPerCore,
    /// <summary>Só núcleos do primeiro domínio de cache L3 (ex.: CCD com 3D V-Cache).</summary>
    FirstCacheDomain,
}

/// <summary>
/// Regra por executável (estilo Process Lasso, simplificada). Aplicada UMA vez a cada processo novo que corresponda.
/// <see cref="ExecutableName"/> aceita nome com ou sem ".exe" e curingas (*, ?). <see cref="Exceptions"/> são padrões
/// de caminho completo (curingas) que excluem a regra (ex.: "C:\Program Files\Jogo\launcher\*").
/// </summary>
public sealed record ProcessRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ExecutableName { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public ProcessPriority? Priority { get; init; }
    public AffinityPreset Affinity { get; init; } = AffinityPreset.None;
    public ulong? AffinityMask { get; init; }
    public IReadOnlyList<string> Exceptions { get; init; } = Array.Empty<string>();
    public string? Note { get; init; }
}

/// <summary>Arquivo de regras (versionado).</summary>
public sealed record ProcessRuleSet
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<ProcessRule> Rules { get; init; } = Array.Empty<ProcessRule>();
}

public static class ProcessRuleValidator
{
    /// <summary>Erros pt-BR (lista vazia = válida).</summary>
    public static IReadOnlyList<string> Validate(ProcessRule rule)
    {
        var errors = new List<string>();
        string name = rule.ExecutableName?.Trim() ?? "";
        if (name.Length == 0) errors.Add("Informe o nome do executável.");
        else if (name.Length > 260) errors.Add("Nome do executável longo demais.");
        else if (name.IndexOfAny(['\\', '/', ':', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            errors.Add("Use apenas o nome do executável (sem caminho).");
        else if (name.Trim('*', '?', '.').Length == 0 || name.Trim('*', '?').Equals(".exe", StringComparison.OrdinalIgnoreCase))
            errors.Add("Curinga amplo demais: a regra atingiria todos os processos.");
        else if (!Glob.HasWildcards(name) && (Protection.IsCriticalProcess(name) || Protection.ManualOnlyProcesses.Contains(Path.GetFileNameWithoutExtension(name))))
            errors.Add($"\"{name}\" é um processo crítico/protegido; regras automáticas nunca o alteram.");

        if (rule.Priority is { } p && !ProcessPriorityMap.IsAllowedToSet(p, out var perr)) errors.Add(perr!);
        if (rule.Affinity == AffinityPreset.CustomMask && (rule.AffinityMask is null or 0))
            errors.Add("Máscara de afinidade personalizada vazia.");
        if (!Enum.IsDefined(rule.Affinity)) errors.Add("Preset de afinidade inválido.");
        if (rule.Priority is null && rule.Affinity == AffinityPreset.None) errors.Add("A regra não define prioridade nem afinidade.");
        foreach (var ex in rule.Exceptions)
            if (string.IsNullOrWhiteSpace(ex) || ex.Trim('*', '?').Length == 0) errors.Add("Exceção vazia ou ampla demais.");
        return errors;
    }
}

public static class ProcessRuleMatcher
{
    /// <summary>Compara o nome do processo com o padrão da regra (".exe" opcional, sem diferenciar maiúsculas).</summary>
    public static bool NameMatches(string pattern, string processName)
    {
        string pat = pattern.Trim();
        if (Glob.HasWildcards(pat))
            return Glob.IsMatch(pat, processName) || Glob.IsMatch(pat, StripExe(processName));
        return StripExe(pat).Equals(StripExe(processName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Regras habilitadas candidatas só pelo nome (barato; nomes exatos antes de curingas).</summary>
    public static IEnumerable<ProcessRule> CandidatesByName(IReadOnlyList<ProcessRule> rules, string processName) =>
        rules.Where(r => r.Enabled && r.ExecutableName.Length > 0 && NameMatches(r.ExecutableName, processName))
             .OrderBy(r => Glob.HasWildcards(r.ExecutableName) ? 1 : 0);

    /// <summary>
    /// Primeira regra aplicável, ou null. Processos críticos/"somente manual" nunca casam. Regras com exceções
    /// exigem o caminho da imagem: se ele for desconhecido, a regra não é aplicada (conservador).
    /// </summary>
    public static ProcessRule? Match(IReadOnlyList<ProcessRule> rules, int pid, string processName, string? imagePath)
    {
        if (!ProcessGuard.IsAutomationAllowed(pid, processName)) return null;
        foreach (var r in CandidatesByName(rules, processName))
        {
            if (r.Exceptions.Count > 0)
            {
                if (imagePath is null) continue;
                if (r.Exceptions.Any(e => IsExcluded(e, imagePath))) continue;
            }
            return r;
        }
        return null;
    }

    public static bool IsExcluded(string exceptionPattern, string imagePath)
    {
        string e = exceptionPattern.Trim();
        if (Glob.HasWildcards(e)) return Glob.IsMatch(e, imagePath);
        // Sem curinga: caminho exato ou pasta (prefixo terminado em '\').
        return imagePath.Equals(e, StringComparison.OrdinalIgnoreCase)
            || imagePath.StartsWith(e.EndsWith('\\') ? e : e + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExe(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
}

/// <summary>Resultado do cálculo de um preset de afinidade.</summary>
public sealed record AffinityComputation(ulong? Mask, string Note);

public static class AffinityPresets
{
    /// <summary>
    /// Calcula a máscara (grupo de processadores 0) para o preset. Null = não aplicável (motivo em Note).
    /// Em CPUs sem núcleos híbridos (ex.: Ryzen 5 5500), presets P/E equivalem a "todos os núcleos" — Note avisa.
    /// </summary>
    public static AffinityComputation Compute(AffinityPreset preset, IReadOnlyList<CoreInfo>? cores, ulong systemMask, ulong? customMask = null)
    {
        if (preset == AffinityPreset.None) return new(null, "Afinidade não alterada.");
        if (preset == AffinityPreset.CustomMask)
        {
            if (customMask is not { } m || m == 0) return new(null, "Máscara personalizada vazia.");
            return ProcessGuard.ValidateAffinityMask(m, systemMask, out var err) ? new(m, "Máscara personalizada.") : new(null, err!);
        }
        if (cores is null || cores.Count == 0) return new(null, "Topologia de núcleos indisponível; preset não aplicado.");

        var g0 = cores.Where(c => c.Group == 0).ToList();
        string groupNote = g0.Count < cores.Count ? " Apenas o grupo de processadores 0 é considerado." : "";
        if (g0.Count == 0) return new(null, "Nenhum núcleo no grupo 0.");

        ulong mask = 0;
        string note;
        switch (preset)
        {
            case AffinityPreset.PerformanceCoresOnly:
            {
                byte max = g0.Max(c => c.EfficiencyClass);
                bool hybrid = g0.Any(c => c.EfficiencyClass != max);
                foreach (var c in g0.Where(c => c.EfficiencyClass == max)) mask |= c.Mask;
                note = hybrid ? "Somente núcleos de desempenho (P-cores)." : "CPU sem núcleos híbridos: equivale a todos os núcleos.";
                break;
            }
            case AffinityPreset.ExcludeEfficiencyCores:
            {
                byte min = g0.Min(c => c.EfficiencyClass);
                bool hybrid = g0.Any(c => c.EfficiencyClass != min);
                foreach (var c in g0.Where(c => !hybrid || c.EfficiencyClass != min)) mask |= c.Mask;
                note = hybrid ? "Núcleos de eficiência (E-cores) excluídos." : "CPU sem núcleos híbridos: equivale a todos os núcleos.";
                break;
            }
            case AffinityPreset.OneThreadPerCore:
            {
                bool smt = false;
                foreach (var c in g0)
                {
                    if (c.Mask == 0) continue;
                    if ((c.Mask & (c.Mask - 1)) != 0) smt = true;
                    mask |= c.Mask & (~c.Mask + 1); // bit mais baixo
                }
                note = smt ? "Um processador lógico por núcleo físico (SMT evitado)." : "CPU sem SMT: equivale a todos os núcleos.";
                break;
            }
            case AffinityPreset.FirstCacheDomain:
            {
                int first = g0.Min(c => c.L3Domain);
                bool multi = g0.Any(c => c.L3Domain != first);
                foreach (var c in g0.Where(c => c.L3Domain == first)) mask |= c.Mask;
                note = multi ? "Somente o primeiro domínio de cache L3." : "CPU com um único domínio L3: equivale a todos os núcleos.";
                break;
            }
            default:
                return new(null, "Preset desconhecido.");
        }
        mask &= systemMask;
        if (mask == 0) return new(null, "A topologia não corresponde à máscara do sistema; preset não aplicado.");
        return new(mask, note + groupNote);
    }
}
