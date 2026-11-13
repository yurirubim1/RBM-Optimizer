using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Data;
using Rbm.Diagnostics;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

/// <summary>Achado apresentado em linguagem simples (o objeto técnico completo continua acessível em <see cref="F"/>).</summary>
public sealed class FindingCard
{
    public required Finding F { get; init; }
    public string Title => F.Title;
    public string Description => F.Description;
    public string Solution => F.Solution;
    public string Level => F.Severity switch { Severity.Critical => "Urgente", Severity.High => "Importante", Severity.Medium => "Atenção", Severity.Low => "Dica", _ => "Informação" };
    public string Kind => F.Severity switch { >= Severity.High => "danger", Severity.Medium => "warn", _ => "info" };
    public string ButtonText => DashboardViewModel.ButtonFor(F.NavigateTo);
    public string NavigateKey => DashboardViewModel.KeyFor(F.NavigateTo);
    public bool HasAction => F.NavigateTo is not null;
    public string Facts => $"Confiança: {F.Confidence.Pt()} · Impacto esperado: {F.ExpectedImpact.Pt()} · Reinício: {F.Restart.Pt()} · Reversível: {(F.Reversible ? "sim" : "não")} · Esforço: {F.Effort switch { EffortLevel.Trivial => "trivial", EffortLevel.Low => "baixo", EffortLevel.Medium => "médio", _ => "alto" }}";
}

public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private readonly DiagnosticsState _state;
    private readonly RbmDatabase _db;
    private readonly ITelemetryService _telemetry;
    public override string Key => "diagnostics";

    public ObservableCollection<FindingCard> Findings { get; } = new();
    public ObservableCollection<IndexCategory> Index { get; } = new();
    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _healthKind = "unknown";
    [ObservableProperty] private string _healthChip = "Ainda não verificado";
    [ObservableProperty] private string _healthTitle = "Vamos verificar a saúde do seu PC";
    [ObservableProperty] private string _healthSentence = "A verificação usa os últimos minutos de uso real do computador. Quanto mais tempo o RBM ficar aberto antes, mais confiável o resultado.";
    [ObservableProperty] private string _runInfo = "";
    [ObservableProperty] private string _ruleErrors = "";
    [ObservableProperty] private int _important;
    [ObservableProperty] private int _attention;
    [ObservableProperty] private int _tips;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _noFindingsInFilter;
    public string ProgressText => _state.ProgressText;
    public bool Running => _state.Running;

    public DiagnosticsViewModel(DiagnosticsState state, RbmDatabase db, ITelemetryService telemetry)
    {
        _state = state; _db = db; _telemetry = telemetry;
        _state.PropertyChanged += (_, e) => Ui.Post(() =>
        {
            if (e.PropertyName == nameof(DiagnosticsState.LastRun)) Apply();
            if (e.PropertyName == nameof(DiagnosticsState.ProgressText)) OnPropertyChanged(nameof(ProgressText));
            if (e.PropertyName == nameof(DiagnosticsState.Running)) OnPropertyChanged(nameof(Running));
        });
    }

    partial void OnFilterChanged(string value) => Apply();

    public override async Task OnNavigatedToAsync()
    {
        Apply();
        if (_state.LastRun is null && !_state.Running)
        {
            // Primeira visita: coleta ~20 s de uso real e verifica automaticamente.
            HealthChip = "Verificando…";
            for (int i = 0; i < 25 && _telemetry.History.Count < 20; i++)
            {
                HealthSentence = $"Observando o uso do computador para um resultado confiável… ({_telemetry.History.Count}/20 s)";
                await Task.Delay(1000);
            }
            HealthSentence = "Analisando…";
            await _state.RunAsync();
        }
        try
        {
            var runs = await _db.ListAsync<DiagnosticRun>(Collections.Diagnostics, 15);
            History.Clear();
            foreach (var r in runs)
                History.Add($"{r.At:g} — {r.Findings.Count(f => f.Severity >= Severity.High)} importante(s), {r.Findings.Count(f => f.Severity == Severity.Medium)} de atenção, {r.Findings.Count(f => f.Severity <= Severity.Low)} dica(s)");
        }
        catch (Exception ex) { App.Log(ex, "Histórico de diagnósticos"); }
    }

    private void Apply()
    {
        var run = _state.LastRun;
        if (run is null) return;
        HasRun = true;
        Important = run.Findings.Count(f => f.Severity >= Severity.High);
        Attention = run.Findings.Count(f => f.Severity == Severity.Medium);
        Tips = run.Findings.Count(f => f.Severity <= Severity.Low);
        (HealthKind, HealthChip, HealthTitle, HealthSentence) = Important > 0
            ? ("danger", "Precisa de cuidado", $"{Important} problema(s) importante(s) encontrado(s)", "Comece pelos itens marcados como Importante. Cada um explica o que fazer — e o porquê, se você quiser ver.")
            : Attention > 0
                ? ("warn", "Atenção", $"Nada grave — {Attention} ponto(s) de atenção", "Melhorias simples que valem a pena quando tiver um tempinho.")
                : ("ok", "Tudo certo", "Nenhum problema relevante encontrado", "Seu computador está em boa condição.");
        RunInfo = $"Verificado em {run.At:g} · analisou {run.SamplesAnalyzed} segundos de uso real · levou {run.Duration.TotalSeconds:0.0} s";
        RuleErrors = run.RuleErrors.Count == 0 ? "" : "Algumas verificações não puderam ser concluídas: " + string.Join("; ", run.RuleErrors);
        Findings.Clear();
        foreach (var f in run.Findings.Where(f => Filter switch
        {
            "important" => f.Severity >= Severity.High,
            "attention" => f.Severity == Severity.Medium,
            "tips" => f.Severity <= Severity.Low,
            _ => true,
        })) Findings.Add(new FindingCard { F = f });
        NoFindingsInFilter = Findings.Count == 0;
        Index.Clear();
        foreach (var c in run.Index.Categories) Index.Add(c);
    }

    [RelayCommand]
    private async Task Run() => await RunBusy("Verificando seu PC…", () => _state.RunAsync());

    [RelayCommand]
    private void SetFilter(string f) => Filter = f;

    [RelayCommand]
    private void Go(string? key)
    {
        if (!string.IsNullOrEmpty(key)) App.Services.GetRequiredService<MainViewModel>().Navigate(key);
    }
}
