using System.Text.Json;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Api.Filters;
using Ark.Providers.Abstractions;
using Ark.Sync;
using Microsoft.Data.Sqlite;

namespace Ark.Api.Infrastructure;

/// <summary>
/// Ark 自身存储（SQLite 单文件 data/ark.db，裸 ADO）：连接配置 + 同步报告。
/// 启动时 CREATE TABLE IF NOT EXISTS。
/// </summary>
public sealed class ArkRepository
{
    private const string DefaultOptions = "{}";
    private readonly string _connString;
    private readonly PasswordProtector _pw;

    public ArkRepository(string dbPath, PasswordProtector pw)
    {
        _pw = pw;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureCreated();
    }

    /// <summary>连接密码保护器（仓库自持，端点直接取用）。</summary>
    public PasswordProtector Pw() => _pw;

    private void EnsureCreated()
    {
        using var conn = new SqliteConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS connections (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                dialect INTEGER NOT NULL,
                host TEXT,
                port INTEGER,
                username TEXT,
                password_enc TEXT,
                database TEXT,
                file_path TEXT,
                readonly INTEGER NOT NULL DEFAULT 0,
                options_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_reports (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                summary_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_tasks (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                profile_id TEXT,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                updated_at TEXT NOT NULL,
                state_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sync_tasks_started ON sync_tasks(started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_sync_tasks_profile ON sync_tasks(profile_id);
            CREATE TABLE IF NOT EXISTS sync_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                config_json TEXT NOT NULL,
                cron TEXT,
                schedule_enabled INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_run_at TEXT
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>供同步持久化层复用（任务/Profile 与仓库同库）。</summary>
    public SqliteConnection OpenShared() => Open();

    // ------------------------------------------------ 连接 ------------------------------------------------

    public IReadOnlyList<ConnectionEntity> ListConnections()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, dialect, host, port, username, password_enc, database, file_path, readonly, options_json, created_at, updated_at FROM connections ORDER BY name";
        using var r = cmd.ExecuteReader();
        var list = new List<ConnectionEntity>();
        while (r.Read()) list.Add(ReadConnection(r));
        return list;
    }

    public ConnectionEntity? GetConnection(Guid id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, dialect, host, port, username, password_enc, database, file_path, readonly, options_json, created_at, updated_at FROM connections WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadConnection(r) : null;
    }

    public ConnectionEntity CreateConnection(SaveConnectionRequest req, PasswordProtector pw)
    {
        var e = new ConnectionEntity(
            Guid.NewGuid(), req.Name, req.Dialect, req.Host, req.Port, req.Username,
            req.Password is null ? null : pw.Protect(req.Password),
            req.Database, req.FilePath, req.ReadOnly,
            JsonSerializer.Serialize(req.Options ?? new Dictionary<string, string>()),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connections (id, name, dialect, host, port, username, password_enc, database, file_path, readonly, options_json, created_at, updated_at)
            VALUES (@id, @name, @dialect, @host, @port, @username, @password_enc, @database, @file_path, @readonly, @options_json, @created_at, @updated_at)
            """;
        Bind(cmd, e);
        cmd.ExecuteNonQuery();
        return e;
    }

    public ConnectionEntity UpdateConnection(Guid id, SaveConnectionRequest req, PasswordProtector pw)
    {
        var existing = GetConnection(id) ?? throw ArkException.NotFound($"连接 {id} 不存在");
        var e = existing with
        {
            Name = req.Name,
            Dialect = req.Dialect,
            Host = req.Host,
            Port = req.Port,
            Username = req.Username,
            PasswordEnc = req.Password is null ? existing.PasswordEnc : pw.Protect(req.Password),
            Database = req.Database,
            FilePath = req.FilePath,
            ReadOnly = req.ReadOnly,
            OptionsJson = JsonSerializer.Serialize(req.Options ?? new Dictionary<string, string>()),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE connections SET name=@name, dialect=@dialect, host=@host, port=@port, username=@username,
                password_enc=@password_enc, database=@database, file_path=@file_path, readonly=@readonly,
                options_json=@options_json, updated_at=@updated_at
            WHERE id=@id
            """;
        Bind(cmd, e);
        cmd.ExecuteNonQuery();
        return e;
    }

    public void DeleteConnection(Guid id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM connections WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        // 0 行受影响 = 连接不存在 → 1404（重复删除报错，而不是静默成功）
        if (cmd.ExecuteNonQuery() == 0)
            throw ArkException.NotFound($"连接 {id} 不存在");
    }

    // ------------------------------------------------ 同步报告 ------------------------------------------------

    public Task SaveReportAsync(SyncTaskState state)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sync_reports (id, status, started_at, finished_at, summary_json)
            VALUES (@id, @status, @started_at, @finished_at, @summary_json)
            """;
        cmd.Parameters.AddWithValue("@id", state.Id.ToString());
        cmd.Parameters.AddWithValue("@status", state.Status.ToString());
        cmd.Parameters.AddWithValue("@started_at", state.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@finished_at", (state.FinishedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        cmd.Parameters.AddWithValue("@summary_json", JsonSerializer.Serialize(new
        {
            message = state.Message,
            error = state.Error,
            log = state.Log,
            reports = state.Reports,
        }));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public IReadOnlyList<object> ListReports(int limit = 20)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, status, started_at, finished_at, summary_json FROM sync_reports ORDER BY started_at DESC LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<object>();
        while (r.Read())
        {
            list.Add(new
            {
                id = Guid.Parse(r.GetString(0)),
                status = r.GetString(1),
                startedAt = DateTimeOffset.Parse(r.GetString(2)),
                finishedAt = r.IsDBNull(3) ? (DateTimeOffset?)null : DateTimeOffset.Parse(r.GetString(3)),
                summary = JsonSerializer.Deserialize<JsonElement>(r.GetString(4)),
            });
        }
        return list;
    }

    // ------------------------------------------------ 应用设置（KV） ------------------------------------------------

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (value is null)
        {
            cmd.CommandText = "DELETE FROM settings WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", key);
        }
        else
        {
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@k, @v)";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", value);
        }
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------ 内部 ------------------------------------------------

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    private static ConnectionEntity ReadConnection(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.GetString(1),
        (ArkDialect)r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : (int)r.GetInt64(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetInt64(9) != 0,
        r.IsDBNull(10) ? DefaultOptions : r.GetString(10),
        DateTimeOffset.Parse(r.GetString(11)),
        DateTimeOffset.Parse(r.GetString(12)));

    private static void Bind(SqliteCommand cmd, ConnectionEntity e)
    {
        cmd.Parameters.AddWithValue("@id", e.Id.ToString());
        cmd.Parameters.AddWithValue("@name", e.Name);
        cmd.Parameters.AddWithValue("@dialect", (long)e.Dialect);
        cmd.Parameters.AddWithValue("@host", (object?)e.Host ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@port", (object?)e.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@username", (object?)e.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@password_enc", (object?)e.PasswordEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@database", (object?)e.Database ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@file_path", (object?)e.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@readonly", e.ReadOnly ? 1L : 0L);
        cmd.Parameters.AddWithValue("@options_json", e.OptionsJson);
        cmd.Parameters.AddWithValue("@created_at", e.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", e.UpdatedAt.ToString("O"));
    }
}
