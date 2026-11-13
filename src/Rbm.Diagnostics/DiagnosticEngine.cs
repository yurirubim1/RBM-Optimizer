using Rbm.Core;
using Rbm.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rbm.Diagnostics;

public sealed record DiagnosticRun(Guid Id, DateTimeOffset At, TimeSpan Duration, int SamplesAnalyzed, List<Finding> Findings, List<string> RuleErrors, HealthIndex Index);

/// <summary>Executa as regras de diagnóstico de forma isolada (falha de uma regra não derruba as demais).</summary>
public sealed class DiagnosticEngine
{
    private readonly IReadOnlyList<IDiagnosticRule> _rules;
    private readonly ILogger _log;

    public DiagnosticEngine(IEnumerable<IDiagnosticRule>? rules = null, ILogger<DiagnosticEngine>? log = null)
    {
        // Regras embutidas + regras extras registradas pelos módulos (ex.: drivers). DI injeta IEnumerable vazio se não houver extras.
        _rules = BuiltInRules.All().Concat(rules ?? Enumerable.Empty<IDiagnosticRule>()).GroupBy(r => r.Id).Select(g => g.Last()).ToList();
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public IReadOnlyList<IDiagnosticRule> Rules => _rules;

    public async Task<DiagnosticRun> RunAsync(DiagnosticContext ctx, IProgress<(int done, int total, string rule)>? progress = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var findings = new List<Finding>();
        var errors = new List<string>();
        for (int i = 0; i < _rules.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = _rules[i];
            progress?.Report((i, _rules.Count, r.Id));
            try { findings.AddRange(await r.EvaluateAsync(ctx, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Regra {Rule} falhou", r.Id);
                errors.Add($"{r.Id}: {ex.Message}");
            }
        }
        progress?.Report((_rules.Count, _rules.Count, ""));
        findings = findings.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToList();
        var index = HealthIndexCalculator.Compute(ctx, findings);
        return new DiagnosticRun(Guid.NewGuid(), DateTimeOffset.Now, sw.Elapsed, ctx.Samples.Count, findings, errors, index);
    }
}
