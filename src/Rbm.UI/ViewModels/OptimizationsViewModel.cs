using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using Rbm.Core;
using Rbm.Core.Tweaks;
using Rbm.Optimization;
using Rbm.Restore;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

/// <summary>Item do catálogo com estado lido do sistema.</summary>
public sealed partial class TweakItem : ObservableObject
{
    public TweakManifest M { get; }
    public TweakItem(TweakManifest m, (bool ok, string reason) compat)
    {
        M = m; IsCompatible = compat.ok; CompatReason = compat.reason;
        SelectedOption = m.Options.FirstOrDefault(o => o.Key == (m.RecommendedOption ?? m.DefaultOption)) ?? m.Options.FirstOrDefault();
    }

    public string Name => M.Name.Get(Loc.Language);
    /// <summary>Primeira frase da explicação acessível (para a lista).</summary>
    public string Summary
    {
        get
        {
            var s = M.PlainExplanation.Get(Loc.Language);
            var i = s.IndexOf(". ", StringComparison.Ordinal);
            return i > 20 && i < 180 ? s[..(i + 1)] : s.Length > 180 ? s[..180] + "…" : s;
        }
    }
    public bool IsRecommended => M.IsRecommendable && !M.Experimental;
    public string RiskWord => M.Risk switch { RiskLevel.Minimal => "Seguro", RiskLevel.Low => "Baixo risco", RiskLevel.Medium => "Risco médio", _ => "Risco alto" };
    public string ImpactWord => M.Impact switch { ImpactLevel.High => "Impacto alto", ImpactLevel.Medium => "Impacto médio", ImpactLevel.Low => "Impacto pequeno", ImpactLevel.Negligible => "Impacto mínimo", _ => "Impacto não comprovado" };
    public string RestartWord => M.Restart switch { RestartRequirement.None => "Vale na hora", RestartRequirement.Reboot => "Requer reiniciar o PC", RestartRequirement.SignOut => "Requer sair da conta", RestartRequirement.ExplorerRestart => "Requer reiniciar o Explorer", _ => "Requer reiniciar o app" };
    public string EvidenceWord => M.Grade switch { EvidenceGrade.A => "Benefício comprovado", EvidenceGrade.B => "Benefício provável", EvidenceGrade.C => "Experimental", EvidenceGrade.D => "Sem benefício comprovado", _ => "Não recomendado (obsoleto)" };
    public string Category => M.Category + (M.Subcategory is { Length: > 0 } s ? " · " + s : "");
    public string Grade => M.Grade.ToString();
    public string GradeText => Fmt.GradeText(M.Grade);
    public bool IsLab => M.Experimental;
    public bool IsBlocked => M.Grade == EvidenceGrade.E;
    public string RecommendedText => M.RecommendedOption is { } r ? $"Recomendado: {M.GetOption(r)?.Label.Get(Loc.Language)}" : "Sem recomendação: manter o padrão do Windows";
    public string DefaultText => M.GetOption(M.DefaultOption)?.Label.Get(Loc.Language) ?? M.DefaultOption;
    public string TargetsText => string.Join("\n", (M.GetOption(M.DefaultOption) ?? M.Options[0]).Targets.Select(t => "• " + t.Display));
    public string DepsText => $"Depende de: {(M.DependsOn.Count == 0 ? "nenhum" : string.Join(", ", M.DependsOn))} · Conflita com: {(M.ConflictsWith.Count == 0 ? "nenhum" : string.Join(", ", M.ConflictsWith))}";
    public bool NeedsAdmin => M.Options.SelectMany(o => o.Targets).Any(t => t.WriteRequiresAdmin);
    public IReadOnlyList<TweakOption> Options => M.Options;

