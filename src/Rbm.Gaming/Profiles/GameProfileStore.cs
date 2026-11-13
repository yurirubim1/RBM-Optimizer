using System.Text.Json;
using System.Text.Json.Serialization;
using Rbm.Core;
using Rbm.Gaming.Sessions;

namespace Rbm.Gaming.Profiles;

/// <summary>
/// Persistência dos perfis em JSON (padrão: RbmPaths.DataRoot\games.json). Escrita atômica: grava em arquivo
/// temporário na mesma pasta, faz flush para o disco e substitui o original (File.Replace mantém games.json.bak).
/// Arquivo corrompido é preservado como games.json.corrupt-AAAAMMDDHHMMSS e o app recomeça vazio (com aviso).
/// Thread-safe dentro do processo.
/// </summary>
public sealed class GameProfileStore
{
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN em métricas indisponíveis
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    private readonly SemaphoreSlim _lock = new(1, 1);
    public string FilePath { get; }

    /// <summary>Último aviso de carga (ex.: arquivo corrompido foi isolado).</summary>
    public string? LastLoadWarning { get; private set; }

    public GameProfileStore(string? filePath = null) =>
        FilePath = filePath ?? Path.Combine(RbmPaths.Ensure(RbmPaths.DataRoot), "games.json");

    public async Task<GameProfilesDocument> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadUnlockedAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task<GameProfilesDocument> LoadUnlockedAsync(CancellationToken ct)
    {
        LastLoadWarning = null;
        if (!File.Exists(FilePath)) return GameProfilesDocument.Empty;
        try
        {
            await using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var doc = await JsonSerializer.DeserializeAsync<GameProfilesDocument>(fs, JsonOptions, ct).ConfigureAwait(false);
            return doc is null ? GameProfilesDocument.Empty : doc with { Games = doc.Games ?? [] };
        }
        catch (JsonException ex)
        {
            string quarantine = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try { File.Move(FilePath, quarantine); } catch (IOException) { }
            LastLoadWarning = $"games.json estava corrompido ({ex.Message}); o arquivo foi preservado como {Path.GetFileName(quarantine)} e a lista recomeçou vazia.";
            return GameProfilesDocument.Empty;
        }
    }

    /// <summary>Grava o documento inteiro de forma atômica.</summary>
    public async Task SaveAsync(GameProfilesDocument doc, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await SaveUnlockedAsync(doc, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task SaveUnlockedAsync(GameProfilesDocument doc, CancellationToken ct)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(fs, doc with { Version = GameProfilesDocument.CurrentVersion }, JsonOptions, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, FilePath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <summary>Lê, aplica <paramref name="mutate"/> e grava atomicamente (sob o mesmo lock).</summary>
    public async Task<GameProfilesDocument> UpdateAsync(Func<GameProfilesDocument, GameProfilesDocument> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cur = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var next = mutate(cur);
            await SaveUnlockedAsync(next, ct).ConfigureAwait(false);
            return next;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Insere ou substitui um perfil (pelo Id).</summary>
    public Task<GameProfilesDocument> UpsertAsync(GameProfile profile, CancellationToken ct = default) =>
        UpdateAsync(d => d with
        {
            Games = d.Games.Where(g => !string.Equals(g.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
                .Append(profile with { UpdatedAt = DateTimeOffset.Now }).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        }, ct);

    public Task<GameProfilesDocument> RemoveAsync(string profileId, CancellationToken ct = default) =>
        UpdateAsync(d => d with { Games = d.Games.Where(g => !string.Equals(g.Id, profileId, StringComparison.OrdinalIgnoreCase)).ToList() }, ct);

    /// <summary>Acrescenta uma sessão ao histórico do perfil (mantém as <paramref name="maxSessions"/> mais recentes).</summary>
    public async Task<OperationResult> AddSessionAsync(string profileId, FrameSessionSummary session, int maxSessions = 200, CancellationToken ct = default)
    {
        bool found = false;
        await UpdateAsync(d => d with
        {
            Games = d.Games.Select(g =>
            {
                if (!string.Equals(g.Id, profileId, StringComparison.OrdinalIgnoreCase)) return g;
                found = true;
                var list = g.Sessions.Where(s => s.Id != session.Id).Append(session).OrderBy(s => s.Start).ToList();
                if (list.Count > maxSessions) list = list.Skip(list.Count - maxSessions).ToList();
                return g with { Sessions = list, UpdatedAt = DateTimeOffset.Now };
            }).ToList(),
        }, ct).ConfigureAwait(false);
        return found ? OperationResult.Ok("Sessão salva.") : OperationResult.Fail("Perfil de jogo não encontrado.", "not_found");
    }
}
