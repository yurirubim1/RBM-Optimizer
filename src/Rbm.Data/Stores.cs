using System.Collections.Concurrent;
using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Tweaks;

namespace Rbm.Data;

/// <summary>Histórico transacional de alterações de tweaks.</summary>
public sealed class ChangeJournal : IChangeJournal
{
    private readonly RbmDatabase _db;
    public ChangeJournal(RbmDatabase db) => _db = db;

    public Task SaveAsync(TweakChangeRecord record, CancellationToken ct = default) =>
        _db.UpsertAsync(Collections.Changes, record.ChangeId.ToString(), record, record.TweakId, record.At, ct);

    public Task<IReadOnlyList<TweakChangeRecord>> ListAsync(int limit = 500, CancellationToken ct = default) =>
        _db.ListAsync<TweakChangeRecord>(Collections.Changes, limit, null, ct);

    public Task<TweakChangeRecord?> GetAsync(Guid changeId, CancellationToken ct = default) =>
        _db.GetAsync<TweakChangeRecord>(Collections.Changes, changeId.ToString(), ct);

    public async Task<IReadOnlyList<TweakChangeRecord>> ActiveChangesAsync(CancellationToken ct = default)
    {
        var all = await ListAsync(5000, ct).ConfigureAwait(false);
        // O mais recente por tweak; ativo = aplicado (ou pendente de reinício) e não revertido.
        return all.GroupBy(r => r.TweakId)
                  .Select(g => g.OrderByDescending(r => r.At).First())
                  .Where(r => r.Status is ChangeStatus.Applied or ChangeStatus.PendingRestart)
                  .ToList();
    }
}

/// <summary>Configurações persistidas no SQLite com cache em memória.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly RbmDatabase _db;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    public SettingsStore(RbmDatabase db) => _db = db;

    public T Get<T>(string key, T fallback)
    {
        try
        {
            var json = _cache.TryGetValue(key, out var c) ? c : _db.GetSettingJson(key);
            if (json is null) return fallback;
            _cache[key] = json;
            return JsonSerializer.Deserialize<T>(json, AgentJson.Options) ?? fallback;
        }
        catch { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value, AgentJson.Options);
        _cache[key] = json;
        _db.SetSettingJson(key, json);
    }
}
