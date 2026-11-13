using System.Diagnostics;
using Rbm.Benchmarks.Framework;
using Rbm.Core;

namespace Rbm.Benchmarks;

/// <summary>
/// Base dos benchmarks implementados: aquecimento + N iterações medidas, executadas em thread dedicada
/// (prioridade Normal) fora da thread de UI. A execução real é feita por <see cref="BenchmarkRunner"/>.
/// </summary>
public abstract class IterativeBenchmark : IBenchmark
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract BenchmarkCategory Category { get; }
    public abstract string Unit { get; }
    public abstract bool HigherIsBetter { get; }
    public abstract string Methodology { get; }
    public virtual bool IsImplemented => true;
    public virtual bool WritesToStorage => false;
    public virtual IReadOnlyList<MetricDefinition> SecondaryMetrics => Array.Empty<MetricDefinition>();

    internal abstract BenchmarkSession CreateSession(BenchmarkOptions options);

    public Task<BenchmarkResult> RunAsync(BenchmarkOptions options, IProgress<BenchmarkProgress>? progress = null, CancellationToken ct = default)
        => BenchmarkRunner.RunAsync(this, options, progress, ct);
}

/// <summary>Medição de uma iteração: valor principal + métricas secundárias (por chave).</summary>
internal readonly record struct IterationMeasurement(double Primary, IReadOnlyDictionary<string, double>? Metrics = null);

internal sealed class SessionContext(BenchmarkOptions options, CancellationToken ct, Action<string> detail)
{
    public BenchmarkOptions Options { get; } = options;
    public CancellationToken Ct { get; } = ct;
    /// <summary>Mensagem de progresso detalhada (pt-BR) durante a fase atual.</summary>
    public void Detail(string message) => detail(message);
}

/// <summary>Estado de uma execução (buffers, arquivos, threads). Dispose SEMPRE é chamado (inclusive em cancelamento).</summary>
internal abstract class BenchmarkSession : IDisposable
{
    /// <summary>Parâmetros que definem a carga (devem ser iguais para comparar resultados).</summary>
    public Dictionary<string, string> Parameters { get; } = new();
    /// <summary>Informações adicionais (checksums, bytes gravados...).</summary>
    public Dictionary<string, string> Info { get; } = new();
    public List<string> Warnings { get; } = new();

    public virtual void Prepare(SessionContext ctx) { }
    public abstract IterationMeasurement Measure(SessionContext ctx, bool warmup);
    /// <summary>Chamado após a última iteração medida (ainda dentro do try; pode adicionar Info/Warnings).</summary>
    public virtual void Complete(SessionContext ctx) { }
    public virtual void Dispose() { }
}

/// <summary>Executor genérico: condições → preparação → aquecimento → iterações medidas → limpeza.</summary>
public static class BenchmarkRunner
{
    /// <summary>Limite de coeficiente de variação (5%) acima do qual as condições são marcadas como instáveis.</summary>
    public const double UnstableCv = 0.05;

