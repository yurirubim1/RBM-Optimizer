using Rbm.Core;

namespace Rbm.Gaming.Sessions;

/// <summary>Opções de comparação.</summary>
/// <param name="MinSeconds">Duração mínima de cada sessão (padrão 20 s).</param>
/// <param name="UserFlaggedDifferentSettings">O usuário indicou que resolução/configurações/cena mudaram entre as sessões.</param>
/// <param name="MinMeaningfulPercent">Variação mínima para declarar ganho/perda (repassada a Stats.Compare).</param>
public sealed record SessionComparisonOptions(double MinSeconds = 20, bool UserFlaggedDifferentSettings = false, double MinMeaningfulPercent = 2.0);

/// <summary>Variação descritiva de uma métrica (sem teste estatístico).</summary>
public sealed record MetricDelta(string NamePt, double Before, double After, double DeltaPercent, bool HigherIsBetter);

/// <summary>Resultado da comparação antes/depois.</summary>
/// <param name="FpsComparison">Teste de Welch sobre o FPS por segundo (Stats.Compare).</param>
/// <param name="Deltas">Variações descritivas (FPS médio, 1% low, 0,1% low, P99, stutters/min).</param>
/// <param name="Caveats">Ressalvas (pt-BR).</param>
public sealed record SessionComparison(
    FrameSessionSummary Before,
    FrameSessionSummary After,
    ComparisonResult FpsComparison,
    IReadOnlyList<MetricDelta> Deltas,
    IReadOnlyList<string> Caveats,
    string VerdictPt);

/// <summary>Comparação de sessões antes/depois com recusa explícita quando o teste não é válido.</summary>
public static class SessionComparer
{
    public static OperationResult<SessionComparison> Compare(FrameSessionSummary before, FrameSessionSummary after, SessionComparisonOptions? options = null)
    {
        options ??= new SessionComparisonOptions();
        if (options.UserFlaggedDifferentSettings)
            return OperationResult<SessionComparison>.Fail("Comparação recusada: você indicou que resolução/configurações/cena mudaram entre as sessões. Repita a captura nas mesmas condições.", "different_settings");
        if (before.Duration.TotalSeconds < options.MinSeconds || after.Duration.TotalSeconds < options.MinSeconds)
            return OperationResult<SessionComparison>.Fail($"Comparação recusada: cada sessão precisa de pelo menos {options.MinSeconds:0} s de dados (antes: {before.Duration.TotalSeconds:0.#} s, depois: {after.Duration.TotalSeconds:0.#} s).", "too_short");
        if (before.PerSecondFps.Count < options.MinSeconds * 0.9 || after.PerSecondFps.Count < options.MinSeconds * 0.9)
            return OperationResult<SessionComparison>.Fail("Comparação recusada: amostras de FPS por segundo insuficientes.", "too_few_samples");
        if (Differs(before.Conditions.Resolution, after.Conditions.Resolution))
            return OperationResult<SessionComparison>.Fail($"Comparação recusada: resoluções diferentes ({before.Conditions.Resolution} × {after.Conditions.Resolution}).", "different_resolution");
        if (Differs(before.Conditions.SettingsLabel, after.Conditions.SettingsLabel))
            return OperationResult<SessionComparison>.Fail($"Comparação recusada: configurações gráficas diferentes ('{before.Conditions.SettingsLabel}' × '{after.Conditions.SettingsLabel}').", "different_settings");

        var caveats = new List<string>
        {
            "O FPS por segundo é autocorrelacionado; o p-valor tende a ser otimista. Repita a captura (3+ vezes por lado) para confirmar.",
        };
        if (!string.Equals(before.GameId, after.GameId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.ProcessName, after.ProcessName, StringComparison.OrdinalIgnoreCase))
            caveats.Add("As sessões são de jogos/processos diferentes — a comparação provavelmente não faz sentido.");
        if (Differs(before.Conditions.Scenario, after.Conditions.Scenario))
            caveats.Add("Cenas/trechos diferentes: o FPS varia muito com a cena.");
        if (before.Conditions.Resolution is null || after.Conditions.Resolution is null)
            caveats.Add("Resolução não informada em uma das sessões; confirme que é a mesma.");
        if (before.DominantPresentMode != after.DominantPresentMode)
            caveats.Add($"O modo de apresentação mudou ({before.DominantPresentMode ?? "?"} → {after.DominantPresentMode ?? "?"}); isso altera latência e às vezes FPS.");
        var addedTweaks = after.Conditions.TweakIds.Except(before.Conditions.TweakIds).ToList();
        var removedTweaks = before.Conditions.TweakIds.Except(after.Conditions.TweakIds).ToList();
        if (addedTweaks.Count + removedTweaks.Count > 1)
            caveats.Add($"Mais de uma alteração entre as sessões ({addedTweaks.Count} adicionadas, {removedTweaks.Count} removidas): não dá para atribuir o efeito a uma só.");
        if (before.PowerPlanChanged(after))
            caveats.Add("O plano de energia mudou entre as sessões.");

        var cmp = Stats.Compare(before.PerSecondFps, after.PerSecondFps, higherIsBetter: true, options.MinMeaningfulPercent);
        var deltas = new List<MetricDelta>
        {
            D("FPS médio", before.AverageFps, after.AverageFps, true),
            D("1% low (P99)", before.Low1PercentFps, after.Low1PercentFps, true),
            D("0,1% low (P99,9)", before.Low01PercentFps, after.Low01PercentFps, true),
            D("1% low (média dos piores)", before.Low1PercentAvgFps, after.Low1PercentAvgFps, true),
            D("Frametime P99 (ms)", before.P99FrameTimeMs, after.P99FrameTimeMs, false),
            D("Stutters por minuto", before.StuttersPerMinute, after.StuttersPerMinute, false),
        };
        string verdict = "FPS por segundo: " + cmp.Verdict + " As variações de 1%/0,1% low são descritivas (sem teste estatístico).";
        return OperationResult<SessionComparison>.Ok(new SessionComparison(before, after, cmp, deltas, caveats, verdict));
    }

    private static MetricDelta D(string name, double a, double b, bool higher) =>
        new(name, a, b, double.IsFinite(a) && a != 0 && double.IsFinite(b) ? (b - a) / Math.Abs(a) * 100.0 : double.NaN, higher);

    private static bool Differs(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        !string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static bool PowerPlanChanged(this FrameSessionSummary a, FrameSessionSummary b) =>
        (a.Conditions.PowerPlanGuid is { } ga && b.Conditions.PowerPlanGuid is { } gb && ga != gb) ||
        Differs(a.Conditions.PowerPlanName, b.Conditions.PowerPlanName);
}