    [ObservableProperty] private bool _isCompatible;
    [ObservableProperty] private string _compatReason = "";
    [ObservableProperty] private TweakStatus? _status;
    [ObservableProperty] private string _stateText = "Lendo…";
    [ObservableProperty] private string _stateKind = "unknown";
    [ObservableProperty] private TweakOption? _selectedOption;
    [ObservableProperty] private string _readingsText = "";
    [ObservableProperty] private bool _hasActiveChange;

    public void ApplyStatus(TweakStatus s)
    {
        Status = s;
        (StateText, StateKind) = s.Kind switch
        {
            TweakStateKind.MatchesOption when s.CurrentOption == M.RecommendedOption => ($"Já está como recomendado ({M.GetOption(s.CurrentOption!)?.Label.Get(Loc.Language)})", "recommended"),
            TweakStateKind.MatchesOption when s.CurrentOption == M.DefaultOption => ($"Como o Windows vem ({DefaultText})", "default"),
            TweakStateKind.MatchesOption => ($"Ajustado: {M.GetOption(s.CurrentOption!)?.Label.Get(Loc.Language)}", s.CurrentOption == M.RecommendedOption ? "recommended" : "custom"),
            TweakStateKind.Custom => ("Valor personalizado (definido por outro programa ou pelo fabricante)", "custom"),
            TweakStateKind.NeedsElevationToRead => ("Leitura requer o agente elevado", "unknown"),
            TweakStateKind.Error => ("Erro na leitura", "error"),
            TweakStateKind.NotApplicable => (s.Message ?? "Não aplicável nesta instalação", "unknown"),
            _ => ("Desconhecido", "unknown"),
        };
        ReadingsText = string.Join("\n", s.Readings.Select(r => $"• {r.Display} = {r.Value}{(r.Error is null ? "" : $"  ({r.Error})")}"));
    }
}

public sealed partial class OptimizationsViewModel : PageViewModel
{
    private readonly TweakCatalog _catalog;
    private readonly ChangeOrchestrator _orch;
    private readonly ISystemInventory _inventory;
    private readonly IChangeJournal _journal;
    private readonly ISettingsStore _settings;
    private bool _loaded;
    public override string Key => "optimizations";

