using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Optimization.Cleanup;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

/// <summary>
/// Limpeza segura: analisar (simulação, nada é apagado) → selecionar → limpar.
/// Categorias do perfil rodam na própria UI; categorias administrativas vão ao RBM Agent (op cleanup.run).
/// </summary>
public sealed partial class CleanupViewModel : PageViewModel
{
    public override string Key => "cleanup";
    private const string AuditAction = "cleanup.run";

    public ObservableCollection<CleanupItem> Items { get; } = new();
    /// <summary>Categorias de risco mínimo, sem administrador e sem perda definitiva (as mesmas pré-selecionadas).</summary>
    public ObservableCollection<CleanupItem> SafeItems { get; } = new();
    /// <summary>Categorias que merecem revisão: risco maior, administrador (UAC) ou exclusão definitiva.</summary>
    public ObservableCollection<CleanupItem> ReviewItems { get; } = new();
    public ObservableCollection<CleanupHistoryRow> History { get; } = new();
    public string DeliveryOptimizationNote => CleanupCatalog.DeliveryOptimizationNote;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CleanCommand))] private bool _isRunning;
    [ObservableProperty] private string _runningText = "";
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _totalCaption = "Clique em Analisar para medir (nada é apagado).";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CleanCommand))] private int _selectedCount;
    [ObservableProperty] private string _selectedText = "Nenhuma categoria selecionada";
    [ObservableProperty] private string? _lastResult;
    [ObservableProperty] private bool _agentConnected;

    // ---- Resumo em linguagem simples (banner do topo) ----
    [ObservableProperty] private string _heroTitle = "Descubra quanto espaço dá para liberar";
    [ObservableProperty] private string _heroText = "A análise só mede temporários e caches — nada é apagado sem a sua confirmação.";
    [ObservableProperty] private string _heroChip = "Ainda não analisado";
    [ObservableProperty] private string _heroTone = "info";
    [ObservableProperty] private bool _hasAnalyzed;
    [ObservableProperty] private string _analyzeLabel = "Analisar";

    private CancellationTokenSource? _cts;
    private bool _analyzedOnce;

    public CleanupViewModel()
    {
        foreach (var c in CleanupEngine.Categories)
        {
            var item = new CleanupItem(c);
            item.SelectionChanged += (_, _) => UpdateSelection();
            item.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanupItem.Bytes) or nameof(CleanupItem.Files)) UpdateSelection(); };
            Items.Add(item);
            (IsSafe(item) ? SafeItems : ReviewItems).Add(item);
        }
    }

    private static bool IsSafe(CleanupItem i) => i.Risk == RiskLevel.Minimal && !i.RequiresAdmin && !i.Irreversible;

    public override async Task OnNavigatedToAsync()
    {
        AgentConnected = AgentCall.IsConnected;
        await LoadHistoryAsync();
        // A análise é somente leitura e rápida (< 1 s típico): roda automaticamente na primeira visita.
        if (!_analyzedOnce && !IsRunning) await Analyze();
    }

    public override void OnNavigatedFrom()
    {
        // Cancela apenas análises; uma limpeza em andamento termina (arquivos já removidos não voltam).
        if (IsRunning && RunningText.StartsWith("Analisando", StringComparison.Ordinal)) _cts?.Cancel();
    }

    private void UpdateSelection()
    {
        var sel = Items.Where(i => i.IsSelected).ToList();
        SelectedCount = sel.Count;
        long bytes = sel.Where(i => i.IsAnalyzed).Sum(i => i.Bytes);
        SelectedText = sel.Count == 0 ? "Nenhuma categoria selecionada"
            : $"{sel.Count} categoria(s) · até {Fmt.Bytes(bytes)}" + (sel.Any(i => i.RequiresAdmin) ? " · inclui itens de administrador (UAC)" : "");
        UpdateHero();
    }

    /// <summary>Frase principal do topo, sempre a partir dos números reais da última análise.</summary>
    private void UpdateHero()
    {
        if (IsRunning)
        {
            HeroChip = RunningText.StartsWith("Limpando", StringComparison.Ordinal) ? "Limpando…" : "Analisando…";
            HeroTone = "info";
            if (!HasAnalyzed) { HeroTitle = "Analisando o seu PC…"; HeroText = "Medindo temporários e caches. Nada é apagado nesta etapa."; }
            return;
        }
        var analyzed = Items.Where(i => i.IsAnalyzed && i.Available).ToList();
        if (!HasAnalyzed || analyzed.Count == 0)
        {
            if (Items.Any(i => i.Cleaned))
            {
                HeroTitle = "Limpeza concluída";
                HeroText = "Clique em “Analisar novamente” para ver se sobrou algo para liberar.";
                HeroChip = "Concluído"; HeroTone = "ok";
            }
            return;
        }
        long total = analyzed.Sum(i => i.Bytes);
        var sel = Items.Where(i => i.IsSelected && i.IsAnalyzed).ToList();
        long selBytes = sel.Sum(i => i.Bytes);
        if (total <= 0)
        {
            HeroTitle = "Seu PC já está limpo";
            HeroText = "Não encontramos temporários ou caches para remover agora.";
            HeroChip = "Nada a limpar"; HeroTone = "ok";
            return;
        }
        long safe = analyzed.Where(IsSafe).Sum(i => i.Bytes);
        HeroTitle = total == safe ? $"Você pode liberar cerca de {Fmt.Bytes(total)} com segurança" : $"Você pode liberar até {Fmt.Bytes(total)}";
        HeroText = (total > safe
                       ? (safe > 0 ? $"{Fmt.Bytes(safe)} são temporários sem efeito colateral; os outros {Fmt.Bytes(total - safe)} estão em itens que valem uma revisão (veja abaixo). "
                                   : "Todo esse espaço está em itens que valem uma revisão antes (veja abaixo). ")
                       : "")
                   + (sel.Count > 0 ? $"Selecionado agora: {Fmt.Bytes(selBytes)} em {sel.Count} categoria(s)." : "Marque as categorias que deseja limpar.");
        HeroChip = sel.Count > 0 ? "Pronto para limpar" : "Nada selecionado";
        HeroTone = sel.Count > 0 ? "ok" : "warn";
    }

    private void UpdateTotals()
    {
        var analyzed = Items.Where(i => i.IsAnalyzed && i.Available).ToList();
        if (analyzed.Count == 0) return;
        TotalText = Fmt.Bytes(analyzed.Sum(i => i.Bytes));
        TotalCaption = $"recuperáveis em {analyzed.Sum(i => i.Files):N0} arquivo(s)/item(ns) · {analyzed.Count} categoria(s) encontradas";
    }

    private bool CanAnalyze() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task Analyze()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true; RunningText = "Analisando (simulação, nada é apagado)…";
        UpdateHero();
        using var _ = App.Tasks.Begin("Analisando limpeza");
        AgentConnected = AgentCall.IsConnected;
        try
        {
            // Categorias administrativas: com o agente já conectado, a análise elevada é completa; sem ele, a leitura
            // local é parcial (sem UAC só para analisar).
            Dictionary<string, CleanupCategoryResult>? elevated = null;
            var adminIds = Items.Where(i => i.RequiresAdmin).Select(i => i.Id).ToList();
            if (AgentConnected && adminIds.Count > 0)
                elevated = await TryElevatedDryRunAsync(adminIds, ct);

            foreach (var item in Items)
            {
                ct.ThrowIfCancellationRequested();
                item.IsWorking = true;
                try
                {
                    var r = elevated is not null && elevated.TryGetValue(item.Id, out var er) ? er : await CleanupEngine.AnalyzeAsync(item.Category, ct);
                    bool first = !item.IsAnalyzed && !_analyzedOnce;
                    item.Apply(r);
                    // Pré-seleção conservadora: só risco mínimo, sem administrador e sem perda definitiva.
                    if (first && r.Available && r.Files > 0) item.IsSelected = item.Risk == RiskLevel.Minimal && !item.RequiresAdmin && !item.Irreversible;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    App.Log(ex, $"Análise de limpeza {item.Id}");
                    item.Status = "Falha na análise"; item.Notes = ex.Message;
                }
                finally { item.IsWorking = false; }
            }
            _analyzedOnce = true;
            HasAnalyzed = true;
            AnalyzeLabel = "Analisar novamente";
            UpdateTotals();
            UpdateSelection();
        }
        catch (OperationCanceledException) { App.Notify.Info("Análise cancelada", "Nenhum arquivo foi alterado."); }
        catch (Exception ex) { App.Log(ex, "Análise de limpeza"); App.Notify.Error("Falha na análise", ex.Message); }
        finally { IsRunning = false; RunningText = ""; foreach (var i in Items) i.IsWorking = false; UpdateHero(); }
    }

    /// <summary>Simulação elevada silenciosa (sem toasts): se a operação não estiver disponível, cai para a análise local.</summary>
    private static async Task<Dictionary<string, CleanupCategoryResult>?> TryElevatedDryRunAsync(List<string> ids, CancellationToken ct)
    {
        try
        {
            var r = await AgentCall.Client.InvokeAsync(AgentOps.CleanupRun, new CleanupRequest(ids, DryRun: true), TimeSpan.FromMinutes(2), ct);
            if (!r.Ok) { Serilog.Log.Information("cleanup.run (simulação) indisponível no agente: {Error}", r.Error); return null; }
            return AgentCall.Read<CleanupRunResponse>(r)?.Results.ToDictionary(x => x.CategoryId, StringComparer.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.Log(ex, "cleanup.run (simulação)"); return null; }
    }

    private bool CanClean() => !IsRunning && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task Clean()
    {
        var selected = Items.Where(i => i.IsSelected && i.Available).ToList();
        if (selected.Count == 0) return;

        long total = selected.Where(i => i.IsAnalyzed).Sum(i => i.Bytes);
        var details = new StringBuilder();
        foreach (var i in selected)
        {
            details.Append("• ").Append(i.Name);
            if (i.IsAnalyzed) details.Append(" — ").Append(i.SizeText).Append(" (").Append(i.FilesText).Append(')');
            if (i.RequiresAdmin) details.Append(" [administrador]");
            details.AppendLine().Append("   Efeito: ").AppendLine(i.SideEffects);
        }
        details.AppendLine().Append("Arquivos em uso, junções/links e arquivos recentes (onde há filtro de 24 h) são mantidos. Nada fora das pastas listadas é tocado.");

        bool hasRecycle = selected.Any(i => i.Irreversible);
        bool hasAdmin = selected.Any(i => i.RequiresAdmin);
        string msg = $"Serão removidos até {Fmt.Bytes(total)} de {selected.Count} categoria(s). A exclusão não pode ser desfeita."
            + (hasRecycle ? " A Lixeira será esvaziada definitivamente." : "")
            + (hasAdmin ? " Itens de administrador serão executados pelo RBM Agent (UAC)." : "");
        if (!await App.Dialogs.ConfirmAsync("Limpar arquivos selecionados", msg, details.ToString(), "Limpar", danger: hasRecycle || hasAdmin,
                requiredPhrase: hasRecycle ? "ESVAZIAR" : null))
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true; RunningText = "Limpando…";
        UpdateHero();
        using var _ = App.Tasks.Begin("Limpeza de arquivos");
        var results = new List<CleanupCategoryResult>();
        try
        {
            // 1) Categorias do perfil: no processo da UI (contexto do usuário).
            foreach (var item in selected.Where(i => !i.RequiresAdmin))
            {
                if (ct.IsCancellationRequested) break;
                item.IsWorking = true;
                try
                {
                    var r = await CleanupEngine.CleanAsync(item.Category, ct);
                    item.Apply(r); results.Add(r);
                    await SysUi.AuditAsync(AuditAction, Describe(r), true);
                }
                catch (OperationCanceledException) { item.Status = "Cancelado (parcial)"; await SysUi.AuditAsync(AuditAction, $"{item.Name}: cancelado pelo usuário (parcial)", false); }
                catch (Exception ex)
                {
                    App.Log(ex, $"Limpeza {item.Id}");
                    item.Status = "Falha"; item.Notes = ex.Message;
                    await SysUi.AuditAsync(AuditAction, $"{item.Name}: falha — {ex.Message}", false);
                }
                finally { item.IsWorking = false; }
            }

            // 2) Categorias administrativas: uma chamada ao agente (allow-list revalidada lá).
            var admin = selected.Where(i => i.RequiresAdmin).ToList();
            if (admin.Count > 0 && !ct.IsCancellationRequested)
            {
                foreach (var i in admin) i.IsWorking = true;
                RunningText = "Limpando itens de administrador (RBM Agent)…";
                var resp = await AgentCall.InvokeAsync(AgentOps.CleanupRun, new CleanupRequest(admin.Select(i => i.Id).ToList(), DryRun: false),
                    "Limpeza (administrador)", TimeSpan.FromMinutes(10), ct);
                AgentConnected = AgentCall.IsConnected;
                var payload = AgentCall.Read<CleanupRunResponse>(resp);
                if (payload is null)
                {
                    foreach (var i in admin) { i.Status = resp is null ? "Não executado (sem agente)" : "Resposta inválida do agente"; }
                    await SysUi.AuditAsync(AuditAction, $"Itens de administrador não executados: {string.Join(", ", admin.Select(a => a.Name))}", false);
                }
                else
                {
                    foreach (var r in payload.Results)
                    {
                        Items.FirstOrDefault(i => i.Id == r.CategoryId)?.Apply(r);
                        results.Add(r);
                        await SysUi.AuditAsync(AuditAction, Describe(r) + " (via RBM Agent)", true);
                    }
                }
                foreach (var i in admin) i.IsWorking = false;
            }

            long freed = results.Sum(r => r.Bytes);
            int skipped = results.Sum(r => r.Skipped);
            LastResult = $"Última limpeza ({DateTime.Now:HH:mm}): {Fmt.Bytes(freed)} liberados em {results.Sum(r => r.Files):N0} arquivo(s)/item(ns)"
                + (skipped > 0 ? $" · {skipped} em uso mantido(s)" : "") + ".";
            if (results.Count > 0) App.Notify.Success("Limpeza concluída", $"{Fmt.Bytes(freed)} liberados" + (skipped > 0 ? $"; {skipped} arquivo(s) em uso foram mantidos." : "."));
            if (ct.IsCancellationRequested) App.Notify.Info("Limpeza interrompida", "As categorias restantes não foram processadas.");
        }
        catch (OperationCanceledException) { App.Notify.Info("Limpeza interrompida", "As categorias restantes não foram processadas."); }
        catch (Exception ex) { App.Log(ex, "Limpeza"); App.Notify.Error("Falha na limpeza", ex.Message); }
        finally
        {
            IsRunning = false; RunningText = "";
            foreach (var i in Items) i.IsWorking = false;
            UpdateSelection();
            await LoadHistoryAsync();
        }
    }

    private static string Describe(CleanupCategoryResult r) =>
        $"{r.Name}: {Fmt.Bytes(r.Bytes)} liberados, {r.Files} arquivo(s)/item(ns) removido(s), {r.Skipped} em uso mantido(s)"
        + (r.ReparsePointsSkipped > 0 ? $", {r.ReparsePointsSkipped} link(s) ignorado(s)" : "")
        + (r.Notes.Count > 0 ? " — " + string.Join(" | ", r.Notes) : "");

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll() { foreach (var i in Items.Where(i => i.CanSelect)) i.IsSelected = true; }

    [RelayCommand]
    private void SelectNone() { foreach (var i in Items) i.IsSelected = false; }

    /// <summary>Meio oficial para o cache do Delivery Optimization (e demais temporários do Windows).</summary>
    [RelayCommand]
    private void OpenStorageSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
        catch (Exception ex) { App.Notify.Error("Não foi possível abrir as Configurações", ex.Message); }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var rows = await SysUi.Db.ListAuditAsync(400);
            History.Clear();
            foreach (var a in rows.Where(a => a.Action == AuditAction).Take(12))
                History.Add(new CleanupHistoryRow(a.At.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.GetCultureInfo("pt-BR")), a.Detail, a.Success));
        }
        catch (Exception ex) { App.Log(ex, "Histórico de limpeza"); }
    }
}
