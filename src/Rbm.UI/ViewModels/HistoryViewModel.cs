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
using Microsoft.Win32;

namespace Rbm.UI.ViewModels;

public sealed class ChangeRow
{
    public required TweakChangeRecord Record { get; init; }
    public required string TweakName { get; init; }
    public string When => Record.At.ToString("g");
    public string FromTo { get; init; } = "";
    public string StatusText => Record.Status switch
    {
        ChangeStatus.Applied => "Em vigor",
        ChangeStatus.PendingRestart => "Em vigor após reiniciar",
        ChangeStatus.FailedRolledBack => "Não aplicado (nada mudou)",
        ChangeStatus.FailedPartial => "Aplicado só em parte — revise",
        ChangeStatus.Reverted => $"Desfeito em {Record.RevertedAt:g}",
        _ => Record.Status.ToString(),
    };
    public bool CanRevert => Record.Status is ChangeStatus.Applied or ChangeStatus.PendingRestart or ChangeStatus.FailedPartial;
    public string Origin => Record.ProfileName is { } p ? $"Perfil {p}" : Record.BatchId is null ? "Manual" : "Em lote";
    public string Details => string.Join("\n", Record.Changes.Select(c => $"• {c.Target.Display}\n    antes: {c.Before}   depois: {c.After}   {(c.Verified ? "✓ verificado" : "✗ não verificado")}{(c.Elevated ? "   [agente]" : "")}"))
                             + (Record.Error is null ? "" : $"\nErro: {Record.Error}");
    public string Restart => Record.Restart.Pt();
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IChangeJournal _journal;
    private readonly ChangeOrchestrator _orch;
    private readonly TweakCatalog _catalog;
    private readonly RbmDatabase _db;
    private readonly ISystemInventory _inventory;
    public override string Key => "history";

    public ObservableCollection<ChangeRow> Changes { get; } = new();
    public ObservableCollection<string> Drift { get; } = new();
    public ObservableCollection<string> RestorePoints { get; } = new();
    public ObservableCollection<AuditEntry> Audit { get; } = new();
    public ObservableCollection<TweakChangeRecord> Imported { get; } = new();

    [ObservableProperty] private ChangeRow? _selected;
    [ObservableProperty] private string _tab = "changes";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _importInfo = "";
    [ObservableProperty] private bool _isEmpty;

    public HistoryViewModel(IChangeJournal journal, ChangeOrchestrator orch, TweakCatalog catalog, RbmDatabase db, ISystemInventory inventory)
    {
        _journal = journal; _orch = orch; _catalog = catalog; _db = db; _inventory = inventory;
        _orch.HistoryChanged += (_, _) => Ui.Post(() => _ = LoadAsync());
    }

