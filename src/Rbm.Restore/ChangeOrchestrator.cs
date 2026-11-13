using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Core.Tweaks;
using Rbm.Data;
using Rbm.Optimization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rbm.Restore;

public sealed record ApplyOutcome(TweakChangeRecord Record, bool Success, string Message);
public sealed record RevertOutcome(bool Success, IReadOnlyList<DriftReport> Drift, string Message, bool NeedsForce);
public sealed record DriftEntry(TweakChangeRecord Record, IReadOnlyList<DriftReport> Drift);

/// <summary>
/// Orquestra a aplicação/reversão de tweaks (lado da UI):
/// pré-condições → snapshot → aplicação (agente p/ alvos privilegiados, processo da UI p/ HKCU) →
/// verificação → histórico. Falha em qualquer etapa desfaz o que já foi aplicado.
/// </summary>
public sealed class ChangeOrchestrator
{
    private readonly TweakCatalog _catalog;
    private readonly TweakExecutor _executor;
    private readonly IChangeJournal _journal;
    private readonly IAgentClient _agent;
    private readonly RbmDatabase _db;
    private readonly ILogger _log;

    public ChangeOrchestrator(TweakCatalog catalog, TweakExecutor executor, IChangeJournal journal, IAgentClient agent, RbmDatabase db, ILogger<ChangeOrchestrator>? log = null)
    {
        _catalog = catalog; _executor = executor; _journal = journal; _agent = agent; _db = db;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public event EventHandler? HistoryChanged;

    /// <summary>Lê o estado real. Alvos que exigem admin para leitura (BCD) usam o agente se já estiver conectado.</summary>
    public async Task<TweakStatus> ReadStatusAsync(TweakManifest m, CancellationToken ct = default)
    {
        var local = await Task.Run(() => _executor.ReadStatus(m, elevated: false), ct).ConfigureAwait(false);
        if (local.Kind == TweakStateKind.NeedsElevationToRead && _agent.IsConnected)
        {
            var r = await _agent.InvokeAsync(AgentOps.TweakRead, new TweakReadRequest(m.Id), null, ct).ConfigureAwait(false);
            if (r.Ok && r.Read<TweakStatus>() is { } elevated) return elevated;
        }
        return local;
    }

    public async Task<ApplyOutcome> ApplyAsync(string tweakId, string optionKey, Guid? batchId = null, string? profileName = null, CancellationToken ct = default)
    {
        var m = _catalog.Get(tweakId) ?? throw new ArgumentException($"Tweak desconhecido: {tweakId}");
        var record = new TweakChangeRecord { TweakId = m.Id, ToOption = optionKey, BatchId = batchId, Restart = m.Restart, ProfileName = profileName };

        if (m.Grade == EvidenceGrade.E)
            return await Finish(record, false, ChangeStatus.FailedRolledBack, "Técnica classificada como E (obsoleta/insegura): apenas documentada, não aplicável.");
        var option = m.GetOption(optionKey);
        if (option is null)
            return await Finish(record, false, ChangeStatus.FailedRolledBack, $"Opção inexistente: {optionKey}");

        var before = await ReadStatusAsync(m, ct).ConfigureAwait(false);
        record.FromOption = before.CurrentOption;
        if (before.Kind == TweakStateKind.MatchesOption && before.CurrentOption == optionKey)
            return new ApplyOutcome(record, true, "A configuração já está neste estado. Nada foi alterado.");

        bool needsElevated = option.Targets.Any(TweakExecutor.IsElevatedScope);
        bool needsLocal = option.Targets.Any(TweakExecutor.IsUserScope);
        var elevatedChanges = new List<TargetChange>();

        if (needsElevated)
        {
            var resp = await _agent.InvokeAsync(AgentOps.TweakApply, new TweakApplyRequest(m.Id, optionKey), TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (!resp.Ok) return await Finish(record, false, ChangeStatus.FailedRolledBack, resp.Error ?? "Falha no agente.");
            var ar = resp.Read<TweakApplyResponse>()!;
            record.Changes.AddRange(ar.Changes);
            if (!ar.Success)
                return await Finish(record, false, ar.RolledBack ? ChangeStatus.FailedRolledBack : ChangeStatus.FailedPartial, ar.Error ?? "Falha na aplicação.");
            elevatedChanges = ar.Changes;
        }

        if (needsLocal)
        {
            var lr = await Task.Run(() => _executor.Apply(m, optionKey, TweakExecutor.IsUserScope, elevatedContext: false), ct).ConfigureAwait(false);
            record.Changes.AddRange(lr.Changes);
            if (!lr.Success)
            {
                // Desfaz a parte elevada para manter atomicidade.
                if (elevatedChanges.Count > 0)
                {
                    var rv = await _agent.InvokeAsync(AgentOps.TweakRevert, new TweakRevertRequest(m.Id, elevatedChanges, true), null, ct).ConfigureAwait(false);
                    var rr = rv.Ok ? rv.Read<TweakRevertResponse>() : null;
                    if (rr is not { Success: true })
                        return await Finish(record, false, ChangeStatus.FailedPartial, (lr.Error ?? "Falha") + " | Rollback da parte elevada falhou: " + (rr?.Error ?? rv.Error));
                }
                return await Finish(record, false, lr.RolledBack ? ChangeStatus.FailedRolledBack : ChangeStatus.FailedPartial, lr.Error ?? "Falha na aplicação.");
            }
        }

        var status = m.Restart == RestartRequirement.None ? ChangeStatus.Applied : ChangeStatus.PendingRestart;
        var msg = m.Restart switch
        {
            RestartRequirement.None => "Aplicado e verificado.",
            RestartRequirement.Reboot => "Aplicado e verificado. Reinicie o computador para entrar em vigor.",
            RestartRequirement.SignOut => "Aplicado e verificado. Saia e entre novamente na conta para entrar em vigor.",
            RestartRequirement.ExplorerRestart => "Aplicado e verificado. Reinicie o Explorer para entrar em vigor.",
            _ => "Aplicado e verificado. Reinicie o aplicativo afetado.",
        };
        return await Finish(record, true, status, msg);
    }

    private async Task<ApplyOutcome> Finish(TweakChangeRecord record, bool success, ChangeStatus status, string message)
    {
        record.Status = status;
        record.Error = success ? null : message;
        try
        {
            if (record.Changes.Count > 0 || !success) await _journal.SaveAsync(record).ConfigureAwait(false);
            await _db.AuditAsync(success ? "tweak.apply" : "tweak.apply.fail", $"{record.TweakId} → {record.ToOption}: {message}", success).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao gravar histórico"); }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return new ApplyOutcome(record, success, message);
    }

    public async Task<RevertOutcome> RevertAsync(Guid changeId, bool force, CancellationToken ct = default)
    {
        var rec = await _journal.GetAsync(changeId, ct).ConfigureAwait(false);
        if (rec is null) return new RevertOutcome(false, Array.Empty<DriftReport>(), "Registro não encontrado.", false);
        if (rec.Status is not (ChangeStatus.Applied or ChangeStatus.PendingRestart or ChangeStatus.FailedPartial))
            return new RevertOutcome(false, Array.Empty<DriftReport>(), "Este registro não está ativo (já revertido ou falhou sem alterações).", false);

        var all = await _journal.ListAsync(5000, ct).ConfigureAwait(false);
        var newer = all.FirstOrDefault(r => r.TweakId == rec.TweakId && r.At > rec.At && r.Status is ChangeStatus.Applied or ChangeStatus.PendingRestart);
        if (newer is not null)
            return new RevertOutcome(false, Array.Empty<DriftReport>(), $"Há uma alteração mais recente deste tweak ({newer.At:g}). Reverta-a primeiro.", false);

        var outcome = await RevertChangesAsync(rec.TweakId, rec.Changes, force, ct).ConfigureAwait(false);
        if (outcome.Success)
        {
            rec.Status = ChangeStatus.Reverted;
            rec.RevertedAt = DateTimeOffset.Now;
            await _journal.SaveAsync(rec, ct).ConfigureAwait(false);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        await _db.AuditAsync("tweak.revert", $"{rec.TweakId} ({rec.ChangeId}): {outcome.Message}", outcome.Success, ct).ConfigureAwait(false);
        return outcome;
    }

    private async Task<RevertOutcome> RevertChangesAsync(string tweakId, List<TargetChange> changes, bool force, CancellationToken ct)
    {
        var local = changes.Where(c => !c.Elevated).ToList();
        var elevated = changes.Where(c => c.Elevated).ToList();
        var drift = new List<DriftReport>();

        // Checagem prévia de divergência (sem escrever nada) para pedir confirmação antes.
        if (!force)
        {
            drift.AddRange(_executor.DetectDrift(local, elevated: false).Where(d => !IsBefore(local, d)));
            if (elevated.Count > 0 && _agent.IsConnected is false)
                drift.AddRange(_executor.DetectDrift(elevated.Where(c => !c.Target.ReadRequiresAdmin), elevated: false).Where(d => !IsBefore(elevated, d)));
            if (drift.Count > 0)
                return new RevertOutcome(false, drift, "Configurações alteradas externamente desde a aplicação. Confirme para sobrescrever.", true);
        }

        if (elevated.Count > 0)
        {
            var resp = await _agent.InvokeAsync(AgentOps.TweakRevert, new TweakRevertRequest(tweakId, elevated, force), null, ct).ConfigureAwait(false);
            if (!resp.Ok) return new RevertOutcome(false, drift, resp.Error ?? "Falha no agente.", false);
            var rr = resp.Read<TweakRevertResponse>()!;
            if (!rr.Success) return new RevertOutcome(false, rr.Drift, rr.Error ?? "Falha na reversão.", rr.Drift.Count > 0);
        }
        if (local.Count > 0)
        {
            var lr = await Task.Run(() => _executor.Revert(local, force), ct).ConfigureAwait(false);
            if (!lr.Success) return new RevertOutcome(false, lr.Drift, lr.Error ?? "Falha na reversão.", lr.Drift.Count > 0);
        }
        return new RevertOutcome(true, drift, "Revertido e verificado.", false);
    }

    private static bool IsBefore(List<TargetChange> changes, DriftReport d) =>
        changes.Any(c => c.Target.IdentityKey == d.IdentityKey && c.Before.SemanticEquals(d.Actual));

    public async Task<IReadOnlyList<ApplyOutcome>> ApplyBatchAsync(IEnumerable<(string tweakId, string option)> items, string? profileName, IProgress<(int done, int total, string current)>? progress = null, CancellationToken ct = default)
    {
        var list = items.ToList();
        var batch = Guid.NewGuid();
        var results = new List<ApplyOutcome>();
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, list.Count, list[i].tweakId));
            try { results.Add(await ApplyAsync(list[i].tweakId, list[i].option, batch, profileName, ct).ConfigureAwait(false)); }
            catch (Exception ex) { results.Add(new ApplyOutcome(new TweakChangeRecord { TweakId = list[i].tweakId, ToOption = list[i].option, BatchId = batch, Status = ChangeStatus.FailedRolledBack }, false, ex.Message)); }
        }
        progress?.Report((list.Count, list.Count, ""));
        return results;
    }

    /// <summary>Reverte um lote na ordem inversa da aplicação.</summary>
    public async Task<IReadOnlyList<(TweakChangeRecord rec, RevertOutcome outcome)>> RevertBatchAsync(Guid batchId, bool force, CancellationToken ct = default)
    {
        var all = await _journal.ListAsync(5000, ct).ConfigureAwait(false);
        var results = new List<(TweakChangeRecord, RevertOutcome)>();
        foreach (var rec in all.Where(r => r.BatchId == batchId && r.Status is ChangeStatus.Applied or ChangeStatus.PendingRestart).OrderByDescending(r => r.At))
            results.Add((rec, await RevertAsync(rec.ChangeId, force, ct).ConfigureAwait(false)));
        return results;
    }

    /// <summary>Divergências entre o estado gravado pelo RBM e o estado atual (alterações externas).</summary>
    public async Task<IReadOnlyList<DriftEntry>> DetectDriftAsync(CancellationToken ct = default)
    {
        var active = await _journal.ActiveChangesAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => active
            .Select(r => new DriftEntry(r, _executor.DetectDrift(r.Changes, elevated: false)))
            .Where(e => e.Drift.Count > 0).ToList(), ct).ConfigureAwait(false);
    }