    internal static Task<BenchmarkResult> RunAsync(IterativeBenchmark b, BenchmarkOptions? options,
        IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        options ??= new BenchmarkOptions();
        var tcs = new TaskCompletionSource<BenchmarkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(RunCore(b, options, progress, ct)); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = $"RBMBench {b.Id}",
            Priority = ThreadPriority.Normal,
        };
        thread.Start();
        return tcs.Task;
    }

    private static BenchmarkResult RunCore(IterativeBenchmark b, BenchmarkOptions o, IProgress<BenchmarkProgress>? progress, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var warnings = new List<string>();
        int steps = o.WarmupIterations + o.MeasuredIterations + 2;
        int stepDone = 0;
        BenchmarkPhase phase = BenchmarkPhase.Conditions;
        int iter = 0, iterTotal = 0;

        void Report(string msg) =>
            progress?.Report(new BenchmarkProgress(b.Id, phase, iter, iterTotal, Math.Clamp(stepDone / (double)steps, 0, 1), msg));

        BenchmarkResult Make(BenchmarkStatus status, string? message, TestConditions? cond,
            List<double>? primary = null, Dictionary<string, List<double>>? metrics = null, List<double>? wall = null,
            BenchmarkSession? s = null)
        {
            var allWarnings = new List<string>();
            if (cond is not null) allWarnings.AddRange(ConditionsProbe.Assess(cond));
            if (s is not null) allWarnings.AddRange(s.Warnings);
            allWarnings.AddRange(warnings);

            var parameters = new Dictionary<string, string>(s?.Parameters ?? new())
            {
                ["workScale"] = o.WorkScale.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            };
            bool completed = status == BenchmarkStatus.Completed;
            var summary = completed && primary is not null ? Stats.Summarize(primary) : StatSummary.Empty;
            bool cvHigh = completed && summary.CoefficientOfVariation > UnstableCv;
            if (cvHigh)
                allWarnings.Add($"Condições instáveis: coeficiente de variação das amostras de {summary.CoefficientOfVariation * 100:0.0}% (limite {UnstableCv * 100:0}%).");

            var metricList = new List<BenchmarkMetric>();
            if (completed && metrics is not null)
            {
                foreach (var def in b.SecondaryMetrics)
                {
                    if (!metrics.TryGetValue(def.Key, out var vals) || vals.All(v => !double.IsFinite(v))) continue;
                    metricList.Add(new BenchmarkMetric
                    {
                        Key = def.Key, Name = def.Name, Unit = def.Unit, HigherIsBetter = def.HigherIsBetter,
                        Samples = vals.ToArray(), Summary = Stats.Summarize(vals),
                    });
                }
            }

            return new BenchmarkResult
            {
                BenchmarkId = b.Id,
                Name = b.Name,
                Category = b.Category,
                Status = status,
                Unit = b.Unit,
                HigherIsBetter = b.HigherIsBetter,
                Samples = completed && primary is not null ? primary.ToArray() : Array.Empty<double>(),
                Summary = summary,
                Metrics = metricList,
                IterationWallSeconds = completed && wall is not null ? wall.ToArray() : Array.Empty<double>(),
                WarmupIterations = o.WarmupIterations,
                Conditions = cond,
                StartedAt = started,
                Duration = total.Elapsed,
                UnstableConditions = completed && (cvHigh || ConditionsProbe.IsBackgroundLoadHigh(cond)),
                Warnings = allWarnings.Distinct().ToArray(),
                Message = message,
                Methodology = b.Methodology,
                Parameters = parameters,
                Info = new Dictionary<string, string>(s?.Info ?? new()),
            };
        }

        string? invalid = o.Validate();
        if (invalid is not null) return Make(BenchmarkStatus.Failed, invalid, null);

        TestConditions? cond = null;
        BenchmarkSession? session = null;
        var primary = new List<double>();
        var wall = new List<double>();
        var metrics = b.SecondaryMetrics.ToDictionary(d => d.Key, _ => new List<double>());
        BenchmarkStatus status = BenchmarkStatus.Completed;
        string? message = null;

        try
        {
            ct.ThrowIfCancellationRequested();
            if (o.CaptureConditions)
            {
                Report("Verificando condições do sistema (carga de CPU de fundo)...");
                cond = ConditionsProbe.Capture(o.ConditionsSampleWindow, ct);
            }
            stepDone++;

            phase = BenchmarkPhase.Preparing;
            Report("Preparando dados de teste...");
            var ctx = new SessionContext(o, ct, Report);
            session = b.CreateSession(o);
            session.Prepare(ctx);
            stepDone++;

            phase = BenchmarkPhase.Warmup;
            iterTotal = o.WarmupIterations;
            for (iter = 1; iter <= o.WarmupIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Aquecimento {iter}/{o.WarmupIterations}...");
                session.Measure(ctx, warmup: true);
                stepDone++;
            }

            phase = BenchmarkPhase.Measuring;
            iterTotal = o.MeasuredIterations;
            for (iter = 1; iter <= o.MeasuredIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                Report($"Medição {iter}/{o.MeasuredIterations}...");
                long t0 = Stopwatch.GetTimestamp();
                var m = session.Measure(ctx, warmup: false);
                wall.Add(Timing.TicksToSeconds(Stopwatch.GetTimestamp() - t0));
                if (!double.IsFinite(m.Primary))
                    throw new BenchmarkFailureException("A medição produziu um valor inválido (não finito).");
                primary.Add(m.Primary);
                foreach (var (key, list) in metrics)
                    list.Add(m.Metrics is not null && m.Metrics.TryGetValue(key, out var v) ? v : double.NaN);
                stepDone++;
            }
            ct.ThrowIfCancellationRequested();
            session.Complete(ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = BenchmarkStatus.Cancelled;
            message = "Teste cancelado pelo usuário. Nenhum resultado parcial é apresentado.";
        }
        catch (BenchmarkFailureException ex)
        {
            status = BenchmarkStatus.Failed;
            message = ex.Message;
        }
        catch (OutOfMemoryException)
        {
            status = BenchmarkStatus.Failed;
            message = "Memória insuficiente para alocar os buffers do teste. Reduza o tamanho configurado.";
        }
        catch (Exception ex)
        {
            status = BenchmarkStatus.Failed;
            message = $"Erro inesperado durante o teste: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            phase = BenchmarkPhase.Finishing;
            iter = 0; iterTotal = 0;
            try { session?.Dispose(); }
            catch (Exception ex) { warnings.Add($"Falha ao liberar recursos do teste: {ex.Message}"); }
        }

        stepDone = steps;
        Report(status switch
        {
            BenchmarkStatus.Completed => "Concluído.",
            BenchmarkStatus.Cancelled => "Cancelado.",
            _ => "Falhou.",
        });
        return Make(status, message, cond, primary, metrics, wall, session);
    }
}
