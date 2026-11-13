using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Tweaks;
using Rbm.Optimization;
using Rbm.Optimization.Cleanup;
using Rbm.Optimization.Providers;
using Rbm.Processes;
using Rbm.Hardware;
using Rbm.Restore;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

/// <summary>Um nível de otimização (Leve / Média / Pesada) descrito em linguagem simples.</summary>
public sealed partial class LevelCard : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Tagline { get; init; }
    public required string Icon { get; init; }
    public required IReadOnlyList<string> Does { get; init; }
    public required string Time { get; init; }
    public required string Admin { get; init; }
    public required string Restart { get; init; }
    public string? Badge { get; init; }
    /// <summary>Perfis do catálogo aplicados (em ordem; o último prevalece em caso de conflito).</summary>
    public required string[] Profiles { get; init; }
    public required string[] Cleanup { get; init; }
    public bool RestorePoint { get; init; }
    /// <summary>Desativa programas de inicialização marcados como seguros para desativar.</summary>
    public bool Startup { get; init; }
    /// <summary>Ativa o plano de energia Alto desempenho (somente desktop).</summary>
    public bool PowerPlan { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Um item da prévia ("o que vai acontecer no seu PC").</summary>
public sealed record PlanItem(string Kind, string Title, string Detail, bool NeedsAdmin);

/// <summary>Um passo da execução, com estado ao vivo.</summary>
public sealed partial class RunStep : ObservableObject
{
    public required string Title { get; init; }
    [ObservableProperty] private string _state = "pending"; // pending | running | ok | warn | fail | skipped
    [ObservableProperty] private string _detail = "";
    public string Glyph => State switch { "ok" => "", "warn" => "", "fail" => "", "skipped" => "", "running" => "", _ => "" };
    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(Glyph));
}

public sealed partial class RecommendationsViewModel : PageViewModel
{
    private readonly TweakCatalog _catalog;
    private readonly ChangeOrchestrator _orch;
    private readonly ISystemInventory _inventory;
    private readonly IAgentClient _agent;
    private readonly DiagnosticsState _diag;
    public override string Key => "recommend";

    public ObservableCollection<LevelCard> Levels { get; } = new();
    public ObservableCollection<PlanItem> Plan { get; } = new();
    public ObservableCollection<RunStep> Steps { get; } = new();

    [ObservableProperty] private LevelCard? _selected;
    [ObservableProperty] private bool _planLoading;
    [ObservableProperty] private string _planSummary = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _finished;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _needsReboot;
    private Guid? _lastBatch;
    private List<(string id, string option)> _tweaks = new();
    private List<StartupEntry> _startupToDisable = new();
    private bool _powerPlanPlanned;
    // Para desfazer: o que esta execução mudou fora do motor de ajustes.
    private List<StartupEntry> _lastStartupDisabled = new();
    private Guid? _lastPreviousScheme;

    /// <summary>
    /// Apps que nunca são desativados automaticamente: sincronização de arquivos e comunicação
    /// (a pessoa perderia mensagens ou sincronização sem perceber). Continuam desativáveis manualmente.
    /// </summary>
    private static readonly HashSet<string> NeverAutoDisable = new(StringComparer.OrdinalIgnoreCase)
        { "onedrive", "googledrive", "dropbox", "discord", "teams", "skype", "zoom", "whatsapp", "telegram" };

