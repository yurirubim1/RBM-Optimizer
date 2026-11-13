using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;
using Microsoft.Data.Sqlite;

namespace Rbm.Data;

/// <summary>
/// Banco local SQLite (WAL). Modelo de "coleções de documentos JSON" — flexível para histórico de
/// alterações, benchmarks, sessões de jogo, diagnósticos, snapshots de drivers e histórico de rede.
/// Todos os dados ficam em %LOCALAPPDATA%\RbmOptimizer (nada sai da máquina).
/// </summary>
public sealed class RbmDatabase
{
    public const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Path { get; }

    public RbmDatabase(string? path = null)
    {
        Path = path ?? RbmPaths.Database;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA synchronous=NORMAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS documents (
                collection TEXT NOT NULL,
                id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                tag TEXT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY (collection, id)
            );
            CREATE INDEX IF NOT EXISTS ix_documents_coll_created ON documents(collection, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_documents_coll_tag ON documents(collection, tag);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                user TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL,
                success INTEGER NOT NULL
            );
            """);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', $v)";
        cmd.Parameters.AddWithValue("$v", SchemaVersion.ToString());
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------- Documentos ----------

    public async Task UpsertAsync<T>(string collection, string id, T doc, string? tag = null, DateTimeOffset? createdAt = null, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(doc, AgentJson.Options);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var c = Open();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO documents(collection, id, created_at, updated_at, tag, json)
                VALUES ($c, $id, $created, $now, $tag, $json)
                ON CONFLICT(collection, id) DO UPDATE SET updated_at = $now, tag = $tag, json = $json;
                """;
            var now = DateTimeOffset.Now.ToString("O");
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$created", (createdAt ?? DateTimeOffset.Now).ToString("O"));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$tag", (object?)tag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json", json);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<T?> GetAsync<T>(string collection, string id, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE collection = $c AND id = $id";
        cmd.Parameters.AddWithValue("$c", collection);
        cmd.Parameters.AddWithValue("$id", id);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is string s ? JsonSerializer.Deserialize<T>(s, AgentJson.Options) : default;
    }

    public async Task<IReadOnlyList<T>> ListAsync<T>(string collection, int limit = 500, string? tag = null, CancellationToken ct = default)
    {
        var list = new List<T>();
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = tag is null
            ? "SELECT json FROM documents WHERE collection = $c ORDER BY created_at DESC LIMIT $l"
            : "SELECT json FROM documents WHERE collection = $c AND tag = $t ORDER BY created_at DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$c", collection);
        cmd.Parameters.AddWithValue("$l", limit);
        if (tag is not null) cmd.Parameters.AddWithValue("$t", tag);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rd.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(rd.GetString(0), AgentJson.Options);
                if (item is not null) list.Add(item);
            }
            catch (JsonException) { /* documento de versão incompatível: ignorado, não derruba a lista */ }
        }
        return list;
    }

    public async Task<int> DeleteAsync(string collection, string id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var c = Open();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM documents WHERE collection = $c AND id = $id";
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<int> CountAsync(string collection, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM documents WHERE collection = $c";
        cmd.Parameters.AddWithValue("$c", collection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    // ---------- Auditoria (lado da UI) ----------

    public async Task AuditAsync(string action, string detail, bool success, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var c = Open();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO audit(at, user, action, detail, success) VALUES ($at, $u, $a, $d, $s)";
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$u", Environment.UserName);
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$d", detail);
            cmd.Parameters.AddWithValue("$s", success ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 500, CancellationToken ct = default)
    {
        var list = new List<AuditEntry>();
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT seq, at, user, action, detail, success FROM audit ORDER BY seq DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        await using var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rd.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new AuditEntry(rd.GetInt64(0), DateTimeOffset.Parse(rd.GetString(1)), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetInt32(5) == 1));
        return list;
    }

    // ---------- Configurações ----------

    internal string? GetSettingJson(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    internal void SetSettingJson(string key, string json)
    {
        _writeLock.Wait();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, json) VALUES ($k, $j) ON CONFLICT(key) DO UPDATE SET json = $j";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.ExecuteNonQuery();
        }
        finally { _writeLock.Release(); }
    }
}

public sealed record AuditEntry(long Seq, DateTimeOffset At, string User, string Action, string Detail, bool Success);

/// <summary>Nomes de coleções padronizados.</summary>
public static class Collections
{
    public const string Changes = "changes";
    public const string Benchmarks = "benchmarks";
    public const string GameSessions = "game_sessions";
    public const string Diagnostics = "diagnostics";
    public const string DriverSnapshots = "driver_snapshots";
    public const string NetworkHistory = "network_history";
    public const string LatencyReports = "latency_reports";
    public const string ProfileApplications = "profile_applications";
}
