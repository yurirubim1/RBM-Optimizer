using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Core.Telemetry;
using Rbm.Data;
using Rbm.Diagnostics;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.UI.ViewModels;

/// <summary>Estado compartilhado do último diagnóstico (Dashboard e página Diagnóstico).</summary>
public sealed partial class DiagnosticsState : ObservableObject
{
    private readonly DiagnosticEngine _engine;
    private readonly ISystemInventory _inventory;
    private readonly ITelemetryService _telemetry;
    private readonly RbmDatabase _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Fonte de processos (preenchida pelo módulo de processos quando disponível).</summary>
    public Func<IReadOnlyList<ProcessSample>> ProcessSource { get; set; } = () => Array.Empty<ProcessSample>();

    [ObservableProperty] private DiagnosticRun? _lastRun;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _progressText = "";

    public DiagnosticsState(DiagnosticEngine engine, ISystemInventory inventory, ITelemetryService telemetry, RbmDatabase db)
    {
        _engine = engine; _inventory = inventory; _telemetry = telemetry; _db = db;
    }

    /// <summary>Executa as regras com a janela de telemetria disponível (mínimo recomendado: 30 s).</summary>
    public async Task<DiagnosticRun?> RunAsync(bool persist = true)
    {
        var profile = _inventory.Current;
        if (profile is null) return null;
        if (!await _gate.WaitAsync(0)) return LastRun;
        try
        {
            Running = true;
            using var _ = App.Tasks.Begin("Diagnóstico");
            var ctx = new DiagnosticContext { Profile = profile, Samples = _telemetry.History, Processes = ProcessSource() };
            var progress = new Progress<(int done, int total, string rule)>(p => ProgressText = p.total == 0 ? "" : $"Regra {p.done}/{p.total}: {p.rule}");
            var run = await Task.Run(() => _engine.RunAsync(ctx, progress));
            LastRun = run;
            if (persist) await _db.UpsertAsync(Collections.Diagnostics, run.Id.ToString(), run, null, run.At);
            return run;
        }
        finally { Running = false; ProgressText = ""; _gate.Release(); }
    }
}