    public ObservableCollection<TweakItem> Items { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<string> Categories { get; } = new();

    /// <summary>"recommended" (padrão), "all" ou "lab".</summary>
    [ObservableProperty] private string _mode = "recommended";
    [ObservableProperty] private string _heroTitle = "";
    [ObservableProperty] private string _heroSentence = "";
    [ObservableProperty] private bool _listEmpty;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _category = "Todas";
    [ObservableProperty] private bool _showLab;
    [ObservableProperty] private bool _showDocOnly;
    [ObservableProperty] private bool _onlyCompatible = true;
    [ObservableProperty] private TweakItem? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _catalogWarnings = "";

    public OptimizationsViewModel(TweakCatalog catalog, ChangeOrchestrator orch, ISystemInventory inventory, IChangeJournal journal, ISettingsStore settings)
    {
        _catalog = catalog; _orch = orch; _inventory = inventory; _journal = journal; _settings = settings;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Category), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(TweakItem.Name), ListSortDirection.Ascending));
        _orch.HistoryChanged += (_, _) => Ui.Post(() => _ = RefreshActiveFlags());
    }

    partial void OnSearchChanged(string value) { View.Refresh(); ListEmpty = View.IsEmpty; }
    partial void OnCategoryChanged(string value) => View.Refresh();
    partial void OnShowLabChanged(bool value) => View.Refresh();
    partial void OnShowDocOnlyChanged(bool value) => View.Refresh();
    partial void OnOnlyCompatibleChanged(bool value) => View.Refresh();

    partial void OnModeChanged(string value)
    {
        ShowLab = value == "lab";
        View.Refresh();
        ListEmpty = View.IsEmpty;
    }

    [RelayCommand]
    private void SetMode(string mode) => Mode = mode;

    private void UpdateHero()
    {
        var rec = Items.Where(i => i.IsRecommended && i.IsCompatible).ToList();
        int done = rec.Count(i => i.StateKind == "recommended");
        int pending = rec.Count(i => i.StateKind is "default" or "custom");
        HeroTitle = pending == 0 && rec.Count > 0 ? "Seu PC já está com todos os ajustes recomendados" : $"{pending} ajuste(s) recomendado(s) ainda não aplicado(s)";
        HeroSentence = $"{done} de {rec.Count} recomendações já estão em vigor. Recomendamos apenas ajustes com benefício comprovado ou provável; todo ajuste pode ser desfeito no Histórico.";
    }

    private bool Filter(object o)
    {
        var t = (TweakItem)o;
        if (Mode == "lab" ? !t.IsLab : t.IsLab) return false;
        if (Mode == "recommended" && !t.IsRecommended) return false;
        if (!ShowDocOnly && t.M.IsDocumentationOnly) return false;
        if (OnlyCompatible && !t.IsCompatible) return false;
        if (Category != "Todas" && t.M.Category != Category) return false;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            return t.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || t.M.Id.Contains(s, StringComparison.OrdinalIgnoreCase)
                || t.M.Tags.Any(x => x.Contains(s, StringComparison.OrdinalIgnoreCase)) || t.M.TechnicalDescription.Pt.Contains(s, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public override async Task OnNavigatedToAsync()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var m in _catalog.Tweaks)
            Items.Add(new TweakItem(m, CompatibilityEvaluator.Evaluate(m, _inventory.Current)));
        Categories.Add("Todas");
        foreach (var c in _catalog.Tweaks.Select(t => t.Category).Distinct().OrderBy(c => c)) Categories.Add(c);
        CatalogWarnings = _catalog.ValidationErrors.Count == 0 ? "" : $"{_catalog.ValidationErrors.Count} item(ns) do catálogo rejeitados na validação (ver log).";
        foreach (var e in _catalog.ValidationErrors) Serilog.Log.Warning("Catálogo: {Error}", e);
        Summary = $"{_catalog.Tweaks.Count} técnicas catalogadas · {_catalog.Tweaks.Count(t => t.IsRecommendable)} com recomendação (A/B) · {_catalog.Tweaks.Count(t => t.Experimental)} no Advanced Tweaks Lab";
        Selected = Items.FirstOrDefault(i => Filter(i));
        await RefreshAllStatesAsync();
    }

    [RelayCommand]
    private async Task RefreshAllStatesAsync()
    {
        using var _ = App.Tasks.Begin("Lendo estado atual dos ajustes");
        foreach (var item in Items.ToList())
        {
            try { item.ApplyStatus(await _orch.ReadStatusAsync(item.M)); }
            catch (Exception ex) { item.StateText = "Erro: " + ex.Message; item.StateKind = "error"; }
        }
        await RefreshActiveFlags();
        UpdateHero();
        ListEmpty = View.IsEmpty;
    }

    private async Task RefreshActiveFlags()
    {
        var active = (await _journal.ActiveChangesAsync()).Select(r => r.TweakId).ToHashSet();
        foreach (var i in Items) i.HasActiveChange = active.Contains(i.M.Id);
    }

    [RelayCommand]
    private async Task Apply(TweakItem? item)
    {
        item ??= Selected;
        if (item?.SelectedOption is not { } opt) return;
        var m = item.M;
        if (item.IsBlocked) { await App.Dialogs.AlertAsync("Não aplicável", "Técnicas grau E são obsoletas, inseguras ou contraindicadas. O RBM apenas as documenta."); return; }

        var details = $"Opção: {opt.Label.Get(Loc.Language)}\nRisco: {m.Risk.Pt()} · Evidência: {Fmt.GradeText(m.Grade)} · Reinício: {m.Restart.Pt()}\n\nAlterações:\n" +
                      string.Join("\n", opt.Targets.Select(t => $"• {t.Display} → {t.Desired}")) +
                      (string.IsNullOrWhiteSpace(m.SideEffects.Pt) ? "" : "\n\nEfeitos colaterais: " + m.SideEffects.Get(Loc.Language)) +
                      (item.IsCompatible ? "" : "\n\n⚠ Incompatibilidade detectada: " + item.CompatReason) +
                      (item.NeedsAdmin ? "\n\nRequer o agente elevado (o Windows pedirá confirmação UAC)." : "");
        bool high = m.Risk >= RiskLevel.High || m.Experimental || m.Grade >= EvidenceGrade.C;
        var msg = m.Grade >= EvidenceGrade.C
            ? "Esta técnica NÃO tem benefício comprovado. Aplique apenas para testar, medindo antes e depois. O estado anterior é salvo e pode ser revertido."
            : "O estado atual será salvo antes da alteração e a mudança será verificada por releitura. Você pode reverter a qualquer momento no Histórico.";
        if (!await App.Dialogs.ConfirmAsync($"Aplicar: {item.Name}", msg, details, "Aplicar", danger: high, requiredPhrase: high ? "APLICAR" : null)) return;

        await RunBusy("Aplicando…", async () =>
        {
            if (m.Risk >= RiskLevel.Medium && _settings.Get("restore.pointBeforeRisky", true))
            {
                var rp = await _orch.CreateRestorePointAsync($"RBM Optimizer: antes de {m.Id}");
                if (!rp.Success && !await App.Dialogs.ConfirmAsync("Ponto de restauração não criado", rp.Message + "\n\nO histórico do RBM ainda permite reverter esta alteração. Continuar mesmo assim?", confirmText: "Continuar"))
                    return;
            }
            using var _ = App.Tasks.Begin($"Aplicando {m.Id}");
            var outcome = await _orch.ApplyAsync(m.Id, opt.Key);
            item.ApplyStatus(await _orch.ReadStatusAsync(m));
            UpdateHero();
            if (outcome.Success) App.Notify.Success(item.Name, outcome.Message);
            else App.Notify.Error(item.Name, outcome.Message);
            await App.Services.GetRequiredService<MainViewModel>().RefreshPendingRestartAsync();
        });
    }

    [RelayCommand]
    private async Task RevertLatest(TweakItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        var rec = (await _journal.ActiveChangesAsync()).FirstOrDefault(r => r.TweakId == item.M.Id);
        if (rec is null) { App.Notify.Info("Nada a reverter", "Não há alteração ativa do RBM para este ajuste."); return; }
        if (!await App.Dialogs.ConfirmAsync($"Reverter: {item.Name}", $"Restaurar os valores anteriores a {rec.At:g}?", string.Join("\n", rec.Changes.Select(c => $"• {c.Target.Display}: {c.After} → {c.Before}")), "Reverter")) return;
        await RunBusy("Revertendo…", async () =>
        {
            var o = await _orch.RevertAsync(rec.ChangeId, force: false);
            if (!o.Success && o.NeedsForce)
            {
                var drift = string.Join("\n", o.Drift.Select(d => $"• {d.Display}: esperado {d.Expected}, atual {d.Actual}"));
                if (await App.Dialogs.ConfirmAsync("Alteração externa detectada", "Estes valores foram modificados fora do RBM depois da aplicação. Sobrescrever com o estado original?", drift, "Sobrescrever", danger: true))
                    o = await _orch.RevertAsync(rec.ChangeId, force: true);
            }
            item.ApplyStatus(await _orch.ReadStatusAsync(item.M));
            if (o.Success) App.Notify.Success("Revertido", o.Message); else App.Notify.Warning("Reversão não concluída", o.Message);
        });
    }

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ToggleLab() => Mode = Mode == "lab" ? "recommended" : "lab";

    [RelayCommand]
    private void OpenProfiles() => App.Services.GetRequiredService<MainViewModel>().Navigate("profiles");
}