    public RecommendationsViewModel(TweakCatalog catalog, ChangeOrchestrator orch, ISystemInventory inventory, IAgentClient agent, DiagnosticsState diag)
    {
        _catalog = catalog; _orch = orch; _inventory = inventory; _agent = agent; _diag = diag;
        bool laptop = inventory.Current?.FormFactor == FormFactor.Laptop;
        Levels.Add(new LevelCard
        {
            Key = "light", Title = "Leve", Icon = "", Tagline = "Uma faxina rápida e segura, sem mudar como o PC funciona.",
            Does = new[]
            {
                "Apaga arquivos temporários antigos e relatórios de falhas de programas",
                "Limpa o cache de miniaturas do Explorer (ele se refaz sozinho)",
                "Liga os recursos de manutenção do SSD e o Modo de Jogo, se estiverem desligados",
                "Desliga a gravação automática em segundo plano do Xbox Game Bar",
            },
            Time = "cerca de 1 minuto", Admin = "Pode pedir permissão uma vez", Restart = "Não precisa reiniciar",
            Profiles = new[] { "safe" }, Cleanup = new[] { "user.temp", "user.crashdumps", "explorer.thumbcache" },
        });
        Levels.Add(new LevelCard
        {
            Key = "medium", Title = "Média", Icon = "", Badge = "Recomendada", Tagline = "O equilíbrio ideal para o dia a dia e jogos.",
            Does = new[]
            {
                "Tudo da Leve",
                "Impede launchers de jogos e atualizadores (Steam, Epic, Adobe, Spotify…) de abrirem sozinhos com o Windows — o PC liga mais rápido e sobra memória",
                "Limpa também os temporários e relatórios de erro do Windows",
                "Ajustes com benefício comprovado: jogos em janela mais fluidos, interface mais ágil",
                "Cria um ponto de restauração antes de mudar qualquer coisa",
            },
            Time = "2 a 3 minutos", Admin = "Pede permissão de administrador", Restart = "Alguns itens valem após reiniciar",
            Profiles = new[] { "safe", "balanced" }, Cleanup = new[] { "user.temp", "user.crashdumps", "explorer.thumbcache", "system.temp", "system.wer" },
            RestorePoint = true, Startup = true,
        });
        Levels.Add(new LevelCard
        {
            Key = "heavy", Title = "Pesada", Icon = "", Tagline = laptop ? "Desempenho máximo quando o notebook está na tomada." : "Máximo desempenho para jogos competitivos.",
            Does = new[]
            {
                "Tudo da Média",
                laptop ? "Mantém o plano de energia do fabricante (melhor para bateria e temperatura)" : "Ativa o plano de energia Alto desempenho: o processador não reduz a velocidade para economizar",
                laptop ? "Prioriza desempenho na tomada e mantém a economia na bateria" : "Processador e placa de vídeo respondem mais rápido (gasta mais energia)",
                "Reduz economias de energia de USB e PCIe que podem causar pequenos atrasos",
                "Remove capturas automáticas e ajustes que atrapalham jogos",
            },
            Time = "3 a 4 minutos", Admin = "Pede permissão de administrador", Restart = "Recomendado reiniciar no fim",
            Profiles = laptop ? new[] { "safe", "balanced", "laptop", "gaming-laptop" } : new[] { "safe", "balanced", "competitive" },
            Cleanup = new[] { "user.temp", "user.crashdumps", "explorer.thumbcache", "system.temp", "system.wer" },
            RestorePoint = true, Startup = true, PowerPlan = !laptop,
        });
    }

