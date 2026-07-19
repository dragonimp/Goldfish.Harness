using Microsoft.Data.Sqlite;

namespace Goldfish.Harness;

public sealed class SqliteHarnessStateStore : ISkillSessionStore, IToolExecutionStore, IDisposable
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private bool _disposed;

    public SqliteHarnessStateStore(string? databasePath = null)
    {
        _databasePath = ExpandPath(string.IsNullOrWhiteSpace(databasePath)
            ? "~/.goldfish/harness-state.db"
            : databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        Initialize(connection);
    }

    public async Task<IReadOnlyList<SkillSessionEntry>> LoadAsync(
        SkillSessionKey key,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT skill_name, loaded_at, source
            FROM goldfish_skill_sessions
            WHERE tenant_id = $tenant_id
              AND user_id = $user_id
              AND agent_id = $agent_id
              AND workspace_id = $workspace_id
              AND session_id = $session_id
            ORDER BY loaded_at ASC;
            """;
        AddSkillKeyParameters(command, key);

        var entries = new List<SkillSessionEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new SkillSessionEntry
            {
                SkillName = reader.GetString(0),
                LoadedAt = DateTimeOffset.Parse(reader.GetString(1)),
                Source = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        }

        return entries;
    }

    public async Task RecordLoadedAsync(
        SkillSessionKey key,
        SkillSessionEntry entry,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.SkillName))
        {
            return;
        }

        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goldfish_skill_sessions (
                    tenant_id, user_id, agent_id, workspace_id, session_id,
                    skill_name, loaded_at, source
                ) VALUES (
                    $tenant_id, $user_id, $agent_id, $workspace_id, $session_id,
                    $skill_name, $loaded_at, $source
                )
                ON CONFLICT(tenant_id, user_id, agent_id, workspace_id, session_id, skill_name)
                DO UPDATE SET loaded_at = excluded.loaded_at, source = excluded.source;
                """;
            AddSkillKeyParameters(command, key);
            command.Parameters.AddWithValue("$skill_name", entry.SkillName.Trim());
            command.Parameters.AddWithValue("$loaded_at", entry.LoadedAt.ToString("O"));
            command.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public async Task RecordAsync(ToolExecutionRecord record, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _writerLock.WaitAsync(ct);
        try
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goldfish_tool_executions (
                    id, run_id, session_id, tenant_id, user_id, agent_id, workspace_id,
                    step, tool_call_id, tool_id, arguments_hash, result_hash, success,
                    error, authorization_decision, started_at, completed_at
                ) VALUES (
                    $id, $run_id, $session_id, $tenant_id, $user_id, $agent_id, $workspace_id,
                    $step, $tool_call_id, $tool_id, $arguments_hash, $result_hash, $success,
                    $error, $authorization_decision, $started_at, $completed_at
                );
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("n"));
            command.Parameters.AddWithValue("$run_id", record.RunId);
            command.Parameters.AddWithValue("$session_id", record.SessionId);
            command.Parameters.AddWithValue("$tenant_id", (object?)record.TenantId ?? string.Empty);
            command.Parameters.AddWithValue("$user_id", (object?)record.UserId ?? string.Empty);
            command.Parameters.AddWithValue("$agent_id", (object?)record.AgentId ?? string.Empty);
            command.Parameters.AddWithValue("$workspace_id", (object?)record.WorkspaceId ?? string.Empty);
            command.Parameters.AddWithValue("$step", record.Step);
            command.Parameters.AddWithValue("$tool_call_id", (object?)record.ToolCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("$tool_id", record.ToolId);
            command.Parameters.AddWithValue("$arguments_hash", record.ArgumentsHash);
            command.Parameters.AddWithValue("$result_hash", (object?)record.ResultHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$success", record.Success ? 1 : 0);
            command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$authorization_decision", record.AuthorizationDecision);
            command.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("O"));
            command.Parameters.AddWithValue("$completed_at", record.CompletedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writerLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _writerLock.Dispose();
        _disposed = true;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goldfish_skill_sessions (
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                session_id TEXT NOT NULL DEFAULT '',
                skill_name TEXT NOT NULL,
                loaded_at TEXT NOT NULL,
                source TEXT NULL,
                PRIMARY KEY (tenant_id, user_id, agent_id, workspace_id, session_id, skill_name)
            );

            CREATE TABLE IF NOT EXISTS goldfish_tool_executions (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL DEFAULT '',
                tenant_id TEXT NOT NULL DEFAULT '',
                user_id TEXT NOT NULL DEFAULT '',
                agent_id TEXT NOT NULL DEFAULT '',
                workspace_id TEXT NOT NULL DEFAULT '',
                step INTEGER NOT NULL,
                tool_call_id TEXT NULL,
                tool_id TEXT NOT NULL,
                arguments_hash TEXT NOT NULL,
                result_hash TEXT NULL,
                success INTEGER NOT NULL,
                error TEXT NULL,
                authorization_decision TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_goldfish_tool_executions_session
            ON goldfish_tool_executions (
                tenant_id, user_id, agent_id, workspace_id, session_id, started_at
            );
            """;
        command.ExecuteNonQuery();
    }

    private static void AddSkillKeyParameters(SqliteCommand command, SkillSessionKey key)
    {
        command.Parameters.AddWithValue("$tenant_id", key.TenantId);
        command.Parameters.AddWithValue("$user_id", key.UserId);
        command.Parameters.AddWithValue("$agent_id", key.AgentId);
        command.Parameters.AddWithValue("$workspace_id", key.WorkspaceId);
        command.Parameters.AddWithValue("$session_id", key.SessionId);
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteHarnessStateStore));
        }
    }
}