    public override Task OnNavigatedToAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var list = await _journal.ListAsync(1000);
            Changes.Clear();
            foreach (var r in list)
                {
                    var m = _catalog.Get(r.TweakId);
                    string L(string? k) => k is null ? "valor personalizado" : m?.GetOption(k)?.Label.Get(Loc.Language) ?? k;
                    Changes.Add(new ChangeRow { Record = r, TweakName = m?.Name.Get(Loc.Language) ?? r.TweakId, FromTo = $"{L(r.FromOption)} → {L(r.ToOption)}" });
                }
            Summary = list.Count == 0 ? "" : $"{list.Count(r => r.Status is ChangeStatus.Applied or ChangeStatus.PendingRestart)} alteração(ões) em vigor · {list.Count(r => r.Status == ChangeStatus.Reverted)} desfeita(s) · {list.Count} no total";
            IsEmpty = list.Count == 0;
            Audit.Clear();
            foreach (var a in await _db.ListAuditAsync(300)) Audit.Add(a);
            RestorePoints.Clear();
            foreach (var rp in await Task.Run(RestorePointService.List)) RestorePoints.Add($"{rp.CreatedAt:g} · {rp.Description} (#{rp.SequenceNumber})");
            if (RestorePoints.Count == 0) RestorePoints.Add("Nenhum ponto listado (Proteção do Sistema desativada ou leitura requer privilégios).");
        }
        catch (Exception ex) { App.Log(ex, "Histórico"); App.Notify.Error("Histórico", ex.Message); }
    }

    [RelayCommand]
    private async Task Revert(ChangeRow? row)
    {
        row ??= Selected;
        if (row is null || !row.CanRevert) return;
        if (!await App.Dialogs.ConfirmAsync($"Reverter: {row.TweakName}", $"Restaurar os valores anteriores a {row.When}?", row.Details, "Reverter")) return;
        await RunBusy("Revertendo…", async () =>
        {
            var o = await _orch.RevertAsync(row.Record.ChangeId, false);
            if (!o.Success && o.NeedsForce)
            {
                var drift = string.Join("\n", o.Drift.Select(d => $"• {d.Display}: gravado {d.Expected}, atual {d.Actual}"));
                if (await App.Dialogs.ConfirmAsync("Alteração externa detectada", "Estes valores mudaram fora do RBM. Sobrescrever com o estado original salvo?", drift, "Sobrescrever", danger: true))
                    o = await _orch.RevertAsync(row.Record.ChangeId, true);
            }
            if (o.Success) App.Notify.Success("Revertido", o.Message); else App.Notify.Warning("Reversão não concluída", o.Message);
            await App.Services.GetRequiredService<MainViewModel>().RefreshPendingRestartAsync();
        });
    }

    [RelayCommand]
    private async Task RevertBatch(ChangeRow? row)
    {
        row ??= Selected;
        if (row?.Record.BatchId is not { } batch) return;
        var items = Changes.Where(c => c.Record.BatchId == batch && c.CanRevert).ToList();
        if (!await App.Dialogs.ConfirmAsync("Reverter lote", $"Reverter {items.Count} alteração(ões) aplicadas juntas ({row.Origin}), na ordem inversa?",
                string.Join("\n", items.Select(i => "• " + i.TweakName)), "Reverter lote")) return;
        await RunBusy("Revertendo lote…", async () =>
        {
            var res = await _orch.RevertBatchAsync(batch, false);
            int ok = res.Count(r => r.outcome.Success);
            if (ok == res.Count) App.Notify.Success("Lote revertido", $"{ok} alteração(ões) revertidas.");
            else App.Notify.Warning("Lote parcialmente revertido", $"{ok}/{res.Count}. Itens com divergência externa devem ser revertidos individualmente.");
        });
    }

    [RelayCommand]
    private async Task DetectDrift() => await RunBusy("Verificando divergências…", async () =>
    {
        var entries = await _orch.DetectDriftAsync();
        Drift.Clear();
        foreach (var e in entries)
            foreach (var d in e.Drift)
                Drift.Add($"{_catalog.Get(e.Record.TweakId)?.Name.Pt ?? e.Record.TweakId}: {d.Display} — gravado {d.Expected}, atual {d.Actual}");
        if (Drift.Count == 0) Drift.Add("Nenhuma divergência: todas as alterações ativas continuam no estado aplicado pelo RBM.");
        Tab = "drift";
    });

    [RelayCommand]
    private async Task CreateRestorePoint()
    {
        if (!await App.Dialogs.ConfirmAsync("Criar ponto de restauração", "Cria um ponto de restauração do sistema via agente elevado (UAC). O Windows limita a 1 ponto a cada 24 h por padrão. Pontos de restauração não substituem backups completos.", confirmText: "Criar")) return;
        await RunBusy("Criando ponto de restauração…", async () =>
        {
            var r = await _orch.CreateRestorePointAsync("RBM Optimizer: ponto manual");
            if (r.Success) App.Notify.Success("Ponto de restauração", r.Message); else App.Notify.Warning("Ponto de restauração", r.Message);
            await LoadAsync();
        });
    }

    [RelayCommand]
    private async Task Export()
    {
        var dlg = new SaveFileDialog { Filter = "Backup RBM (*.rbmbackup.json)|*.rbmbackup.json", FileName = $"rbm-backup-{DateTime.Now:yyyyMMdd-HHmm}.rbmbackup.json", InitialDirectory = RbmPaths.Backups };
        if (dlg.ShowDialog() != true) return;
        await RunBusy("Exportando…", async () =>
        {
            await _orch.ExportBackupAsync(dlg.FileName, _inventory.Current?.Os.Build ?? 0);
            App.Notify.Success("Backup exportado", dlg.FileName);
        });
    }

    [RelayCommand]
    private async Task Import()
    {
        var dlg = new OpenFileDialog { Filter = "Backup RBM (*.rbmbackup.json)|*.rbmbackup.json|JSON (*.json)|*.json", InitialDirectory = RbmPaths.Backups };
        if (dlg.ShowDialog() != true) return;
        await RunBusy("Validando backup…", async () =>
        {
            var r = await _orch.ImportBackupAsync(dlg.FileName);
            Imported.Clear();
            ImportInfo = r.Message;
            if (!r.Success) { App.Notify.Error("Backup inválido", r.Message); return; }
            foreach (var rec in r.Value!.Records.Where(x => x.Changes.Count > 0)) Imported.Add(rec);
            Tab = "import";
        });
    }

    [RelayCommand]
    private async Task RestoreImported(TweakChangeRecord? rec)
    {
        if (rec is null) return;
        var detail = string.Join("\n", rec.Changes.Select(c => $"• {c.Target.Display} → {c.Before}"));
        if (!await App.Dialogs.ConfirmAsync("Restaurar estado do backup", $"Restaurar os valores anteriores a '{rec.TweakId}' ({rec.At:g}) registrados no backup?", detail, "Restaurar", danger: true)) return;
        await RunBusy("Restaurando…", async () =>
        {
            var o = await _orch.RestoreImportedAsync(rec, false);
            if (!o.Success && o.NeedsForce && await App.Dialogs.ConfirmAsync("Valores atuais diferem do backup", "Os valores atuais não correspondem ao que o backup registrou como aplicado. Sobrescrever mesmo assim?",
                    string.Join("\n", o.Drift.Select(d => $"• {d.Display}: esperado {d.Expected}, atual {d.Actual}")), "Sobrescrever", danger: true))
                o = await _orch.RestoreImportedAsync(rec, true);
            if (o.Success) App.Notify.Success("Restaurado", o.Message); else App.Notify.Warning("Não restaurado", o.Message);
        });
    }

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;
}