    public override Task OnNavigatedToAsync()
    {
        if (Selected is null) Select(Levels[1]);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Select(LevelCard? level)
    {
        if (level is null || Running) return;
        foreach (var l in Levels) l.IsSelected = l == level;
        Selected = level;
        Finished = false;
        _ = BuildPlanAsync(level);
    }

    /// <summary>Monta a prévia real: só entram ajustes compatíveis que ainda não estão no estado desejado.</summary>
    private async Task BuildPlanAsync(LevelCard level)
    {
        PlanLoading = true;
        Plan.Clear();
        try
        {
            var wanted = new Dictionary<string, (string option, string? why)>();
            foreach (var pid in level.Profiles)
                if (_catalog.Profiles.FirstOrDefault(p => p.Id == pid) is { } prof)
                    foreach (var e in prof.Entries) wanted[e.TweakId] = (e.Option, e.Rationale?.Get(Loc.Language));

            _tweaks = new();
            int already = 0, notApplicable = 0;
            foreach (var (id, (option, _)) in wanted)
            {
                if (_catalog.Get(id) is not { } m) continue;
                if (!CompatibilityEvaluator.Evaluate(m, _inventory.Current).Compatible) { notApplicable++; continue; }
                var st = await _orch.ReadStatusAsync(m);
                if (st.Kind == TweakStateKind.NotApplicable) { notApplicable++; continue; }
                if (st.Kind == TweakStateKind.MatchesOption && st.CurrentOption == option) { already++; continue; }
                _tweaks.Add((id, option));
                var label = m.GetOption(option)?.Label.Get(Loc.Language) ?? option;
                Plan.Add(new PlanItem("Ajuste", m.Name.Get(Loc.Language), $"Vai ficar: {label}", m.Options.SelectMany(o => o.Targets).Any(t => t.WriteRequiresAdmin)));
            }

            long cleanBytes = 0;
            foreach (var cid in level.Cleanup)
            {
                if (CleanupCatalog.Get(cid) is not { } c) continue;
                string detail;
                if (c.RequiresAdmin) detail = "Tamanho calculado na hora (precisa de permissão de administrador)";
                else
                {
                    var r = await CleanupEngine.AnalyzeAsync(c);
                    cleanBytes += r.Bytes;
                    detail = r.Available ? $"{Fmt.Bytes(r.Bytes)} para liberar" : "Nada a limpar";
                }
                Plan.Add(new PlanItem("Limpeza", c.Name, detail, c.RequiresAdmin));
            }
            _startupToDisable = new();
            if (level.Startup)
            {
                var entries = await new StartupManager().EnumerateAsync(verifySignatures: false);
                foreach (var e in entries.Where(e => e.State == StartupEntryState.Enabled && e.CanToggle
                             && e.Recommendation.Kind == AutostartRecommendationKind.OptionalSafeToDisable
                             && e.Recommendation.KnownAppId is { } app && !NeverAutoDisable.Contains(app)))
                {
                    _startupToDisable.Add(e);
                    var known = AutostartAdvisor.KnownApps.FirstOrDefault(k => k.Id == e.Recommendation.KnownAppId);
                    Plan.Add(new PlanItem("Inicialização", $"Não abrir sozinho: {known?.DisplayName ?? e.Name}", known?.Note ?? "Continua funcionando quando você abrir.", e.ToggleRequiresElevation));
                }
            }

            _powerPlanPlanned = false;
            if (level.PowerPlan)
            {
                var (active, exists) = await Task.Run(() => (PowerApi.GetActiveScheme(), PowerApi.SchemeExists(PowerSchemes.HighPerformance)));
                if (exists && active != PowerSchemes.HighPerformance && active != PowerSchemes.UltimatePerformance)
                {
                    _powerPlanPlanned = true;
                    Plan.Insert(0, new PlanItem("Energia", "Plano de energia: Alto desempenho", $"Hoje: {PowerApi.FriendlyName(active)}. O processador mantém a velocidade alta em vez de oscilar para economizar.", false));
                }
            }

            if (level.RestorePoint) Plan.Insert(0, new PlanItem("Segurança", "Ponto de restauração do Windows", "Criado antes de qualquer mudança", true));

            PlanSummary = $"{_tweaks.Count} ajuste(s) a aplicar"
                          + (_startupToDisable.Count > 0 ? $" · {_startupToDisable.Count} programa(s) deixam de abrir com o Windows" : "")
                          + (_powerPlanPlanned ? " · plano Alto desempenho" : "")
                          + $" · cerca de {Fmt.Bytes(cleanBytes)} para liberar (sem contar arquivos do sistema)"
                          + (already > 0 ? $" · {already} ajuste(s) já estão certos" : "")
                          + (notApplicable > 0 ? $" · {notApplicable} não se aplicam ao seu PC" : "");
        }
        catch (Exception ex) { PlanSummary = "Não foi possível montar a prévia: " + ex.Message; }
        finally { PlanLoading = false; }
    }

    [RelayCommand]
    private async Task Run()
    {
        var level = Selected;
        if (level is null || Running) return;
        var details = string.Join("\n", Plan.Select(p => $"• {p.Title} — {p.Detail}"));
        if (!await App.Dialogs.ConfirmAsync($"Otimização {level.Title}", "O RBM vai fazer as mudanças abaixo. Tudo é registrado e pode ser desfeito depois com um clique.", details, "Otimizar agora"))
            return;

        Running = true; Finished = false; NeedsReboot = false;
        Steps.Clear();
        var sRestore = level.RestorePoint ? AddStep("Criar ponto de restauração") : null;
        var sClean = AddStep("Limpar arquivos temporários");
        var sAdminClean = level.Cleanup.Any(c => CleanupCatalog.Get(c)?.RequiresAdmin == true) ? AddStep("Limpar arquivos temporários do Windows") : null;
        var sStartup = _startupToDisable.Count > 0 ? AddStep($"Tirar programas da inicialização ({_startupToDisable.Count})") : null;
        var sPower = _powerPlanPlanned ? AddStep("Ativar plano de energia Alto desempenho") : null;
        var sTweaks = AddStep($"Aplicar ajustes ({_tweaks.Count})");
        var sCheck = AddStep("Verificar a saúde do PC");
        long freed = 0; int applied = 0, failed = 0;
        using var busy = App.Tasks.Begin($"Otimização {level.Title}");
        try
        {
            if (sRestore is not null)
            {
                sRestore.State = "running";
                var rp = await _orch.CreateRestorePointAsync($"RBM Optimizer: antes da otimização {level.Title}");
                (sRestore.State, sRestore.Detail) = rp.Success ? ("ok", "Criado") : ("warn", rp.Message);
            }

            sClean.State = "running";
            foreach (var c in level.Cleanup.Select(CleanupCatalog.Get).OfType<CleanupCategory>().Where(c => !c.RequiresAdmin))
            {
                var r = await CleanupEngine.CleanAsync(c);
                freed += r.Bytes;
            }
            (sClean.State, sClean.Detail) = ("ok", $"{Fmt.Bytes(freed)} liberados (arquivos em uso foram mantidos)");

            if (sAdminClean is not null)
            {
                sAdminClean.State = "running";
                var ids = level.Cleanup.Where(c => CleanupCatalog.Get(c)?.RequiresAdmin == true).ToList();
                var resp = await _agent.InvokeAsync(AgentOps.CleanupRun, new CleanupRequest(ids, false), TimeSpan.FromMinutes(10));
                if (resp.Ok && resp.Read<CleanupRunResponse>() is { } rr) { freed += rr.TotalBytes; (sAdminClean.State, sAdminClean.Detail) = ("ok", $"{Fmt.Bytes(rr.TotalBytes)} liberados"); }
                else (sAdminClean.State, sAdminClean.Detail) = ("warn", "Pulado: " + (resp.Error ?? "sem permissão de administrador"));
            }

            _lastStartupDisabled = new();
            if (sStartup is not null)
            {
                sStartup.State = "running";
                foreach (var e in _startupToDisable)
                    if (await SetStartupAsync(e, false)) _lastStartupDisabled.Add(e);
                int miss = _startupToDisable.Count - _lastStartupDisabled.Count;
                (sStartup.State, sStartup.Detail) = miss == 0 ? ("ok", $"{_lastStartupDisabled.Count} programa(s) não abrem mais sozinhos")
                                                              : ("warn", $"{_lastStartupDisabled.Count} desativado(s), {miss} não puderam ser alterados");
            }

            _lastPreviousScheme = null;
            if (sPower is not null)
            {
                sPower.State = "running";
                try
                {
                    var prev = await Task.Run(PowerApi.GetActiveScheme);
                    await Task.Run(() => PowerApi.SetActive(PowerSchemes.HighPerformance));
                    _lastPreviousScheme = prev;
                    (sPower.State, sPower.Detail) = ("ok", $"Antes: {PowerApi.FriendlyName(prev)}");
                    Serilog.Log.Information("Otimização {Level}: plano de energia {Prev} -> Alto desempenho", level.Title, prev);
                }
                catch (Exception ex) { (sPower.State, sPower.Detail) = ("warn", "Não foi possível trocar: " + ex.Message); }
            }

            sTweaks.State = "running";
            if (_tweaks.Count == 0) (sTweaks.State, sTweaks.Detail) = ("ok", "Tudo já estava como recomendado");
            else
            {
                var progress = new Progress<(int done, int total, string current)>(p => sTweaks.Detail = p.total == 0 ? "" : $"{p.done}/{p.total}");
                var results = await _orch.ApplyBatchAsync(_tweaks, $"Otimização {level.Title}", progress);
                applied = results.Count(r => r.Success);
                failed = results.Count - applied;
                _lastBatch = results.FirstOrDefault()?.Record.BatchId;
                NeedsReboot = results.Any(r => r.Success && r.Record.Restart == RestartRequirement.Reboot);
                (sTweaks.State, sTweaks.Detail) = failed == 0 ? ("ok", $"{applied} aplicado(s) e conferido(s)") : ("warn", $"{applied} aplicado(s), {failed} não aplicado(s) (nada ficou pela metade)");
            }

            sCheck.State = "running";
            await _diag.RunAsync();
            (sCheck.State, sCheck.Detail) = ("ok", "Painel atualizado");

            ResultTitle = failed == 0 ? "Otimização concluída" : "Otimização concluída com avisos";
            ResultText = $"{Fmt.Bytes(freed)} liberados · {applied} ajuste(s) aplicados"
                         + (_lastStartupDisabled.Count > 0 ? $" · {_lastStartupDisabled.Count} programa(s) fora da inicialização" : "")
                         + (_lastPreviousScheme is not null ? " · plano Alto desempenho ativo" : "") + "." + (NeedsReboot ? " Reinicie o computador para tudo entrar em vigor." : "")
                         + " Se notar algo estranho, use “Desfazer esta otimização”.";
            await App.Services.GetRequiredService<MainViewModel>().RefreshPendingRestartAsync();
        }
        catch (Exception ex)
        {
            foreach (var s in Steps.Where(s => s.State == "running")) (s.State, s.Detail) = ("fail", ex.Message);
            ResultTitle = "A otimização foi interrompida"; ResultText = ex.Message;
        }
        finally { Running = false; Finished = true; _ = BuildPlanAsync(level); }
    }

    /// <summary>Liga/desliga uma entrada de inicialização (HKCU direto; HKLM pelo RBM Agent). Reversível.</summary>
    private async Task<bool> SetStartupAsync(StartupEntry e, bool enabled)
    {
        try
        {
            if (!StartupManager.IsToggleable(e.Location)) return false;
            if (!StartupManager.RequiresElevation(e.Location))
                return (await Task.Run(() => new StartupManager().SetEnabled(e.Location, e.Name, enabled))).Success;
            var resp = await _agent.InvokeAsync(AgentOps.StartupSetEnabled, new StartupToggleRequest(e.Location.ToString(), e.Name, enabled), TimeSpan.FromMinutes(2));
            return resp.Ok;
        }
        catch (Exception ex) { App.Log(ex, $"Otimização: inicialização {e.Name}"); return false; }
    }

    private RunStep AddStep(string title) { var s = new RunStep { Title = title }; Steps.Add(s); return s; }

    [RelayCommand]
    private async Task Undo()
    {
        if (_lastBatch is null && _lastStartupDisabled.Count == 0 && _lastPreviousScheme is null)
        { App.Services.GetRequiredService<MainViewModel>().Navigate("history"); return; }
        if (!await App.Dialogs.ConfirmAsync("Desfazer otimização", "Voltar tudo o que esta otimização mudou para como estava antes? (Arquivos apagados na limpeza não voltam — eram temporários.)", confirmText: "Desfazer")) return;
        await RunBusy("Desfazendo…", async () =>
        {
            int ok = 0, total = 0;
            if (_lastBatch is { } batch)
            {
                var res = await _orch.RevertBatchAsync(batch, false);
                ok += res.Count(r => r.outcome.Success); total += res.Count;
            }
            foreach (var e in _lastStartupDisabled) { total++; if (await SetStartupAsync(e, true)) ok++; }
            if (_lastPreviousScheme is { } prev)
            {
                total++;
                try { await Task.Run(() => PowerApi.SetActive(prev)); ok++; }
                catch (Exception ex) { App.Log(ex, "Otimização: restaurar plano de energia"); }
            }
            if (ok == total) App.Notify.Success("Desfeito", $"{ok} mudança(s) voltaram ao estado anterior.");
            else App.Notify.Warning("Desfeito em parte", $"{ok}/{total}. Veja os demais em Histórico e desfazer e em Inicialização.");
            _lastBatch = null; _lastStartupDisabled = new(); _lastPreviousScheme = null; Finished = false;
            if (Selected is not null) await BuildPlanAsync(Selected);
        });
    }

    [RelayCommand]
    private static void Go(string key) => App.Services.GetRequiredService<MainViewModel>().Navigate(key);
}