    public async Task<OperationResult> CreateRestorePointAsync(string description, CancellationToken ct = default)
    {
        var resp = await _agent.InvokeAsync(AgentOps.RestorePointCreate, new RestorePointRequest(description), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        var ok = resp.Ok && resp.Read<OperationResult<RestorePointInfo>>() is { Success: true };
        var msg = resp.Ok ? resp.Read<OperationResult<RestorePointInfo>>()?.Message ?? "?" : resp.Error ?? "Falha.";
        await _db.AuditAsync("restorepoint.create", msg, ok, ct).ConfigureAwait(false);
        return ok ? OperationResult.Ok(msg) : OperationResult.Fail(msg);
    }

    // ---------------- Backup ----------------

    public sealed class BackupFile
    {
        public string Format { get; set; } = "rbm-backup";
        public int Version { get; set; } = 1;
        public DateTimeOffset ExportedAt { get; set; }
        public string Machine { get; set; } = "";
        public int OsBuild { get; set; }
        public List<TweakChangeRecord> Records { get; set; } = new();
        public string Sha256 { get; set; } = "";
    }

    public async Task<string> ExportBackupAsync(string path, int osBuild, CancellationToken ct = default)
    {
        var records = (await _journal.ListAsync(10000, ct).ConfigureAwait(false)).ToList();
        var file = new BackupFile { ExportedAt = DateTimeOffset.Now, Machine = Environment.MachineName, OsBuild = osBuild, Records = records };
        file.Sha256 = Hash(records);
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true });
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        await _db.AuditAsync("backup.export", path, true, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>Lê e valida um backup: integridade (SHA-256), versão, tweaks existentes e alvos pertencentes ao catálogo.</summary>
    public async Task<OperationResult<BackupFile>> ImportBackupAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 50 * 1024 * 1024) return OperationResult<BackupFile>.Fail("Arquivo inexistente ou grande demais.");
            var file = JsonSerializer.Deserialize<BackupFile>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), AgentJson.Options);
            if (file is null || file.Format != "rbm-backup" || file.Version != 1) return OperationResult<BackupFile>.Fail("Formato de backup não reconhecido.");
            if (!string.Equals(file.Sha256, Hash(file.Records), StringComparison.OrdinalIgnoreCase)) return OperationResult<BackupFile>.Fail("Integridade inválida: o conteúdo foi modificado.");
            foreach (var r in file.Records)
            {
                var m = _catalog.Get(r.TweakId);
                if (m is null) return OperationResult<BackupFile>.Fail($"Tweak desconhecido no backup: {r.TweakId}");
                var err = CatalogValidator.ValidateRevertTargets(m, r.Changes);
                if (err is not null) return OperationResult<BackupFile>.Fail($"{r.TweakId}: {err}");
            }
            await _db.AuditAsync("backup.import", path, true, ct).ConfigureAwait(false);
            return OperationResult<BackupFile>.Ok(file, $"{file.Records.Count} registros validados (exportado em {file.ExportedAt:g} de {file.Machine}).");
        }
        catch (Exception ex) { return OperationResult<BackupFile>.Fail("Backup inválido: " + ex.Message); }
    }

    /// <summary>Restaura o estado "anterior" de um registro importado (com checagem de divergência).</summary>
    public Task<RevertOutcome> RestoreImportedAsync(TweakChangeRecord rec, bool force, CancellationToken ct = default) =>
        RevertChangesAsync(rec.TweakId, rec.Changes, force, ct);

    private static string Hash(List<TweakChangeRecord> records) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(records, AgentJson.Options)));

    public bool AgentConnected => _agent.IsConnected;
    public (bool Compatible, string Reason) Compatibility(TweakManifest m, SystemProfile? p) => CompatibilityEvaluator.Evaluate(m, p);
}
