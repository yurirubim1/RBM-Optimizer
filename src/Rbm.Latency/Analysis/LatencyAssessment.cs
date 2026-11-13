using System.Globalization;
using Rbm.Core;
using Rbm.Core.Diagnostics;

namespace Rbm.Latency;

/// <summary>Resultado da avaliação (veredito + achados).</summary>
public sealed record LatencyAssessmentResult(LatencyVerdict Verdict, string Text, IReadOnlyList<Finding> Findings);

/// <summary>
/// Regras de veredito e geração de achados para uma captura DPC/ISR.
/// Interpretação no estilo LatencyMon (heurística, não norma):
/// <list type="bullet">
/// <item>&lt; 500 µs por execução: normal para áudio/jogos.</item>
/// <item>500–1000 µs: merece atenção se recorrente.</item>
/// <item>&gt; 1 ms: pode causar falhas (estalos) de áudio em tempo real com buffers pequenos.</item>
/// <item>&gt; 2 ms recorrente: problemático.</item>
/// </list>
/// Veredito:
/// Problemático = execuções ≥ SevereUs em ≥ SustainedSeconds segundos distintos;
/// Atenção = qualquer execução ≥ HighUs, ou execuções ≥ ElevatedUs em ≥ SustainedSeconds segundos distintos;
/// Bom = caso contrário; Inconclusivo = nenhum evento recebido.
/// </summary>
public static class LatencyAssessment
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const int MaxModuleFindings = 5;

    public static LatencyAssessmentResult Assess(
        LatencyStats dpc, LatencyStats isr,
        IReadOnlyList<LatencyTimelinePoint> timeline,
        IReadOnlyList<ModuleLatencyStats> modules,
        IReadOnlyList<CpuLatencyStats> cpus,
        LatencyThresholds t,
        long eventsLost,
        double durationSeconds)
    {
        var findings = new List<Finding>();
        long total = dpc.Count + isr.Count;
        if (total == 0)
        {
            return new LatencyAssessmentResult(LatencyVerdict.Inconclusive,
                "Inconclusivo: nenhum evento DPC/ISR foi recebido. A sessão ETW de kernel pode não ter iniciado corretamente ou foi interrompida.",
                findings);
        }

        double worst = Math.Max(dpc.MaxUs, isr.MaxUs);
        int secSevere = timeline.Count(p => Math.Max(p.MaxDpcUs, p.MaxIsrUs) >= t.SevereUs);
        int secHigh = timeline.Count(p => Math.Max(p.MaxDpcUs, p.MaxIsrUs) >= t.HighUs);
        int secElevated = timeline.Count(p => Math.Max(p.MaxDpcUs, p.MaxIsrUs) >= t.ElevatedUs);

        LatencyVerdict verdict;
        if (secSevere >= t.SustainedSeconds) verdict = LatencyVerdict.Problematic;
        else if (worst >= t.HighUs || secElevated >= t.SustainedSeconds) verdict = LatencyVerdict.Attention;
        else verdict = LatencyVerdict.Good;

        string worstModule = modules.FirstOrDefault()?.Module ?? LatencyAggregator.UnknownModule;
        string summary = F($"Maior execução: {worst:0} µs ({worstModule}). DPC: máx {dpc.MaxUs:0} µs, P99 {dpc.P99Us:0} µs. ISR: máx {isr.MaxUs:0} µs, P99 {isr.P99Us:0} µs. Segundos com picos ≥ {t.ElevatedUs:0} µs: {secElevated}; ≥ {t.HighUs:0} µs: {secHigh}; ≥ {t.SevereUs:0} µs: {secSevere} (de {timeline.Count}).");
        string text = verdict switch
        {
            LatencyVerdict.Good => "Bom: nenhuma execução DPC/ISR acima de " + F($"{t.HighUs:0}") + " µs e sem picos recorrentes. Latência de kernel adequada para áudio em tempo real e jogos. " + summary,
            LatencyVerdict.Attention => "Atenção: há execuções longas de DPC/ISR. Picos isolados geralmente são inofensivos; se forem recorrentes podem causar estalos de áudio com buffers pequenos. " + summary,
            _ => "Problemático: execuções DPC/ISR acima de " + F($"{t.SevereUs:0}") + " µs se repetem ao longo da captura. Isso é associado a falhas de áudio e micro-travamentos. " + summary,
        };

        if (verdict is LatencyVerdict.Attention or LatencyVerdict.Problematic)
        {
            findings.Add(new Finding
            {
                RuleId = "latency.dpcisr.overall",
                Category = "Latency",
                Title = verdict == LatencyVerdict.Problematic ? "Latência DPC/ISR problemática" : "Latência DPC/ISR elevada",
                Description = text,
                Evidence =
                {
                    F($"Duração da captura: {durationSeconds:0.#} s; {dpc.Count} DPCs e {isr.Count} ISRs."),
                    F($"Maior execução: {worst:0} µs."),
                    F($"Segundos com pico ≥ {t.SevereUs:0} µs: {secSevere}; ≥ {t.HighUs:0} µs: {secHigh}; ≥ {t.ElevatedUs:0} µs: {secElevated}."),
                },
                ProbableCause = "Um ou mais drivers executam rotinas longas em nível de kernel (DPC/ISR). Os módulos listados nos achados seguintes estão associados aos picos — associação, não causa confirmada.",
                Confidence = Confidence.High,
                Severity = verdict == LatencyVerdict.Problematic ? Severity.High : Severity.Medium,
                ExpectedImpact = verdict == LatencyVerdict.Problematic ? ImpactLevel.Medium : ImpactLevel.Low,
                Solution = "Atualize drivers de chipset, rede, vídeo e áudio pelos sites dos fabricantes; atualize a BIOS; desative dispositivos não usados; repita a captura para comparar.",
                Risks = "Atualizar BIOS/drivers tem risco baixo a moderado; crie um ponto de restauração antes.",
                Restart = RestartRequirement.Reboot,
                Effort = EffortLevel.Medium,
                NavigateTo = "Latency",
                CorrelationOnly = false,
            });
        }

        // --- achados por módulo ---
        foreach (var m in modules
                     .Where(m => m.HighestUs >= t.HighUs || (m.Dpc.CountAtOrAbove(t.ElevatedUs) + m.Isr.CountAtOrAbove(t.ElevatedUs)) >= t.ModuleSpikeCount)
                     .OrderByDescending(m => m.HighestUs)
                     .Take(MaxModuleFindings))
        {
            findings.Add(ModuleFinding(m, t));
        }

        // --- concentração no CPU 0 (informativo) ---
        if (cpus.Count > 1)
        {
            double sum = cpus.Sum(c => c.DpcTotalUs + c.IsrTotalUs);
            var cpu0 = cpus.FirstOrDefault(c => c.Cpu == 0);
            if (cpu0 is not null && sum > 0)
            {
                double share = (cpu0.DpcTotalUs + cpu0.IsrTotalUs) / sum * 100;
                if (share >= 60 && cpu0.PercentTime >= 2)
                {
                    findings.Add(new Finding
                    {
                        RuleId = "latency.dpcisr.cpu0",
                        Category = "Latency",
                        Title = "DPC/ISR concentrados no CPU 0",
                        Description = F($"{share:0}% do tempo de DPC/ISR ocorreu no processador lógico 0, que passou {cpu0.PercentTime:0.##}% do tempo nessas rotinas."),
                        Evidence = { F($"CPU 0: {cpu0.DpcCount} DPCs ({cpu0.DpcTotalUs / 1000:0.#} ms), {cpu0.IsrCount} ISRs ({cpu0.IsrTotalUs / 1000:0.#} ms).") },
                        ProbableCause = "Muitos drivers direcionam interrupções ao CPU 0 por padrão (comum e nem sempre problemático). Associação, não causa confirmada.",
                        Confidence = Confidence.Medium,
                        Severity = Severity.Info,
                        ExpectedImpact = ImpactLevel.Unknown,
                        Solution = "Somente se houver problema observável: verifique se os dispositivos usam MSI/MSI-X (página de interrupções) e se o driver permite distribuir filas (RSS em placas de rede).",
                        Risks = "Alterar afinidade/modo de interrupção pode causar instabilidade se o dispositivo não suportar; faça pelo motor de ajustes com reversão.",
                        Restart = RestartRequirement.None,
                        NavigateTo = "Latency",
                        CorrelationOnly = true,
                    });
                }
            }
        }

        if (eventsLost > 0)
        {
            findings.Add(new Finding
            {
                RuleId = "latency.capture.eventsLost",
                Category = "Latency",
                Title = "Captura com eventos perdidos",
                Description = F($"A sessão ETW descartou {eventsLost} evento(s) por falta de buffer. Contagens e médias podem estar subestimadas."),
                Evidence = { F($"EventsLost = {eventsLost}") },
                ProbableCause = "Taxa de eventos maior que a capacidade de processamento em tempo real (sistema muito carregado durante a captura).",
                Confidence = Confidence.High,
                Severity = Severity.Info,
                ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Repita a captura com o sistema menos carregado ou com buffer maior.",
                Restart = RestartRequirement.None,
                NavigateTo = "Latency",
            });
        }

        return new LatencyAssessmentResult(verdict, text, findings);
    }

    private static Finding ModuleFinding(ModuleLatencyStats m, LatencyThresholds t)
    {
        long severeCount = m.Dpc.CountAtOrAbove(t.SevereUs) + m.Isr.CountAtOrAbove(t.SevereUs);
        long elevatedCount = m.Dpc.CountAtOrAbove(t.ElevatedUs) + m.Isr.CountAtOrAbove(t.ElevatedUs);
        bool unknown = m.Module == LatencyAggregator.UnknownModule;
        bool ambiguous = unknown || m.Category is DriverHints.Framework or DriverHints.Kernel;

        var sev = severeCount >= t.SustainedSeconds ? Severity.High
            : m.HighestUs >= t.HighUs ? Severity.Medium
            : Severity.Low;

        var evidence = new List<string>();
        if (m.Dpc.Count > 0)
            evidence.Add(F($"DPC: {m.Dpc.Count} execuções, máx {m.Dpc.MaxUs:0} µs, média {m.Dpc.MeanUs:0.#} µs, P99 {m.Dpc.P99Us:0} µs, {m.Dpc.CountAtOrAbove(t.ElevatedUs)} ≥ {t.ElevatedUs:0} µs."));
        if (m.Isr.Count > 0)
            evidence.Add(F($"ISR: {m.Isr.Count} execuções, máx {m.Isr.MaxUs:0} µs, média {m.Isr.MeanUs:0.#} µs, P99 {m.Isr.P99Us:0} µs, {m.Isr.CountAtOrAbove(t.ElevatedUs)} ≥ {t.ElevatedUs:0} µs."));
        if (!string.IsNullOrEmpty(m.ImagePath)) evidence.Add("Imagem: " + m.ImagePath);
        if (!string.IsNullOrEmpty(m.Category)) evidence.Add("Categoria provável: " + m.Category);

        string title = unknown
            ? "Execuções longas em rotinas de módulo não identificado"
            : $"Driver {m.Module} associado a execuções DPC/ISR longas";

        return new Finding
        {
            RuleId = "latency.dpcisr.module",
            Category = "Latency",
            Title = title,
            Description = F($"Durante a captura, rotinas de {m.Module} tiveram execução máxima de {m.HighestUs:0} µs e {elevatedCount} execução(ões) ≥ {t.ElevatedUs:0} µs."),
            Evidence = evidence,
            ProbableCause = "Associação, não causa confirmada: o tempo foi medido dentro de rotinas deste módulo, mas o gatilho pode estar em outro driver ou dispositivo. "
                            + (unknown ? "O endereço da rotina não pôde ser mapeado a um módulo carregado." : DriverHints.Hint(m.Module)),
            Confidence = ambiguous ? Confidence.Low : Confidence.Medium,
            Severity = sev,
            ExpectedImpact = sev >= Severity.Medium ? ImpactLevel.Medium : ImpactLevel.Low,
            Solution = unknown
                ? "Repita a captura; se persistir, analise com WPA (Windows Performance Recorder/Analyzer) para obter pilhas de chamada."
                : DriverHints.Hint(m.Module),
            Risks = "Atualizar ou desativar drivers pode afetar o dispositivo correspondente; crie um ponto de restauração e mantenha o driver anterior exportado.",
            Restart = RestartRequirement.Reboot,
            Effort = EffortLevel.Medium,
            NavigateTo = "Drivers",
            CorrelationOnly = true,
        };
    }

    private static string F(FormattableString s) => s.ToString(Pt);
}
