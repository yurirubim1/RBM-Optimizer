using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Tweaks;
using Rbm.Data;
using Rbm.Optimization;
using Rbm.Restore;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

public sealed record ProfileCard(string Id, string Name, string Description, string Kind, int Count, TweakProfile? Profile)
{
    /// <summary>Selo de orientação ("Comece por aqui" etc.).</summary>
    public string? Badge => Id switch { "safe" => "Comece por aqui", "default" => "Desfaz tudo", "custom" => "Você escolhe", _ => null };
    public string CountText => Count > 0 ? $"{Count} ajustes" : "";
}

public sealed partial class ProfileDiffRow : ObservableObject
{
    public required TweakManifest M { get; init; }
    public required string Option { get; init; }
    public string? Rationale { get; init; }
    public string Name => M.Name.Get(Loc.Language);
    public string OptionLabel => M.GetOption(Option)?.Label.Get(Loc.Language) ?? Option;
    [ObservableProperty] private string _current = "lendo…";
    [ObservableProperty] private string _action = "";
    [ObservableProperty] private bool _willChange;
    [ObservableProperty] private bool _included = true;
}

public sealed record CustomEntry(string TweakId, string Option);

public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly TweakCatalog _catalog;
    private readonly ChangeOrchestrator _orch;
    private readonly IChangeJournal _journal;
    private readonly ISystemInventory _inventory;
    private readonly ISettingsStore _settings;
    private readonly RbmDatabase _db;
    public override string Key => "profiles";

    public ObservableCollection<ProfileCard> SystemProfiles { get; } = new();
    public ObservableCollection<ProfileCard> GamingProfiles { get; } = new();
    public ObservableCollection<ProfileDiffRow> Diff { get; } = new();

    [ObservableProperty] private ProfileCard? _selected;
    [ObservableProperty] private string _diffSummary = "Escolha um perfil à esquerda para ver exatamente o que mudaria. Nada é aplicado sem sua confirmação.";
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private string _conditionWarning = "";
    [ObservableProperty] private bool _isCustom;

    public ProfilesViewModel(TweakCatalog catalog, ChangeOrchestrator orch, IChangeJournal journal, ISystemInventory inventory, ISettingsStore settings, RbmDatabase db)
    {
        _catalog = catalog; _orch = orch; _journal = journal; _inventory = inventory; _settings = settings; _db = db;
    }

    public override Task OnNavigatedToAsync()
    {
        if (SystemProfiles.Count > 0) return Task.CompletedTask;
        SystemProfiles.Add(new ProfileCard("default", "Padrão (Default)", "Restaura o comportamento original: reverte todas as alterações ativas feitas pelo RBM, na ordem inversa.", "system", 0, null));
        foreach (var p in _catalog.Profiles.Where(p => p.Id != "default"))
        {
            var card = new ProfileCard(p.Id, p.Name.Get(Loc.Language), p.Description.Get(Loc.Language), p.Kind, p.Entries.Count, p);
            (p.Kind == "gaming" ? GamingProfiles : SystemProfiles).Add(card);
        }
        SystemProfiles.Add(new ProfileCard("custom", "Personalizado (Custom)", "Você escolhe exatamente quais ajustes com evidência A/B incluir (cada um com sua opção recomendada). Para outras opções ou ajustes experimentais, use Otimizações item a item.", "system", 0, null));
        return Task.CompletedTask;
    }

    partial void OnSelectedChanged(ProfileCard? value) => _ = BuildDiffAsync(value);

    private async Task BuildDiffAsync(ProfileCard? card)
    {
        Diff.Clear();
        ConditionWarning = "";
        IsCustom = card?.Id == "custom";
        if (card is null) return;
        var p = _inventory.Current;

        if (card.Id == "default")
        {
            var active = await _journal.ActiveChangesAsync();
            foreach (var r in active.OrderByDescending(r => r.At))
                if (_catalog.Get(r.TweakId) is { } m)
                    Diff.Add(new ProfileDiffRow { M = m, Option = r.FromOption ?? m.DefaultOption, Rationale = $"Aplicado em {r.At:g}", Current = $"Aplicado pelo RBM: {r.ToOption}", Action = "Volta a como estava", WillChange = true });
            DiffSummary = Diff.Count == 0 ? "Nenhuma alteração ativa do RBM: o sistema já está no estado original (para o que o RBM controla)." : $"{Diff.Count} alteração(ões) serão revertidas.";
            return;
        }

        IEnumerable<(string id, string opt, string? why)> entries;
        if (card.Id == "custom")
        {
            var saved = _settings.Get("profiles.custom", new List<CustomEntry>());
            var savedMap = saved.ToDictionary(s => s.TweakId, s => s.Option);
            entries = _catalog.Tweaks.Where(t => t.IsRecommendable && !t.Experimental)
                .Select(t => (t.Id, savedMap.TryGetValue(t.Id, out var o) ? o : t.RecommendedOption ?? t.DefaultOption, (string?)Fmt.GradeText(t.Grade)));
        }
        else
        {
            var prof = card.Profile!;
            if (prof.Conditions?.FormFactor is { } ff && p is not null && p.FormFactor != FormFactor.Unknown && ff != FormFactor.Unknown && ff != p.FormFactor)
                ConditionWarning = $"Este perfil foi pensado para {(ff == FormFactor.Laptop ? "notebooks" : "desktops")}, e este computador é {(p.FormFactor == FormFactor.Laptop ? "um notebook" : "um desktop")}.";
            entries = prof.Entries.Select(e => (e.TweakId, e.Option, e.Rationale?.Get(Loc.Language)));
        }

        var customSaved = card.Id == "custom" ? _settings.Get("profiles.custom", new List<CustomEntry>()).Select(s => s.TweakId).ToHashSet() : null;
        foreach (var (id, opt, why) in entries)
        {
            if (_catalog.Get(id) is not { } m) continue;
            Diff.Add(new ProfileDiffRow { M = m, Option = opt, Rationale = why, Included = customSaved?.Contains(id) ?? true });
        }
        foreach (var row in Diff.ToList())
        {
            var (ok, reason) = CompatibilityEvaluator.Evaluate(row.M, p);
            var st = await _orch.ReadStatusAsync(row.M);
            row.Current = st.Kind switch
            {
                TweakStateKind.MatchesOption => row.M.GetOption(st.CurrentOption!)?.Label.Get(Loc.Language) ?? st.CurrentOption!,
                TweakStateKind.NeedsElevationToRead => "precisa de admin para ler",
                TweakStateKind.NotApplicable => "não aplicável",
                _ => "valor personalizado",
            };
            if (!ok) { row.Action = "Não se aplica: " + reason; row.WillChange = false; row.Included = false; }
            else if (st.Kind == TweakStateKind.NotApplicable) { row.Action = "Não se aplica a este Windows"; row.WillChange = false; row.Included = false; }
            else if (st.Kind == TweakStateKind.MatchesOption && st.CurrentOption == row.Option) { row.Action = "Já está assim"; row.WillChange = false; }
            else { row.Action = "Vai mudar"; row.WillChange = true; }
        }
        ChangeCount = Diff.Count(d => d.WillChange && d.Included);
        DiffSummary = ChangeCount == 0 ? "Seu PC já está como este perfil propõe — nada a mudar." : $"{ChangeCount} ajuste(s) vão mudar e {Diff.Count(d => !d.WillChange)} já estão certos ou não se aplicam ao seu PC. Nada é aplicado sem sua confirmação.";
    }

    [RelayCommand]
    private void SaveCustom()
    {
        var list = Diff.Where(d => d.Included).Select(d => new CustomEntry(d.M.Id, d.Option)).ToList();
        _settings.Set("profiles.custom", list);
        App.Notify.Success("Perfil personalizado salvo", $"{list.Count} ajuste(s) selecionado(s).");
    }

    [RelayCommand]
    private async Task Apply()
    {
        if (Selected is null) return;
        if (Selected.Id == "default") { await RevertAll(); return; }
        var todo = Diff.Where(d => d.WillChange && d.Included).ToList();
        if (todo.Count == 0) { App.Notify.Info("Nada a aplicar", "Todos os ajustes já estão no estado do perfil."); return; }
        bool risky = todo.Any(t => t.M.Risk >= RiskLevel.Medium || t.M.Grade >= EvidenceGrade.C);
        var details = string.Join("\n", todo.Select(t => $"• {t.Name}: {t.Current} → {t.OptionLabel}  (risco {t.M.Risk.Pt()}, reinício: {t.M.Restart.Pt()})"));
        if (!await App.Dialogs.ConfirmAsync($"Aplicar perfil: {Selected.Name}", $"{todo.Count} ajuste(s) serão aplicados individualmente, com snapshot, verificação e rollback por item. O lote inteiro pode ser revertido no Histórico.",
                details + (string.IsNullOrEmpty(ConditionWarning) ? "" : "\n\n⚠ " + ConditionWarning), "Aplicar perfil", danger: risky, requiredPhrase: risky ? "APLICAR" : null)) return;

        await RunBusy("Aplicando perfil…", async () =>
        {
            if (_settings.Get("restore.pointBeforeProfile", true))
            {
                var rp = await _orch.CreateRestorePointAsync($"RBM Optimizer: antes do perfil {Selected.Name}");
                if (!rp.Success) App.Notify.Warning("Ponto de restauração", rp.Message);
            }
            using var _ = App.Tasks.Begin($"Perfil {Selected.Name}");
            var progress = new Progress<(int done, int total, string current)>(x => BusyText = $"Aplicando {x.done + 1}/{x.total}: {x.current}");
            var results = await _orch.ApplyBatchAsync(todo.Select(t => (t.M.Id, t.Option)), Selected.Name, progress);
            int ok = results.Count(r => r.Success);
            var batch = results.FirstOrDefault()?.Record.BatchId;
            await _db.UpsertAsync(Collections.ProfileApplications, Guid.NewGuid().ToString(), new { Profile = Selected.Id, Batch = batch, At = DateTimeOffset.Now, Applied = ok, Total = results.Count });
            if (ok == results.Count) App.Notify.Success("Perfil aplicado", $"{ok} ajuste(s) aplicados e verificados.");
            else App.Notify.Warning("Perfil aplicado parcialmente", $"{ok}/{results.Count}. Falhas: " + string.Join("; ", results.Where(r => !r.Success).Select(r => $"{r.Record.TweakId}: {r.Message}")));
            await App.Services.GetRequiredService<MainViewModel>().RefreshPendingRestartAsync();
            await BuildDiffAsync(Selected);
        });
    }

    private async Task RevertAll()
    {
        var active = (await _journal.ActiveChangesAsync()).OrderByDescending(r => r.At).ToList();
        if (active.Count == 0) { App.Notify.Info("Nada a reverter", "Não há alterações ativas do RBM."); return; }
        if (!await App.Dialogs.ConfirmAsync("Restaurar perfil padrão", $"Reverter {active.Count} alteração(ões) do RBM para os valores salvos antes de cada aplicação?", string.Join("\n", Diff.Select(d => "• " + d.Name)), "Reverter tudo", danger: true)) return;
        await RunBusy("Revertendo…", async () =>
        {
            int ok = 0; var issues = new List<string>();
            foreach (var r in active)
            {
                var o = await _orch.RevertAsync(r.ChangeId, false);
                if (o.Success) ok++; else issues.Add($"{r.TweakId}: {o.Message}");
            }
            if (issues.Count == 0) App.Notify.Success("Perfil padrão restaurado", $"{ok} alteração(ões) revertidas.");
            else App.Notify.Warning("Restauração parcial", $"{ok}/{active.Count}. Itens com divergência externa: reverta individualmente no Histórico. " + string.Join("; ", issues.Take(3)));
            await BuildDiffAsync(Selected);
        });
    }

    [RelayCommand]
    private void OpenOptimizations() => App.Services.GetRequiredService<MainViewModel>().Navigate("optimizations");
}
